namespace IAGrim.Platform;

/// <summary>
/// The directory shared with the hook DLL running inside the Proton prefix.
///
/// Upstream's host is a Windows process, so it and the DLL simply agreed on
/// <c>%LOCALAPPDATA%\EvilSoft\IAGD</c>. A native Linux host has to reach into the prefix
/// instead, because the DLL derives that path itself and we cannot redirect it.
///
/// Everything here is plain file I/O — no Wine call, no Windows API.
/// </summary>
public sealed class PrefixBridge {
    public string Root { get; }

    /// <summary>
    /// The compatdata folder the prefix lives in, when it is known.
    ///
    /// Not used to reach any file here — every path on this class hangs off <see cref="Root"/> —
    /// but the attach script has to be given it, since Proton is launched with
    /// STEAM_COMPAT_DATA_PATH pointing at it. Null when a bare prefix was named and nothing
    /// resembling a compatdata folder sits above it.
    /// </summary>
    public string? CompatData { get; }

    public PrefixBridge(string root, string? compatData = null) {
        Root = root;
        CompatData = compatData;
    }

    /// <summary>
    /// The prefix this bridge sits inside: <see cref="Root"/> with the hook's own sub-path
    /// removed. Null when the root is not shaped like one, which is the case in tests.
    ///
    /// For display. <see cref="CompatData"/> is the one anything functional wants.
    /// </summary>
    public string? Prefix {
        get {
            var prefix = Root;
            foreach (var _ in SteamPaths.BridgeDirIn("").Split(Path.DirectorySeparatorChar,
                                                               StringSplitOptions.RemoveEmptyEntries)) {
                prefix = Path.GetDirectoryName(prefix) ?? "";
            }
            return prefix.Length == 0 ? null : prefix;
        }
    }

    /// <summary>Locates the bridge for a discovered Grim Dawn prefix.</summary>
    public static PrefixBridge? Discover(SteamPaths paths) =>
        paths.BridgeDir is null ? null : new PrefixBridge(paths.BridgeDir, paths.CompatDataDir);

    /// <summary>
    /// The bridge inside a prefix named by hand, for when discovery cannot find one.
    ///
    /// Discovery only looks where Steam puts things: <c>steamapps/compatdata/219990/pfx</c>
    /// under a library named in libraryfolders.vdf. A prefix made by Lutris or Heroic, one
    /// moved off that path, or a Steam library described in a way the .vdf reader cannot follow
    /// is invisible to it — and since the bridge is the only channel the hook has, an invisible
    /// prefix means no looting at all, with nothing in the UI able to change that.
    ///
    /// Both spellings are accepted, because both are things people call "the prefix": the
    /// compatdata folder (which contains <c>pfx</c>) and the prefix itself (which contains
    /// <c>drive_c</c>). Rejecting one of them would be a setting that silently does nothing.
    /// </summary>
    /// <returns>The bridge, or null when the path is not a Wine prefix at all.</returns>
    public static PrefixBridge? ForPrefix(string path) {
        var trimmed = path.Trim();
        if (trimmed.Length == 0) return null;

        var full = Path.GetFullPath(trimmed);

        // A compatdata folder: Proton keeps the prefix in "pfx" beside its config_info.
        if (Directory.Exists(Path.Combine(full, "pfx", "drive_c"))) {
            return new PrefixBridge(SteamPaths.BridgeDirIn(Path.Combine(full, "pfx")), full);
        }

        // The prefix itself. Its parent is the compatdata folder when Steam made it, and
        // something else entirely when it did not — hence the check rather than an assumption.
        if (Directory.Exists(Path.Combine(full, "drive_c"))) {
            var parent = Path.GetDirectoryName(full);
            var compatData = parent is not null
                             && File.Exists(Path.Combine(parent, "config_info")) ? parent : null;
            return new PrefixBridge(SteamPaths.BridgeDirIn(full), compatData);
        }

        return null;
    }

    /// <summary>Hook → host: binary messages, replacing WM_COPYDATA.</summary>
    public string LinuxHack => Ensure(Path.Combine(Root, "linuxhack"));

    /// <summary>Hook → host: items looted out of the stash.</summary>
    public string LootIncoming => Ensure(Path.Combine(Root, "itemqueue", "ingoing"));

    /// <summary>
    /// Host → hook: items to materialise back into the game.
    ///
    /// The hook scans this per (hardcore, mod) pair — see
    /// InventorySack_AddItem::GetFolderToLootFrom — so the sub-path is significant, not
    /// cosmetic. A file in the wrong branch is simply never picked up.
    /// </summary>
    public string TransferToGame(bool isHardcore, string? mod = null) {
        var path = Path.Combine(Root, "itemqueue", "outgoing", isHardcore ? "hc" : "sc");
        if (!string.IsNullOrWhiteSpace(mod)) {
            path = Path.Combine(path, mod);
        }
        return Ensure(path);
    }

    /// <summary>
    /// Where the hook moves a transfer file once the item is in the game — a soft delete.
    /// Its presence here is the only confirmation that a transfer actually happened.
    /// </summary>
    public string TransferCompleted(bool isHardcore, string? mod = null) {
        var path = Path.Combine(Root, "itemqueue", "deleted", isHardcore ? "hc" : "sc");
        if (!string.IsNullOrWhiteSpace(mod)) {
            path = Path.Combine(path, mod);
        }
        return Ensure(path);
    }

    /// <summary>Host → hook: requests to resolve stats for an item (not transfers).</summary>
    public string StatRequestToGame => Ensure(Path.Combine(Root, "replica", "from_ia"));

    /// <summary>Hook → host: resolved item stats.</summary>
    public string StatResultFromGame => Ensure(Path.Combine(Root, "replica", "to_ia"));

    /// <summary>The settings file the DLL reads. Only a few keys matter — see EnsureWineMode.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string HookLog => Path.Combine(Root, "iagd_hook.log");

    /// <summary>
    /// True when a hook is attached to the *currently running* game.
    ///
    /// The pid inside a marker is a Wine pid and means nothing to Linux, so a marker on its
    /// own proves nothing: one left by a previous session looks identical to a live one.
    /// Age relative to the running game is the only signal that can be checked from here.
    /// </summary>
    public bool IsHookLive(DateTime? gameStartedAt) {
        var markers = Directory.Exists(LinuxHack)
            ? Directory.GetFiles(LinuxHack, "*.PID")
            : [];

        if (markers.Length == 0) return false;
        if (gameStartedAt is null) return false;   // no game running: every marker is stale

        return markers.Any(m => File.GetLastWriteTime(m) >= gameStartedAt.Value);
    }

    public IEnumerable<string> PendingLootFiles() =>
        Directory.Exists(LootIncoming)
            ? Directory.EnumerateFiles(LootIncoming, "*.csv")
            : [];

    private static string Ensure(string path) {
        Directory.CreateDirectory(path);
        return path;
    }
}
