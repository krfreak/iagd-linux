namespace IAGrim.Platform;

/// <summary>
/// Replaces upstream's <c>IAGrim/Utilities/GlobalPaths.cs</c>, which builds everything under
/// <c>%LOCALAPPDATA%\EvilSoft\IAGD</c> via <c>SHGetKnownFolderPath</c>.
///
/// The split matters. Two different directories are in play and conflating them is how the
/// port would go wrong:
///
///   * <b>Native storage</b> — our database, settings, extracted icons and logs. XDG
///     directories on the Linux side, nothing to do with Wine.
///   * <b>The bridge</b> — the directory shared with the injected hook DLL. Its location is
///     not ours to choose: the DLL derives it itself from
///     <c>SHGetKnownFolderPath(RoamingAppData)</c> inside the prefix
///     (<c>HookDll/Hook/HookLog.cpp</c>), so we must follow it there.
/// </summary>
public static class LinuxPaths {
    private const string AppName = "iagd-linux";

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string XdgOr(string variable, string fallback) {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? Path.Combine(Home, fallback) : value;
    }

    /// <summary>~/.local/share/iagd-linux — database, extracted icons, backups.</summary>
    public static string DataDir => Ensure(Path.Combine(XdgOr("XDG_DATA_HOME", ".local/share"), AppName));

    /// <summary>~/.config/iagd-linux — settings.</summary>
    public static string ConfigDir => Ensure(Path.Combine(XdgOr("XDG_CONFIG_HOME", ".config"), AppName));

    /// <summary>~/.cache/iagd-linux — regenerable data only.</summary>
    public static string CacheDir => Ensure(Path.Combine(XdgOr("XDG_CACHE_HOME", ".cache"), AppName));

    /// <summary>~/.local/state/iagd-linux — logs.</summary>
    public static string StateDir => Ensure(Path.Combine(XdgOr("XDG_STATE_HOME", ".local/state"), AppName));

    private static string? _databaseOverride;

    /// <summary>
    /// The collection database in use.
    ///
    /// Overridable because this port writes upstream's schema: an existing IAGD database — from
    /// a Windows install, a Wine prefix, or simply a second collection — can be opened directly.
    /// The override is process-wide and set once during startup, which is why it is here rather
    /// than threaded through the twenty-odd places that open the database.
    /// </summary>
    public static string DatabaseFile => _databaseOverride ?? Path.Combine(DataDir, "userdata.db");

    /// <summary>Whether the database in use is somewhere other than the default location.</summary>
    public static bool IsDatabaseOverridden => _databaseOverride is not null;

    /// <summary>
    /// Chooses the database, most specific source first: an explicit <c>--database</c>, the
    /// <c>IAGD_DATABASE</c> environment variable, then the saved setting.
    /// </summary>
    /// <returns>A message worth printing, or null when the default is in use.</returns>
    public static string? ResolveDatabase(string[] args, string? fromSettings) {
        string? chosen = null;
        string? source = null;

        for (var i = 0; i < args.Length - 1; i++) {
            if (args[i] is "--database" or "--db") {
                chosen = args[i + 1];
                source = "--database";
                break;
            }
        }

        if (chosen is null) {
            var fromEnvironment = Environment.GetEnvironmentVariable("IAGD_DATABASE");
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) {
                chosen = fromEnvironment;
                source = "IAGD_DATABASE";
            }
        }

        if (chosen is null && !string.IsNullOrWhiteSpace(fromSettings)) {
            chosen = fromSettings;
            source = "settings";
        }

        if (chosen is null) return null;

        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(
            chosen.StartsWith('~') ? Path.Combine(Home, chosen[1..].TrimStart('/')) : chosen));

        _databaseOverride = full;
        return $"using database {full} (from {source})";
    }

    /// <summary>Restores the default, for tests.</summary>
    public static void UseDefaultDatabase() => _databaseOverride = null;
    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");

    /// <summary>Item icons extracted from the game's ARC files.</summary>
    public static string IconDir => Ensure(Path.Combine(DataDir, "storage"));

    /// <summary>
    /// Copies of loot CSVs, kept for a few days after they are safely in the database — see
    /// <see cref="LootWatcher.PruneBackups"/>, which is the only thing that empties this.
    /// </summary>
    public static string LootBackupDir => Ensure(Path.Combine(DataDir, "loot-backup"));

    /// <summary>
    /// Point-in-time copies of the collection database. Separate from <see cref="LootBackupDir"/>,
    /// which holds the raw loot files as captured — those are the inputs, these are the result.
    /// </summary>
    public static string BackupDir => Ensure(Path.Combine(DataDir, "backup"));

    private static string Ensure(string path) {
        Directory.CreateDirectory(path);
        return path;
    }
}
