using System.Text.Json;
using System.Text.Json.Serialization;

namespace IAGrim.Platform;

/// <summary>
/// This port's own settings, in <c>~/.config/iagd-linux/settings.json</c>.
///
/// Deliberately separate from the bridge's <c>settings.json</c> inside the Wine prefix. That
/// file is upstream's, and the hook DLL reads four keys out of it; it also holds a real
/// install's cloud credentials. Writing our preferences into someone's prefix would mix two
/// unrelated lifetimes — see <see cref="BridgeSettings"/>, which touches only the keys the hook
/// needs and preserves everything else.
///
/// Upstream splits its settings into "local" (machine-specific, discarded when the machine name
/// changes) and "persistent"; the split exists to support its cloud sync. This port has one flat
/// object, and <see cref="ICloudSettings"/> records which side of that split each cloud key
/// belongs to instead.
/// </summary>
public sealed class AppSettings : ICloudSettings {
    /// <summary>
    /// Stash tab to take deposited items from. 0 means the last tab, matching the hook's own
    /// default — see SettingsReader.cpp.
    /// </summary>
    public int StashToLootFrom { get; set; }

    /// <summary>Stash tab to place transferred items into. 0 means the last tab.</summary>
    public int StashToDepositTo { get; set; }

    /// <summary>
    /// Language code for Grim Dawn's text archives, e.g. "DE". Determines which
    /// <c>Text_XX.arc</c> is read. Defaults to English.
    /// </summary>
    public string Language { get; set; } = "EN";

    /// <summary>
    /// Grim Dawn install directory, when auto-discovery gets it wrong or the game lives
    /// somewhere unusual. Null means "discover it".
    /// </summary>
    public string? GameDir { get; set; }

    /// <summary>
    /// Grim Dawn's Proton prefix, when auto-discovery does not find it. Null means "discover it".
    ///
    /// This has no upstream counterpart, because upstream is a Windows program talking to a hook
    /// in another Windows process: the two simply agree on %LOCALAPPDATA%. Here the same
    /// directory lives inside a Wine prefix, and if this port cannot find that prefix there is no
    /// channel to the hook at all — nothing is looted and nothing can be transferred back. It is
    /// the Linux half of the same setting as <see cref="GameDir"/>, and needs to be settable for
    /// the same reason: discovery only knows the layout Steam happens to use.
    ///
    /// Either the compatdata folder or the pfx inside it is accepted; see
    /// <see cref="PrefixBridge.ForPrefix"/>.
    /// </summary>
    public string? PrefixDir { get; set; }

    /// <summary>
    /// Collection database to use, instead of the one in ~/.local/share/iagd-linux.
    ///
    /// Points at an existing IAGD database — a Windows install's, one inside a Wine prefix, or a
    /// second collection kept elsewhere. The schema here is upstream's, so such a file opens
    /// directly rather than needing an import. Null means the default location.
    /// </summary>
    public string? DatabaseFile { get; set; }

    /// <summary>
    /// Attach the hook automatically when Grim Dawn is detected.
    ///
    /// On by default: without it, capturing loot needs a terminal and a script run at the right
    /// moment, which is a poor answer to "why is nothing being captured". Retrying is safe —
    /// the hook unloads itself when it declines and refuses a second copy — and the pacing is in
    /// <see cref="AutoAttachService"/>.
    /// </summary>
    public bool AutoAttach { get; set; } = true;

    /// <summary>
    /// Whether an item may be transferred into a different mod than it was looted from.
    /// Upstream's <c>transferAnyMod</c>; off, because crossing that boundary is usually a
    /// mistake rather than an intention.
    /// </summary>
    public bool TransferAnyMod { get; set; }

    /// <summary>
    /// Whether an item's granted-skill block is left off its card and detail panel. Upstream's
    /// <c>HideSkills</c>, bound to the "Hide Skills" checkbox in its settings window and applied
    /// by skipping <c>ApplySkills</c> in ItemStatService.
    ///
    /// Upstream's own default hides the block on a fresh install — its backing field defaults to
    /// true, not false. This port has never had the toggle at all and has always drawn the
    /// block, so copying that default would silently hide skills for every existing user the
    /// moment they update. Defaulting to false keeps what they already see; the box is there for
    /// anyone who wants upstream's default instead.
    /// </summary>
    public bool HideSkills { get; set; }

    // ------------------------------------------------------------------- online sync
    //
    // See ICloudSettings for what each of these means and which of upstream's two settings
    // files it comes from. They live here rather than in a separate file because the settings
    // file is one document, and a second one would be a second thing to back up.

    /// <inheritdoc />
    public string? CloudUser { get; set; }

    /// <inheritdoc />
    public string? CloudAuthToken { get; set; }

    /// <inheritdoc />
    public long CloudUploadTimestamp { get; set; }

    /// <inheritdoc />
    public bool UsingDualComputer { get; set; }

    /// <inheritdoc />
    public long? BuddySyncUserIdV3 { get; set; }

    /// <inheritdoc />
    public bool OptOutOfBackups { get; set; }

    /// <inheritdoc />
    public DateTime LastCharSyncUtc { get; set; }

    /// <summary>
    /// This installation's own identity, minted once and then never changed.
    ///
    /// Upstream keeps one of these too (<c>persistent.uuid</c>) but only ever sends it with a
    /// crash report, which this port does not do. Here it rides along in the user agent, so a
    /// run of the port is attributable to an installation rather than to Linux in general.
    ///
    /// It lives in this file specifically so that it survives what a machine identifier must
    /// survive: rebuilding, reinstalling the AppImage, and Steam recreating the Wine prefix.
    /// Nothing derives it from the hardware, so copying this file to another machine copies the
    /// identity, which is the same bargain upstream makes.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Copies the keys no settings form owns across from <paramref name="stored"/>.
    ///
    /// The settings page sends back the object it was given, and what it was given has never
    /// included these: they belong to the Online tab, which has its own endpoints, and one of
    /// them is a session token. Without this, a request that simply omits them is indistinguishable
    /// from one asking for the defaults — so saving a stash tab index silently signed the user out.
    ///
    /// A whitelist rather than a merge because the direction matters: the caller's object is the
    /// new preferences, and these are state it has no business restating.
    /// </summary>
    public void CarryOverUnmanaged(AppSettings stored) {
        CloudUser = stored.CloudUser;
        CloudAuthToken = stored.CloudAuthToken;
        CloudUploadTimestamp = stored.CloudUploadTimestamp;
        UsingDualComputer = stored.UsingDualComputer;
        BuddySyncUserIdV3 = stored.BuddySyncUserIdV3;
        OptOutOfBackups = stored.OptOutOfBackups;
        LastCharSyncUtc = stored.LastCharSyncUtc;

        // Minted once and then never changed: a new one would make this installation look like a
        // different one to the sync service.
        ClientId = stored.ClientId;
    }

    // ---------------------------------------------------------------- persistence

    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Reads the settings, falling back to defaults for anything missing or unreadable.
    ///
    /// A corrupt settings file must not stop the application starting: the collection is the
    /// thing of value, and defaults are all recoverable by changing them back.
    /// </summary>
    public static AppSettings Load(string? path = null) {
        path ??= LinuxPaths.SettingsFile;
        try {
            if (!File.Exists(path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json)
                   ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"warning: could not read settings ({ex.Message}); using defaults.");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes the settings via a temporary file and a rename, so an interrupted write leaves the
    /// previous version rather than a truncated one.
    /// </summary>
    public void Save(string? path = null) {
        path ??= LinuxPaths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// <see cref="ICloudSettings.Save"/>. Explicit because the public <see cref="Save(string?)"/>
    /// takes an optional path and so does not implement a parameterless method.
    /// </summary>
    void ICloudSettings.Save() => Save();
}
