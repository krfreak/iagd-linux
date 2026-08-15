using System.Diagnostics;

namespace IAGrim.Cloud;

/// <summary>
/// "Do this at most every N milliseconds" — upstream's <c>Utilities/ActionCooldown.cs</c>.
///
/// Deliberately starts *ready*: a freshly constructed cooldown fires on the first call and only
/// then begins counting. Upstream relies on that to get one upload attempt shortly after start
/// rather than waiting out a 54-minute window first.
/// </summary>
public sealed class ActionCooldown {
    private Stopwatch? _stopwatch;
    private readonly long _cooldown;

    /// <param name="cooldown">Milliseconds.</param>
    public ActionCooldown(long cooldown) => _cooldown = cooldown;

    public ActionCooldown(long cooldown, bool startTriggered) {
        _cooldown = cooldown;
        if (startTriggered) Reset();
    }

    public bool IsReady => _stopwatch is null || _stopwatch.ElapsedMilliseconds >= _cooldown;

    public bool IsOnCooldown => !IsReady;

    /// <summary>Runs the action if the window has passed, then restarts the clock.</summary>
    public void ExecuteIfReady(Action action) {
        if (!IsReady) return;
        action();
        Reset();
    }

    public void Reset() {
        _stopwatch ??= new Stopwatch();
        _stopwatch.Restart();
    }

    public override string ToString() => $"AC[{_cooldown}]";
}

/// <summary>
/// Splits work into batches of at most 100 — upstream's <c>Backup/BatchUtil.cs</c> and the
/// identical private copies in <c>BackupService</c> and <c>WebSocketSyncService</c>.
///
/// 100 is the server's own limit (<c>api/upload/upload.go</c> rejects a longer array with 400,
/// as does the websocket handler), so this is a protocol constant rather than a tuning choice.
/// </summary>
public static class BatchUtil {
    public const int MaxBatchSize = 100;

    public static List<List<T>> ToBatches<T>(IEnumerable<T>? items) {
        var batches = new List<List<T>>();
        if (items is null) return batches;

        var current = new List<T>();
        foreach (var item in items) {
            if (current.Count >= MaxBatchSize) {
                batches.Add(current);
                current = [];
            }

            current.Add(item);
        }

        if (current.Count > 0) batches.Add(current);
        return batches;
    }
}
