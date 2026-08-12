using Tmds.DBus.Protocol;

namespace IAGrim.App;

/// <summary>
/// A system tray icon, via the StatusNotifierItem protocol.
///
/// **Why D-Bus and not a library.** Photino has no tray API, and there is no
/// libappindicator/ayatana on this system to P/Invoke. StatusNotifierItem is the protocol every
/// modern desktop tray actually speaks, so it is implemented directly. KDE runs the watcher
/// (<c>org.kde.StatusNotifierWatcher</c>) and a host in plasmashell; GNOME needs an extension,
/// and some compositors have neither.
///
/// **It is optional by construction.** Every failure path — no session bus, no watcher, a
/// refused name — returns null and the application carries on exactly as it would without a
/// tray. A tray icon is a convenience; it must never be the reason the app does not start.
///
/// Deliberately minimal: an icon, a tooltip, and left-click. There is no context menu, because
/// menus are a second protocol (<c>com.canonical.dbusmenu</c>) with its own layout tree and
/// event plumbing, and the value of Show/Quit entries does not justify it here — closing the
/// window still quits, which is the behaviour we want anyway.
/// </summary>
public sealed class TrayIcon : IPathMethodHandler, IDisposable {
    private const string ItemPath = "/StatusNotifierItem";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string WatcherService = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";

    private readonly DBusConnection _connection;
    private readonly string _iconName;
    private readonly string _title;
    private readonly Action _onActivate;

    public string Path => ItemPath;
    public bool HandlesChildPaths => false;

    private TrayIcon(DBusConnection connection, string iconName, string title, Action onActivate) {
        _connection = connection;
        _iconName = iconName;
        _title = title;
        _onActivate = onActivate;
    }

    /// <summary>
    /// Registers a tray icon, or returns null when this desktop has nowhere to put one.
    /// </summary>
    /// <param name="iconName">
    /// An icon *theme* name, not a path — the tray looks it up in the icon theme, which is why
    /// `iagd install-desktop` puts the icon in ~/.local/share/icons/hicolor.
    /// </param>
    /// <param name="onActivate">Invoked on left-click. Called on a D-Bus thread.</param>
    public static async Task<TrayIcon?> TryCreateAsync(string iconName, string title, Action onActivate) {
        try {
            if (string.IsNullOrEmpty(DBusAddress.Session)) return null;   // no session bus

            var connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync();

            var tray = new TrayIcon(connection, iconName, title, onActivate);

            // The name is by convention org.kde.StatusNotifierItem-<pid>-<instance>; the watcher
            // reads the pid back out of it.
            var service = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
            if (!await connection.TryRequestNameAsync(service, RequestNameOptions.None)) {
                Debug($"could not take the bus name {service}");
                connection.Dispose();
                return null;
            }

            connection.AddMethodHandler(tray);

            // Registering is what actually makes the icon appear. If no watcher owns the name,
            // this throws — which is the "GNOME without an extension" case, and is not an error
            // worth reporting as one.
            await connection.CallMethodAsync(BuildRegisterCall(connection, service));

            Debug($"registered as {service}");
            return tray;
        }
        catch (Exception ex) {
            // Failing to get a tray is normal on some desktops, so this is silent by default.
            // It is still the kind of thing worth being able to see on demand.
            Debug($"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds the RegisterStatusNotifierItem call.
    ///
    /// Written out rather than routed through a general "make a call" helper taking a
    /// <c>Action&lt;MessageWriter&gt;</c>: <see cref="MessageWriter"/> is a mutable struct, so a
    /// delegate receives a *copy* and everything it writes is discarded when the copy goes out
    /// of scope. The result is a message whose header promises a string argument and whose body
    /// is empty — which the bus answers by closing the connection, some distance from the cause.
    ///
    /// The writer also cannot cross an await, which is why the buffer is built in full here and
    /// sent by the caller.
    /// </summary>
    /// <summary>
    /// Tray diagnostics, off unless <c>IAGD_DEBUG_TRAY=1</c>.
    ///
    /// Worth keeping: a tray that does not appear has several indistinguishable causes — no
    /// session bus, no watcher on this desktop, the name refused, or a reply the bus rejected —
    /// and none of them produce a visible symptom beyond a missing icon.
    /// </summary>
    private static void Debug(string message) {
        if (Environment.GetEnvironmentVariable("IAGD_DEBUG_TRAY") == "1") {
            Console.Error.WriteLine($"tray: {message}");
        }
    }

    private static MessageBuffer BuildRegisterCall(DBusConnection connection, string service) {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(WatcherService, WatcherPath, WatcherService,
                                     "RegisterStatusNotifierItem", "s");
        writer.WriteString(service);
        return writer.CreateMessage();
    }

    /// <summary>
    /// Answers the tray host's calls: the properties it reads to draw the icon, and the clicks
    /// it forwards back.
    /// </summary>
    public ValueTask HandleMethodAsync(MethodContext context) {
        var request = context.Request;

        switch (request.InterfaceAsString) {
            case "org.freedesktop.DBus.Properties":
                HandleProperties(context);
                return default;

            case ItemInterface:
                switch (request.MemberAsString) {
                    case "Activate":
                        // Left click. The handler talks to the window, which lives on another
                        // thread, so it must not block this one.
                        Task.Run(_onActivate);
                        Reply(context, null);
                        return default;

                    // Middle click and right click. Answered rather than ignored so the host
                    // does not log an error for each one; without a menu there is nothing
                    // sensible to do.
                    case "SecondaryActivate":
                    case "ContextMenu":
                    case "Scroll":
                        Reply(context, null);
                        return default;
                }
                break;
        }

        if (!context.ReplySent) context.ReplyUnknownMethodError();
        return default;
    }

    private void HandleProperties(MethodContext context) {
        var reader = context.Request.GetBodyReader();
        reader.ReadString();   // the interface being queried; we only serve the one

        switch (context.Request.MemberAsString) {
            case "Get": {
                var name = reader.ReadString();
                using var writer = context.CreateReplyWriter("v");
                writer.WriteVariant(PropertyValue(name));
                context.Reply(writer.CreateMessage());
                return;
            }

            case "GetAll": {
                using var writer = context.CreateReplyWriter("a{sv}");
                writer.WriteDictionary(Properties());
                context.Reply(writer.CreateMessage());
                return;
            }
        }

        context.ReplyUnknownMethodError();
    }

    /// <summary>
    /// The properties a tray host reads to draw the item.
    ///
    /// Built through <see cref="VariantValue"/> and written with the library's own dictionary
    /// support rather than by hand. Hand-writing the <c>a{sv}</c> body is where this first went
    /// wrong, and the failure mode is unhelpful: the bus rejects a malformed reply by closing
    /// the connection, so the symptom is an icon that registers and then vanishes, with the
    /// error surfacing on whatever call happens to be in flight rather than on the bad reply.
    /// </summary>
    private Dictionary<string, VariantValue> Properties() => new() {
        // ApplicationStatus rather than Communications: this is an app that is running, not
        // something with unread messages, and trays sort the two differently.
        ["Category"]   = VariantValue.String("ApplicationStatus"),
        ["Id"]         = VariantValue.String("iagd"),
        ["Title"]      = VariantValue.String(_title),
        ["Status"]     = VariantValue.String("Active"),
        // An icon *theme* name; `iagd install-desktop` is what puts it where this resolves.
        ["IconName"]   = VariantValue.String(_iconName),
        // False means "send me Activate on click" rather than "I am a menu".
        ["ItemIsMenu"] = VariantValue.Bool(false),
    };

    private VariantValue PropertyValue(string name) =>
        Properties().TryGetValue(name, out var value) ? value : VariantValue.String(string.Empty);

    private static void Reply(MethodContext context, string? signature) {
        if (context.NoReplyExpected || context.ReplySent) return;
        using var writer = context.CreateReplyWriter(signature);
        context.Reply(writer.CreateMessage());
    }

    public void Dispose() {
        try { _connection.Dispose(); }
        catch (Exception) { /* going away anyway */ }
    }
}
