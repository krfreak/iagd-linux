using System.Collections.Concurrent;
using IAGrim.Platform;

namespace IAGrim.Host;

/// <summary>A transfer that has been queued for the game to collect.</summary>
public sealed record PendingTransfer(
    string TransferId,
    long ItemId,
    string ItemName,
    string QueuedPath,
    DateTime QueuedAt,
    DateTime ExpiresAt);

/// <summary>
/// Tracks in-flight transfers and reports their outcome over the WebSocket.
///
/// The HTTP request used to block until the hook collected the file, which matched the CLI's
/// semantics but is wrong for a UI: collection only happens once the player opens the
/// transfer stash, so the request could legitimately hang for minutes, and a reload lost all
/// knowledge of it. Queuing is now immediate and the outcome arrives as an event.
///
/// The safety rule is unchanged and is the whole reason this class exists: the database row
/// is deleted strictly after the queued file disappears. The hook acknowledges nothing —
/// that disappearance is the only evidence the item was created — so deleting earlier loses
/// the item and never deleting duplicates it.
/// </summary>
public sealed class TransferTracker {
    private readonly ConcurrentDictionary<string, PendingTransfer> _pending = new();
    private readonly PrefixBridge _bridge;
    private readonly EventHub _events;
    private readonly string _databasePath;

    /// <param name="databasePath">
    /// Injected rather than read from LinuxPaths, so this can be exercised against a
    /// throwaway database. Hardcoding it would make any test of the deletion rule mutate the
    /// player's real collection — which is exactly the rule most worth testing.
    /// </param>
    public TransferTracker(PrefixBridge bridge, EventHub events, string? databasePath = null) {
        _bridge = bridge;
        _events = events;
        _databasePath = databasePath ?? LinuxPaths.DatabaseFile;
    }

    /// <summary>
    /// Called after the game has taken an item and the row is gone.
    ///
    /// Online sync uses it to push the deletion to the user's other machines immediately. That
    /// ordering matters more than it looks: until the other machine knows, it still offers the
    /// item, and transferring it there too puts a second copy in the game.
    /// </summary>
    public Action? OnItemTakenByGame { get; set; }

    public IReadOnlyCollection<PendingTransfer> Pending => _pending.Values.ToArray();

    public PendingTransfer Queue(LootedItem item, long itemId, int timeoutSeconds,
                                 bool? targetHardcore = null, string? targetMod = null) {
        var transfer = new TransferService(_bridge);
        var queuedPath = transfer.Queue(item, targetHardcore, targetMod);

        var record = new PendingTransfer(
            TransferId: Guid.NewGuid().ToString("N")[..12],
            ItemId:     itemId,
            ItemName:   item.PlainName ?? item.BaseRecord,
            QueuedPath: queuedPath,
            QueuedAt:   DateTime.UtcNow,
            ExpiresAt:  DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 5, 3600)));

        _pending[record.TransferId] = record;
        return record;
    }

    /// <summary>
    /// Cancels a transfer, if the hook has not already taken it. Returns false when the item
    /// is already in the game — at which point removing the row is correct, not retrying.
    /// </summary>
    public bool Cancel(string transferId) {
        if (!_pending.TryGetValue(transferId, out var record)) return false;

        var transfer = new TransferService(_bridge);
        if (!transfer.Cancel(record.QueuedPath)) return false;

        _pending.TryRemove(transferId, out _);
        return true;
    }

    /// <summary>
    /// One polling pass, driven by the host's background loop. Kept synchronous-in-spirit
    /// and side-effecting so there is exactly one place where a row is deleted.
    /// </summary>
    public async Task PollAsync(CancellationToken cancellationToken) {
        if (_pending.IsEmpty) return;

        foreach (var record in _pending.Values) {
            if (!File.Exists(record.QueuedPath)) {
                // Collected: the hook moved it to itemqueue/deleted.
                _pending.TryRemove(record.TransferId, out _);

                using (var store = new LootStore(_databasePath)) {
                    // Writes the tombstone as well as removing the row -- see LootStore.Delete.
                    store.Delete(record.ItemId);
                }

                // The tombstone now exists, so the push can read it. A no-op unless live sync is
                // connected; the regular deletion sync carries it otherwise.
                try { OnItemTakenByGame?.Invoke(); }
                catch (Exception ex) { Console.Error.WriteLine($"live sync push failed: {ex.Message}"); }

                await _events.BroadcastAsync(new HostEvent("transferCompleted", new {
                    transferId = record.TransferId,
                    itemId     = record.ItemId,
                    collected  = true,
                    message    = $"{record.ItemName} is in your stash.",
                }), cancellationToken);

                await _events.BroadcastAsync(HostEvent.Removed(record.ItemId), cancellationToken);
                continue;
            }

            if (DateTime.UtcNow > record.ExpiresAt) {
                // Give up watching, but leave the file: the hook will still collect it the
                // next time the stash is opened. Removing it silently would be worse — the
                // player would have neither the item nor a queued transfer.
                _pending.TryRemove(record.TransferId, out _);

                await _events.BroadcastAsync(new HostEvent("transferCompleted", new {
                    transferId = record.TransferId,
                    itemId     = record.ItemId,
                    collected  = false,
                    message    = $"{record.ItemName} is still queued — open the transfer stash in game.",
                }), cancellationToken);
            }
        }
    }
}
