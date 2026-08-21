using System.Net;
using System.Net.Sockets;
using IAGrim.Host;
using IAGrim.Platform;
using Photino.NET;

namespace IAGrim.App;

/// <summary>
/// The desktop application: the local host, plus a native window pointed at it.
///
/// The window is a WebKitGTK view via Photino, and the UI inside it is the same Preact app the
/// host serves over HTTP. That is deliberate — running `iagd-host` and opening a browser gives
/// exactly the same interface, which is what makes the headless path a real option rather than
/// a degraded one.
/// </summary>
internal static class Program {
    /// <summary>
    /// Default port. Chosen high and fixed rather than random so a browser bookmark keeps
    /// working across restarts.
    /// </summary>
    private const int DefaultPort = 5680;

    [STAThread]
    private static int Main(string[] args) {
        ConfigureWebViewBackend();
        SetApplicationId();

        if (!Startup.SelectDatabase(args, AppSettings.Load(), Console.Out)) return 1;

        var port = ParsePort(args) ?? DefaultPort;

        // An already-running instance owns the port. Rather than failing, point the new window
        // at the existing host — the second launch behaves like "show me the window", which is
        // what clicking a desktop icon twice should do.
        if (IsPortInUse(port)) {
            Console.WriteLine($"iagd is already running on port {port}; opening a window onto it.");
            ShowWindow($"http://127.0.0.1:{port}/", null);
            return 0;
        }

        HostServer server;
        try {
            server = new HostServer(port);
            server.Start();
        }
        catch (HttpListenerException ex) {
            Console.Error.WriteLine($"error: could not listen on port {port}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"iagd — host on {server.Url}, database {LinuxPaths.DatabaseFile}");

        try {
            ShowWindow(server.Url, server.DiscoveryWarning, server);
        }
        finally {
            // The window loop has returned, so the user closed it. Shut the host down with it:
            // a background server with no window is a process nobody knows how to stop.
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return 0;
    }

    /// <summary>
    /// Picks a GTK backend before anything touches Photino, because GTK reads these at
    /// <c>gtk_init</c> and ignores them afterwards.
    ///
    /// **What this is actually for.** Photino's GTK3 layer once died during window creation on
    /// a Wayland session — <c>Gdk-Message: Error 71 (Protocol error)</c>, taking the host down
    /// with it — and XWayland was the workaround. The real culprit turned out to be WebKitGTK's
    /// DMA-BUF renderer, disabled below: with that off, the window comes up on Wayland
    /// natively, which is what this session actually runs.
    ///
    /// The backend preference is left in place for sessions where the renderer is not the
    /// problem, and it defers to an explicit <c>GDK_BACKEND</c> — which KDE sets to "wayland"
    /// for its own session, so in practice that is the branch taken here.
    /// </summary>
    private static void ConfigureWebViewBackend() {
        var onWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        var hasX11 = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GDK_BACKEND")) && onWayland && hasX11) {
            SetNativeEnvironment("GDK_BACKEND", "x11");
        }

        // WebKitGTK's DMA-BUF renderer fails on a number of drivers and in VMs, printing
        // "Failed to create GBM buffer" and sometimes rendering nothing at all. The fallback
        // path is slower but correct, and this UI is a list of item icons rather than a game.
        if (Environment.GetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER") is null) {
            SetNativeEnvironment("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        }
    }

    /// <summary>
    /// Sets a variable in the *process* environment, not just the managed view of it.
    ///
    /// <see cref="Environment.SetEnvironmentVariable"/> does not call <c>setenv</c> on Unix —
    /// it updates a managed dictionary, so native code reading <c>getenv</c> never sees the
    /// change. GTK reads <c>GDK_BACKEND</c> through <c>getenv</c>, so the managed call looks
    /// like it works and silently does nothing.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);

    /// <summary>
    /// GLib's program name. GTK uses it as the Wayland <c>app_id</c>, which is the *only* thing
    /// a Wayland compositor has to identify a window by.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("libglib-2.0.so.0")]
    private static extern void g_set_prgname(string prgname);

    [System.Runtime.InteropServices.DllImport("libglib-2.0.so.0")]
    private static extern IntPtr g_get_prgname();

    /// <summary>The X11 equivalent: the class half of WM_CLASS.</summary>
    [System.Runtime.InteropServices.DllImport("libgdk-3.so.0")]
    private static extern void gdk_set_program_class(string programClass);

    /// <summary>
    /// Tells the desktop which application this window belongs to, so it can find the icon.
    ///
    /// **Setting the window's icon is not enough, and on Wayland does nothing at all.** Wayland
    /// has no per-window icon; a compositor identifies a window by its <c>app_id</c> and looks
    /// up the matching <c>.desktop</c> file for the name and icon. GTK derives that app_id from
    /// GLib's program name, which for a .NET application is whatever the host process happens to
    /// be called — so it is set explicitly here to match <c>iagd.desktop</c>.
    ///
    /// Must run before the first window is created; GTK reads it when the surface is made.
    /// X11 gets the same treatment through the WM_CLASS, for sessions that are not Wayland.
    /// </summary>
    private static void SetApplicationId() {
        const string id = "iagd";
        try {
            g_set_prgname(id);
            if (Environment.GetEnvironmentVariable("IAGD_DEBUG_TRAY") == "1") {
                var read = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(g_get_prgname());
                Console.Error.WriteLine($"app-id: g_set_prgname -> \"{read}\"");
            }
        }
        catch (Exception ex) {
            Console.Error.WriteLine($"app-id: could not set the program name: {ex.GetType().Name}");
        }
        try { gdk_set_program_class(id); }
        catch (DllNotFoundException) { /* GDK not loadable under this name; X11 only anyway */ }
    }

    private static void SetNativeEnvironment(string name, string value) {
        Environment.SetEnvironmentVariable(name, value);   // keep the managed view consistent
        try {
            setenv(name, value, 1);
        }
        catch (DllNotFoundException) {
            // Not glibc, or not Linux. The window may still come up; if it does not, the
            // variable can be set from the shell instead.
            Console.Error.WriteLine($"warning: could not set {name}; set it in the environment if the window fails to open.");
        }
    }

    /// <summary>
    /// The window icon, shipped beside the executable.
    ///
    /// Without it the taskbar shows a generic placeholder: this runs under XWayland, where the
    /// compositor cannot match the window to a .desktop file and so has nothing else to go on.
    /// Setting it explicitly gives X11 an icon hint, which is what the panel reads.
    /// </summary>
    private static string? FindIcon() {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "iagd.png");
        return File.Exists(path) ? path : null;
    }

    private static void ShowWindow(string url, string? warning, HostServer? server = null) {
        var icon = FindIcon();

        var window = new PhotinoWindow()
            .SetTitle("Item Assistant for Grim Dawn")
            .SetUseOsDefaultSize(false)
            .SetSize(1280, 820)
            .SetMinSize(720, 480)
            // Photino's native SetMinSize passes GDK_HINT_MIN_SIZE and GDK_HINT_MAX_SIZE in one
            // gtk_window_set_geometry_hints call, so asking for a minimum publishes a maximum
            // too — int.MaxValue, whatever we do. On a display at 200% scale that value
            // overflows on its way through GTK and reaches the compositor as a single pixel: a
            // maximum below the minimum, which KWin reads as "this window cannot be resized".
            // Measured on a 200% SteamOS session, the window arrived pinned at 722x509 with the
            // maximize button gone. A finite maximum no monitor will reach survives being
            // multiplied by any scale factor.
            .SetMaxSize(16384, 16384)
            .SetUseOsDefaultLocation(false)
            .Center()
            // No right-click menu: it is WebKit's ("Reload", "Open Link"), which reads as a
            // browser leaking through an application window.
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(Environment.GetEnvironmentVariable("IAGD_DEVTOOLS") == "1");

        if (icon is not null) window.SetIconFile(icon);

        // Whether the window is minimized, as far as this process knows.
        //
        // Tracked rather than asked, because PhotinoWindow.Minimized cannot be relied on: under
        // a Wayland session it reads false however many times SetMinimized(true) has been called
        // and whatever the window is actually doing. The tray toggle used to ask it, which on
        // Wayland meant "restore" was unreachable — every click computed !false and minimized
        // again. Under X11 the same getter is correct, so this was invisible to anyone testing
        // there. The window's own events keep this honest when the state changes without us.
        var minimized = false;

        window.RegisterMinimizedHandler((_, _) => minimized = true);
        window.RegisterRestoredHandler((_, _) => minimized = false);

        // Opening minimized, for the case this setting exists to serve: the app launched
        // alongside the game rather than instead of it.
        //
        // Set from the created handler because minimizing is a request to a window manager about
        // a window that has to exist first. Verified under X11, where the window arrives with
        // WM_STATE Iconic and _NET_WM_STATE_HIDDEN; a Wayland compositor may decline, and gives
        // no way to find out that it has. Photino minimizes rather than hides — it has no API
        // for hiding at all — so this leaves an entry in the taskbar, which is upstream's
        // behaviour too whenever MinimizeToTray is off. See BACKLOG entry 7.
        if (AppSettings.Load().StartMinimized) {
            window.RegisterWindowCreatedHandler((_, _) => {
                try {
                    window.SetMinimized(true);
                    minimized = true;
                    // Said out loud because the alternative is an application that appears not to
                    // have started. Someone who set this weeks ago and forgot has no other clue.
                    Console.WriteLine("note: starting minimized; the tray icon brings the window back.");
                }
                catch (Exception ex) {
                    // A window manager that refuses is not a reason to fail to start.
                    Console.Error.WriteLine($"note: could not start minimized ({ex.Message}).");
                }
            });
        }

        // A tray icon, where the desktop has somewhere to put one. Left-click toggles the
        // window, which is the whole feature: this sits beside a fullscreen game and gets
        // alt-tabbed to for a few seconds at a time.
        //
        // Closing the window still quits the application. That is deliberate — a background
        // process with no window is one nobody knows how to stop, and a tray icon is a thin
        // excuse for introducing one.
        var tray = TrayIcon.TryCreateAsync(
            iconName: "iagd",
            title: "Item Assistant for Grim Dawn",
            onActivate: () => {
                try {
                    minimized = !minimized;
                    window.SetMinimized(minimized);
                }
                catch (Exception) { /* window already gone */ }
            }).GetAwaiter().GetResult();

        if (tray is null) {
            // Not an error: GNOME without an extension, or a session with no tray at all.
            Console.WriteLine("note: no system tray available; running without a tray icon.");
        }

        if (warning is not null) {
            // Discovery failed, so the UI will show its own explanation once loaded. Log it
            // too: if the window itself fails to come up, this is the only trace.
            Console.Error.WriteLine($"warning: {warning}");
        }

        // Native file chooser for the settings page. Dialogs are GTK, so they have to run on
        // the thread that owns the window; the request arrives on a listener thread.
        if (server is not null) {
            server.FilePicker = (directory, title, start) => {
                string? result = null;
                window.Invoke(() => {
                    var chosen = directory
                        ? window.ShowOpenFolder(title, start, false)
                        : window.ShowOpenFile(title, start, false, []);
                    result = chosen?.FirstOrDefault();
                });
                return result;
            };
        }

        window.Load(new Uri(url));
        window.WaitForClose();

        tray?.Dispose();
    }

    private static int? ParsePort(string[] args) {
        for (var i = 0; i < args.Length - 1; i++) {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsed)) return parsed;
        }
        return null;
    }

    /// <summary>
    /// Whether something already holds the loopback port. Tested by binding rather than by
    /// connecting: a connect test cannot distinguish "nothing there" from "there but busy".
    /// </summary>
    private static bool IsPortInUse(int port) {
        try {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return false;
        }
        catch (SocketException) {
            return true;
        }
    }
}
