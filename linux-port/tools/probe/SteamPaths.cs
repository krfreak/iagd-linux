namespace Iagd.Probe;

/// <summary>
/// Locates everything the host needs, natively, with no Windows API and no Wine call.
/// This is the seed of IAGrim.Platform and replaces upstream's registry lookups in
/// IAGrim/Utilities/Detection/GrimDawnDetector.cs.
/// </summary>
internal sealed class SteamPaths {
    public const string GrimDawnAppId = "219990";

    public required string SteamRoot { get; init; }
    public required IReadOnlyList<string> Libraries { get; init; }
    public string? GameDir { get; init; }
    public string? PrefixDir { get; init; }
    public string? SavePath { get; init; }
    public string? SaveSource { get; init; }

    /// <summary>
    /// The shared IPC directory. The hook DLL builds this path itself via
    /// SHGetKnownFolderPath(RoamingAppData) + "\..\local\evilsoft\iagd\"
    /// (HookDll/Hook/HookLog.cpp:10), which under Proton lands inside the prefix.
    /// </summary>
    public string? BridgeDir => PrefixDir is null
        ? null
        : Path.Combine(PrefixDir, "drive_c", "users", "steamuser",
                       "AppData", "Local", "EvilSoft", "IAGD");

    private static readonly string[] SteamRootCandidates = [
        "~/.local/share/Steam",
        "~/.steam/steam",
        "~/.steam/debian-installation",
        "~/.var/app/com.valvesoftware.Steam/data/Steam",
    ];

    public static SteamPaths Discover() {
        var root = SteamRootCandidates
            .Select(Expand)
            .FirstOrDefault(p => Directory.Exists(Path.Combine(p, "steamapps")))
            ?? throw new DirectoryNotFoundException(
                "No Steam installation found. Tried: " + string.Join(", ", SteamRootCandidates));

        var libraries = ReadLibraryFolders(root);

        string? gameDir = libraries
            .Select(l => Path.Combine(l, "steamapps", "common", "Grim Dawn"))
            .FirstOrDefault(Directory.Exists);

        string? prefix = libraries
            .Select(l => Path.Combine(l, "steamapps", "compatdata", GrimDawnAppId, "pfx"))
            .FirstOrDefault(Directory.Exists);

        var (savePath, saveSource) = FindSavePath(root, prefix);

        return new SteamPaths {
            SteamRoot = root,
            Libraries = libraries,
            GameDir = gameDir,
            PrefixDir = prefix,
            SavePath = savePath,
            SaveSource = saveSource,
        };
    }

    /// <summary>
    /// With Steam Cloud enabled, Grim Dawn keeps transfer.gst under Steam's userdata tree,
    /// NOT in the prefix's Documents folder — the latter is typically empty. Upstream's
    /// GlobalPaths.SavePath hardcodes the Documents path and would find nothing here.
    /// Both are natively readable; no Wine involved either way.
    /// </summary>
    private static (string?, string?) FindSavePath(string steamRoot, string? prefix) {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (Directory.Exists(userdata)) {
            foreach (var user in Directory.GetDirectories(userdata)) {
                var candidate = Path.Combine(user, GrimDawnAppId, "remote", "save");
                if (File.Exists(Path.Combine(candidate, "transfer.gst"))) {
                    return (candidate, $"Steam Cloud userdata (user {Path.GetFileName(user)})");
                }
            }
        }

        if (prefix is not null) {
            var docs = Path.Combine(prefix, "drive_c", "users", "steamuser",
                                    "Documents", "My Games", "Grim Dawn", "Save");
            if (Directory.Exists(docs)) {
                var populated = Directory.EnumerateFiles(docs, "transfer.*").Any();
                return (docs, populated ? "prefix Documents" : "prefix Documents (EMPTY)");
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Minimal reader for steamapps/libraryfolders.vdf. The real host should use
    /// Gameloop.Vdf (already an upstream dependency); this avoids a package reference
    /// in a throwaway probe. Only "path" keys are extracted.
    /// </summary>
    private static List<string> ReadLibraryFolders(string steamRoot) {
        var libraries = new List<string> { steamRoot };

        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return libraries;

        foreach (var line in File.ReadLines(vdf)) {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;

            // "path"		"/mnt/games/SteamLibrary"
            var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
            var value = parts.LastOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(value) && Directory.Exists(value) && !libraries.Contains(value)) {
                libraries.Add(value);
            }
        }

        return libraries;
    }

    private static string Expand(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
}
