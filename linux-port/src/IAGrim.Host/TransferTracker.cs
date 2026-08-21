using IAGrim.Platform;

namespace IAGrim.Host;

/// <summary>A transfer that has been queued for the game to collect.</summary>
public sealed record PendingTransfer(
    string TransferId,
    long ItemId,
    string ItemName,
    string QueuedPath,
    DateTime QueuedAt,
    DateTime ExpiresAt) {
    /// <summary>
    /// Whether the player has already been told this one is taking a while. The notice is worth
    /// sending once; sending it every two seconds for the rest of the session is not.
    /// </summary>
    public bool Notified { get; init; }
}

/// <summary>
/// What came of asking to queue a transfer: either the one just written, or the one already in
/// flight for that item. The caller needs to tell those apart — the second is a refusal, and
/// reporting it as a fresh transfer is how the UI ends up showing two handles for one file.
/// </summary>
public sealed record QueueResult(PendingTransfer Transfer, bool WasAlreadyQueued);

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
///
/// **One item, one file.** That rule has a cost upstream does not pay. Upstream deposits and
/// removes the row in a single synchronous call, so its second click finds no item and does
/// nothing; here the row survives until the hook collects, and every click during that window
/// used to write another CSV. The hook obeys all of them, and the game has no way to know that
/// two of the three swords it just made were never earned. So an item with a file waiting for
/// it is claimed, and stays claimed until that file is gone — collected or cancelled.
/// </summary>
public sealed class TransferTracker {
    /// <summary>
    /// Keyed by transfer id, and <see cref="_inFlight"/> maps item id to the same key.
    ///
    /// Both are guarded by <see cref="_gate"/> rather than being concurrent collections. The
    /// property that matters is that checking for an existing claim and writing the file happen
    /// together: requests are handled on separate thread pool tasks, so a check that merely
    /// precedes the write lets two clicks a few milliseconds apart both pass it. The lock is
    /// held across the write for that reason. It costs the few milliseconds of a small file
    /// write, and it means transfers are serialised — which is what "one item, one file" is.
    /// </summary>
    private readonly Dictionary<string, PendingTransfer> _pending = [];
    private readonly Dictionary<long, string> _inFlight = [];
    private readonly Lock _gate = new();

    private readonly PrefixBridge _bridge;
    private readonly EventHub _events;
    private readonly string _databasePath;
    private readonly Func<DateTime> _now;

    /// <param name="databasePath">
    /// Injected rather than read from LinuxPaths, so this can be exercised against a
    /// throwaway database. Hardcoding it would make any test of the deletion rule mutate the
    /// player's real collection — which is exactly the rule most worth testing.
    /// </param>
    /// <param name="now">
    /// The clock, for the same reason: the shortest transfer timeout the API accepts is five
    /// seconds, and a test of what happens after one should not cost five seconds of waiting.
    /// </param>
    public TransferTracker(PrefixBridge bridge, EventHub events, string? databasePath = null,
                           Func<DateTime>? now = null) {
        _bridge = bridge;
        _events = events;
        _databasePath = databasePath ?? LinuxPaths.DatabaseFile;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Called after the game has taken an item and the row is gone.
    ///
    /// Online sync uses it to push the deletion to the user's other machines immediately. That
    /// ordering matters more than it looks: until the other machine knows, it still offers the
    /// item, and transferring it there too puts a second copy in the game.
    /// </summary>
    public Action? OnItemTakenByGame { get; set; }

    public IReadOnlyCollection<PendingTransfer> Pending {
        get { lock (_gate) return [.. _pending.Values]; }
    }

    /// <summary>Whether this item already has a file waiting for the game to collect.</summary>
    public bool IsInFlight(long itemId) {
        lock (_gate) return _inFlight.ContainsKey(itemId);
    }

    /// <summary>
    /// Queues one item, unless it is already queued — see the class remarks for why that second
    /// case is a refusal rather than a second file. The refusal returns the transfer already in
    /// flight, untouched: it is the same file, so it keeps its own age and its own deadline.
    /// </summary>
    public QueueResult Queue(LootedItem item, long itemId, int timeoutSeconds,
                             bool? targetHardcore = null, string? targetMod = null) {
        lock (_gate) {
            if (_inFlight.TryGetValue(itemId, out var existingId)
                && _pending.TryGetValue(existingId, out var existing)) {
                return new QueueResult(existing, WasAlreadyQueued: true);
            }

            var transfer = new TransferService(_bridge);
            var queuedPath = transfer.Queue(item, targetHardcore, targetMod);

            var queuedAt = _now();
            var record = new PendingTransfer(
                TransferId: Guid.NewGuid().ToString("N")[..12],
                ItemId:     itemId,
                ItemName:   item.PlainName ?? item.BaseRecord,
                QueuedPath: queuedPath,
                QueuedAt:   queuedAt,
                ExpiresAt:  queuedAt.AddSeconds(Math.Clamp(timeoutSeconds, 5, 3600)));

            _pending[record.TransferId] = record;
            _inFlight[itemId] = record.TransferId;
            return new QueueResult(record, WasAlreadyQueued: false);
        }
    }

    /// <summary>
    /// Cancels a transfer, if the hook has not already taken it. Returns false when the item
    /// is already in the game — at which point removing the row is correct, not retrying.
    ///
    /// A successful cancel releases the item as well as the record. The file is gone, so the
    /// next transfer of that item writes the only one there is.
    /// </summary>
    public bool Cancel(string transferId) {
        lock (_gate) {
            if (!_pending.TryGetValue(transferId, out var record)) return false;

            var transfer = new TransferService(_bridge);
            if (!transfer.Cancel(record.QueuedPath)) return false;

            Forget(record);
            return true;
        }
    }

    /// <summary>
    /// One polling pass, driven by the host's background loop. Kept synchronous-in-spirit
    /// and side-effecting so there is exactly one place where a row is deleted.
    /// </summary>
    public async Task PollAsync(CancellationToken cancellationToken) {
        PendingTransfer[] watching;
        lock (_gate) {
            if (_pending.Count == 0) return;
            watching = [.. _pending.Values];
        }

        foreach (var record in watching) {
            if (!File.Exists(record.QueuedPath)) {
                // Collected: the hook moved it to itemqueue/deleted. The item is released here
                // as well, but only because its file is gone — that is the same condition, not
                // a second one.
                lock (_gate) {
                    if (!_pending.ContainsKey(record.TransferId)) continue;   // cancelled underneath us
                    Forget(record);
                }

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

            if (!record.Notified && _now() > record.ExpiresAt) {
                // Say it is slow; keep watching. This used to drop the record and leave the file,
                // reasoning that the hook would still collect it whenever the stash was next
                // opened. It does — and with nothing watching, nothing deleted the row, so the
                // player kept a copy of an item the game had just been given. Worse, the item
                // was claimable again while its file was still sitting there, so the next click
                // wrote a second one. The deadline is a notification, not an expiry.
                lock (_gate) {
                    if (!_pending.ContainsKey(record.TransferId)) continue;
                    _pending[record.TransferId] = record with { Notified = true };
                }

                await _events.BroadcastAsync(new HostEvent("transferDelayed", new {
                    transferId = record.TransferId,
                    itemId     = record.ItemId,
                    message    = $"{record.ItemName} is still queued — open the transfer stash in game.",
                }), cancellationToken);
            }
        }
    }

    /// <summary>Drops a transfer and the claim it holds on its item. Call under the lock.</summary>
    private void Forget(PendingTransfer record) {
        _pending.Remove(record.TransferId);
        if (_inFlight.TryGetValue(record.ItemId, out var held) && held == record.TransferId) {
            _inFlight.Remove(record.ItemId);
        }
    }
}
