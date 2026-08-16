namespace IAGrim.Platform;

/// <summary>
/// Finds the running Grim Dawn, and — just as importantly — refuses to find anything else.
///
/// Under Wine the process name is the loader, so the game only shows up in the command line;
/// hence scanning /proc rather than matching on a process name.
///
/// **The exclusions are the point.** The attach script runs the injector as
/// <c>proton run injector64.exe … --attach-window "Grim Dawn" --attach-name "Grim Dawn.exe"</c>,
/// so for as long as an attach is in flight there are three processes — the Proton wrapper,
/// wine's steam.exe, and the injector itself — whose command line contains the game's name.
/// Measured with no game running at all: zero matches before an attach, three during it.
///
/// A hook attacher that mistakes its own injector for the game is not a cosmetic bug. It makes
/// "is the game running" true when it is not, which arms another attach, which produces another
/// three phantom processes. Everything downstream — the attach pacing, the stale-marker sweep,
/// the cloud worker's idea of whether it may talk to the server, the header in the UI — is
/// reading this answer.
/// </summary>
public static class GameProcess {
    /// <summary>
    /// Whether a /proc command line belongs to Grim Dawn itself.
    ///
    /// Deliberately a subtractive rule rather than a precise positive match on the executable's
    /// full path: a positive match that is slightly wrong stops loot capture working at all and
    /// says nothing about why, while removing the one family of false positives we can name
    /// cannot lose a real game.
    /// </summary>
    /// <param name="commandLine">
    /// Raw /proc/&lt;pid&gt;/cmdline, NUL-separated. Every marker below sits inside a single
    /// argument, so the separators do not matter.
    /// </param>
    public static bool IsGameCommandLine(string commandLine) {
        if (!commandLine.Contains("Grim Dawn.exe", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        // Our own injector, and the Proton/wine processes carrying its arguments. Any one of
        // these markers is enough; all three are checked because the wrapper processes carry
        // different parts of the command line.
        foreach (var ours in InjectorMarkers) {
            if (commandLine.Contains(ours, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    private static readonly string[] InjectorMarkers = [
        "injector64.exe",
        "--attach-name",
        "--attach-window",
    ];

    /// <summary>
    /// When the running game started, or null if it is not running.
    ///
    /// The earliest match wins: during an attach the game is older than anything we launched,
    /// so this stayed correct even before the exclusions above — which is exactly why the bug
    /// survived so long. It was only wrong in the gap between sessions, where it mattered most.
    /// </summary>
    public static DateTime? StartTime() {
        DateTime? earliest = null;

        foreach (var dir in Directory.EnumerateDirectories("/proc")) {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try {
                if (!IsGameCommandLine(File.ReadAllText(Path.Combine(dir, "cmdline")))) continue;

                var start = System.Diagnostics.Process.GetProcessById(pid).StartTime;
                if (earliest is null || start < earliest) earliest = start;
            }
            catch { /* exited mid-scan */ }
        }

        return earliest;
    }
}
