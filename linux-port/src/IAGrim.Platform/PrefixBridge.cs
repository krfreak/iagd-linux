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

    public PrefixBridge(string root) {
        Root = root;
    }

    /// <summary>Locates the bridge for a discovered Grim Dawn prefix.</summary>
    public static PrefixBridge? Discover(SteamPaths paths) =>
        paths.BridgeDir is null ? null : new PrefixBridge(paths.BridgeDir);

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
