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
