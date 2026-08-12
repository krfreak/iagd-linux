using System.Text;
using System.Text.Json;

namespace Iagd.Probe;

/// <summary>
/// Observes the shared IPC directory and reports everything the hook DLL emits.
///
/// Polls rather than using inotify. Upstream's host polls at 500ms
/// (IAGrim/UI/MainWindow.cs:801) and polling has no rename/coalescing subtleties, which
/// matters more in a probe than latency does. Production can move to inotify with a
/// polling fallback.
/// </summary>
internal sealed class BridgeWatcher(string bridgeDir, string recordPath) {
    private readonly string _linuxHack = Path.Combine(bridgeDir, "linuxhack");
    private readonly string _archive = Path.Combine(bridgeDir, "probe-archive");
    private readonly string _itemQueue = Path.Combine(bridgeDir, "itemqueue", "ingoing");
    private readonly string _replicaToIa = Path.Combine(bridgeDir, "replica", "to_ia");

    private readonly HashSet<string> _seenFiles = [];
    private readonly Dictionary<int, int> _typeCounts = [];
    private readonly Dictionary<int, long> _noisyCounts = [];
    private StreamWriter? _record;
    private int _messageCount;

    public async Task RunAsync(CancellationToken token) {
        Directory.CreateDirectory(_archive);
        _record = new StreamWriter(recordPath, append: true) { AutoFlush = true };

        Console.WriteLine($"Watching  {bridgeDir}");
        Console.WriteLine($"Recording {recordPath}");
        Console.WriteLine($"Archiving consumed .msg files to {_archive}");
        Console.WriteLine();
        Console.WriteLine("Waiting for the hook DLL. Launch Grim Dawn, then open your stash.");
        Console.WriteLine("Press Ctrl+C to stop and print a summary.");
        Console.WriteLine(new string('-', 78));

        // Anything already present predates this run; note it but don't replay it as live.
        PrimeExisting();

        while (!token.IsCancellationRequested) {
            try {
                PollMarkers();
                PollMessages();
                PollNewFiles(_itemQueue, "*.csv", "LOOT");
                PollNewFiles(_replicaToIa, "*.json", "REPLICA");
            }
            catch (Exception ex) {
                Console.WriteLine($"[probe error] {ex.Message}");
            }

            try { await Task.Delay(250, token); }
            catch (TaskCanceledException) { break; }
        }

        PrintSummary();
    }

    private void PrimeExisting() {
        foreach (var dir in new[] { _itemQueue, _replicaToIa }) {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir)) {
                _seenFiles.Add(f);
            }
        }

        var stale = Directory.Exists(_linuxHack) ? Directory.GetFiles(_linuxHack, "*.msg").Length : 0;
        if (stale > 0) {
            Console.WriteLine($"[note] {stale} pre-existing .msg file(s) found; consuming them as backlog.");
        }
    }

    private void PollMarkers() {
        if (!Directory.Exists(_linuxHack)) return;

        foreach (var pattern in new[] { "*.PID", "*.ABORTED" }) {
            foreach (var file in Directory.GetFiles(_linuxHack, pattern)) {
                if (!_seenFiles.Add(file)) continue;

                var kind = pattern == "*.PID" ? "PID" : "ABORTED";
                var pid = Path.GetFileNameWithoutExtension(file);

                // The pid in the filename is a Wine pid and means nothing to Linux, so a
                // marker cannot be validated directly. A marker older than the running game
                // belongs to an earlier session — reporting that as a live hook is worse
                // than saying nothing, because it looks like everything is fine.
                var stale = IsStale(file);
                var note = (kind, stale) switch {
                    ("PID", false) => "hook DLL attached and running",
                    ("PID", true)  => "STALE — predates the running game, so the current session is NOT hooked",
                    (_, true)      => "stale abort marker from an earlier session",
                    _              => "hook aborted: game not ready to be hooked yet (expected in menus)",
                };

                Console.WriteLine($"[{Stamp()}] {kind,-8} wine pid {pid}  — {note}");
                if (stale && kind == "PID") {
                    Console.WriteLine($"           Run scripts/attach-gd.sh; it clears stale markers and re-attaches.");
                }
                Record(new { kind, pid, file = Path.GetFileName(file), stale });
            }
        }
    }

    private void PollMessages() {
        if (!Directory.Exists(_linuxHack)) return;

        foreach (var file in Directory.GetFiles(_linuxHack, "*.msg").OrderBy(File.GetLastWriteTimeUtc)) {
            byte[] bytes;
            try {
                bytes = File.ReadAllBytes(file);
            }
            catch (IOException) {
                continue; // still being written; next poll
            }

            if (bytes.Length < 8) {
                Console.WriteLine($"[{Stamp()}] MALFORMED  {Path.GetFileName(file)} ({bytes.Length} bytes, need >= 8)");
                Record(new { kind = "malformed", length = bytes.Length });
                Archive(file);
                continue;
            }

            // [int32 type][int32 dataLength][raw data]  — HookDll/Hook/dllmain.cpp:136
            var type = BitConverter.ToInt32(bytes, 0);
            var declared = BitConverter.ToInt32(bytes, 4);
            var actual = bytes.Length - 8;

            var data = declared > 0 && actual >= declared
                ? bytes[8..(8 + declared)]
                : bytes[8..];

            _messageCount++;
            _typeCounts[type] = _typeCounts.GetValueOrDefault(type) + 1;

            if (MessageTypes.IsNoisy(type)) {
                _noisyCounts[type] = _noisyCounts.GetValueOrDefault(type) + 1;
            }
            else {
                var truncated = declared != actual ? $"  [!] declared {declared} but file carries {actual}" : "";
                Console.WriteLine(
                    $"[{Stamp()}] MSG {MessageTypes.Name(type),-34} len={declared,-6} {Describe(data)}{truncated}");
            }

            Record(new {
                kind = "msg",
                type,
                name = MessageTypes.Name(type),
                known = MessageTypes.IsKnown(type),
                declaredLength = declared,
                actualLength = actual,
                utf16 = TryUtf16(data),
                hex = Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 64))),
            });

            Archive(file);
        }
    }

    /// <summary>
    /// Read-only. These files are real looted items and real item stats — the host owns
    /// their lifecycle. Deleting them here would destroy the user's loot.
    /// </summary>
    private void PollNewFiles(string dir, string pattern, string label) {
        if (!Directory.Exists(dir)) return;

        foreach (var file in Directory.GetFiles(dir, pattern)) {
            if (!_seenFiles.Add(file)) continue;

            string content;
            try {
                content = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (IOException) {
                _seenFiles.Remove(file);   // retry next poll
                continue;
            }

            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Console.WriteLine($"[{Stamp()}] {label,-8} {Path.GetFileName(file)}  {lines.Length} row(s)");
            foreach (var line in lines.Take(3)) {
                Console.WriteLine($"           {Truncate(line.Trim(), 100)}");
            }
            if (lines.Length > 3) Console.WriteLine($"           ... {lines.Length - 3} more row(s)");

            Record(new { kind = label.ToLowerInvariant(), file = Path.GetFileName(file), rows = lines.Length, content });
        }
    }

    private void Archive(string file) {
        try {
            File.Move(file, Path.Combine(_archive, Path.GetFileName(file)), overwrite: true);
        }
        catch (IOException) {
            try { File.Delete(file); } catch { /* next poll */ }
        }
    }

    private static string Describe(byte[] data) {
        if (data.Length == 0) return "(no payload)";

        // Fixed-width scalars are checked BEFORE the string interpretation. A 4-byte
        // integer payload is frequently a valid pair of UTF-16 code units, so trying the
        // string first renders hook ids as mojibake ("ὀ") — and this text is the A/B
        // baseline for validating the ported DLL, so it has to be right.
        if (data.Length == 1) return $"byte={data[0]}  (bool={data[0] != 0})";
        if (data.Length == 4) return $"int32={BitConverter.ToInt32(data)}";

        var s = TryUtf16(data);
        if (s is not null) return $"\"{Truncate(s, 60)}\"";

        return "0x" + Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 24)));
    }

    /// <summary>
    /// The DLL builds payload strings as wide strings, so printable payloads are UTF-16LE.
    /// </summary>
    private static string? TryUtf16(byte[] data) {
        if (data.Length < 2 || data.Length % 2 != 0) return null;

        var s = Encoding.Unicode.GetString(data).TrimEnd('\0');
        if (s.Length == 0) return null;

        return s.All(c => !char.IsControl(c) || c is '\t' or '\n' or '\r') ? s : null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    /// <summary>
    /// A marker written before the currently running Grim Dawn started belongs to a
    /// previous session. With no game running at all, every marker is stale.
    /// </summary>
    private static bool IsStale(string markerPath) {
        var started = GameStartTime();
        if (started is null) {
            return true;
        }

        try {
            return File.GetLastWriteTime(markerPath) < started.Value;
        }
        catch (IOException) {
            return false;
        }
    }

    /// <summary>
    /// Earliest start time of any running Grim Dawn process. The game runs under Wine, so
    /// the process name is the Wine loader — the executable only shows up in the command
    /// line, which means scanning /proc rather than matching on process name.
    /// </summary>
    private static DateTime? GameStartTime() {
        DateTime? earliest = null;

        foreach (var dir in Directory.EnumerateDirectories("/proc")) {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) {
                continue;
            }

            try {
                var cmdline = File.ReadAllText(Path.Combine(dir, "cmdline"));
                if (!cmdline.Contains("Grim Dawn.exe", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                var start = System.Diagnostics.Process.GetProcessById(pid).StartTime;
                if (earliest is null || start < earliest) {
                    earliest = start;
                }
            }
            catch {
                // Process exited mid-scan, or /proc entry not readable. Skip it.
            }
        }

        return earliest;
    }

    private void Record(object payload) =>
        _record?.WriteLine(JsonSerializer.Serialize(new {
            ts = DateTimeOffset.Now.ToString("O"),
            payload,
        }));

    private void PrintSummary() {
        Console.WriteLine();
        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"Messages decoded: {_messageCount}");

        if (_typeCounts.Count == 0) {
            Console.WriteLine();
            Console.WriteLine("NOTHING RECEIVED. Check, in order:");
            Console.WriteLine("  1. Does <bridge>/linuxhack/ exist?  If not, the DLL never entered Wine mode");
            Console.WriteLine("     -> settings.json is missing persistent.isRunningInWine = true");
            Console.WriteLine("  2. Is there a .PID file?  If not, the DLL never attached");
            Console.WriteLine("     -> injection failed; check the injector log");
            Console.WriteLine("  3. Is there an .ABORTED file?  Then injection worked but the game");
            Console.WriteLine("     was not ready — load a character rather than sitting in the menu");
            Console.WriteLine("  4. Check the DLL's own log: <bridge>/iagd_hook.log");
            return;
        }

        Console.WriteLine();
        foreach (var (type, count) in _typeCounts.OrderByDescending(kv => kv.Value)) {
            var noisy = MessageTypes.IsNoisy(type) ? "  (suppressed from live output)" : "";
            Console.WriteLine($"  {count,6}  {MessageTypes.Name(type)}{noisy}");
        }

        var unknown = _typeCounts.Keys.Where(t => !MessageTypes.IsKnown(t)).ToList();
        if (unknown.Count > 0) {
            Console.WriteLine();
            Console.WriteLine($"  [!] Unknown message types: {string.Join(", ", unknown)}");
            Console.WriteLine("      The DLL emits types the host does not name. Worth investigating.");
        }

        Console.WriteLine();
        Console.WriteLine($"Baseline written to {recordPath}");
        Console.WriteLine("Keep it: it is the A/B reference for validating the Phase 1 MinGW build.");
    }
}
