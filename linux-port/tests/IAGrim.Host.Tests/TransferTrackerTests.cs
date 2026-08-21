using IAGrim.Platform;
using Xunit;

namespace IAGrim.Host.Tests;

/// <summary>
/// Queueing the same item twice.
///
/// Upstream cannot reach this state. Its <c>TransferItem</c> is a synchronous call into C# from
/// the embedded browser, and <c>ItemTransferController.TransferItems</c> removes the row in the
/// same call that deposits the file — so a second click re-runs <c>GetItemsForTransfer</c>,
/// finds nothing, and says "item does not exist". There is no window.
///
/// This port opened one deliberately: queueing returns immediately and the row is deleted later,
/// once the hook has taken the file, because that disappearance is the only evidence the item
/// was really created. The window between the two is real time — the hook only collects while
/// the player has the transfer stash open, so it can be minutes — and for all of it the row is
/// still there, still searchable, still transferable.
///
/// A second click during that window writes a second CSV. The hook obeys both. The player gets
/// two items from one, which is the one failure this whole area exists to prevent, and it is
/// not recoverable: the game has no idea one of them is spurious.
/// </summary>
public class TransferTrackerTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "iagd-transfer-" + Guid.NewGuid().ToString("N"));

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"iagd-transfer-{Guid.NewGuid():N}.db");

    private readonly PrefixBridge _bridge;
    private readonly EventHub _events = new();
    private DateTime _now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    public TransferTrackerTests() {
        Directory.CreateDirectory(_root);
        _bridge = new PrefixBridge(_root);
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { File.Delete(_databasePath); } catch { }
    }

    private TransferTracker NewTracker() =>
        new(_bridge, _events, _databasePath, () => _now);

    private static LootedItem Item(string name = "Mythical Deathmarked Bloodrender") => new() {
        Mod = "",
        IsHardcore = false,
        BaseRecord = "records/items/gearweapons/swords1h/d013_sword.dbr",
        Seed = 1234567,
        StackCount = 1,
        Stats = [new LootStat(6, name)],
    };

    /// <summary>Everything the hook would find waiting for it in softcore vanilla.</summary>
    private string[] Queued() {
        var folder = Path.Combine(_root, "itemqueue", "outgoing", "sc");
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.csv") : [];
    }

    /// <summary>An item that exists in the database, so the deletion rule has something to delete.</summary>
    private long Insert(LootedItem item) {
        using var store = new LootStore(_databasePath);
        return store.Insert(item);
    }

    private bool RowExists(long id) {
        using var store = new LootStore(_databasePath);
        return store.GetById(id) is not null;
    }

    /// <summary>
    /// The bug, at its simplest: click, click. One item in the collection must not become two
    /// files for the hook to act on.
    /// </summary>
    [Fact]
    public void A_second_click_during_a_transfer_does_not_queue_the_item_again() {
        var tracker = NewTracker();
        var item = Item();
        var id = Insert(item);

        var first  = tracker.Queue(item, id, timeoutSeconds: 300);
        var second = tracker.Queue(item, id, timeoutSeconds: 300);

        Assert.False(first.WasAlreadyQueued);
        Assert.True(second.WasAlreadyQueued);

        // Same transfer, so the UI can keep following the one it already knows about rather
        // than replacing its handle with a second one that means the same file.
        Assert.Equal(first.Transfer.TransferId, second.Transfer.TransferId);
        Assert.Equal(first.Transfer.QueuedPath, second.Transfer.QueuedPath);

        // The assertion that is really about items rather than bookkeeping: the hook finds one
        // file, so the game creates one sword.
        Assert.Single(Queued());
        Assert.Single(tracker.Pending);
    }

    /// <summary>
    /// The same thing, but genuinely simultaneous.
    ///
    /// Every request is handled on its own thread pool task (HostServer.RunAsync), so two clicks
    /// a few milliseconds apart really do run at once. A guard that reads the pending set and
    /// then writes the file is not a guard: both requests pass the read before either writes.
    /// This is the test that fails if the check-and-write is not atomic, and the sequential test
    /// above is the one that fails if there is no check at all.
    /// </summary>
    [Fact]
    public void Simultaneous_clicks_queue_the_item_once() {
        var tracker = NewTracker();
        var item = Item();
        var id = Insert(item);

        var start = new ManualResetEventSlim();
        var results = new QueueResult[16];
        var threads = new Thread[results.Length];

        for (var i = 0; i < threads.Length; i++) {
            var slot = i;
            threads[i] = new Thread(() => {
                start.Wait();
                results[slot] = tracker.Queue(item, id, timeoutSeconds: 300);
            });
            threads[i].Start();
        }

        start.Set();
        foreach (var thread in threads) thread.Join();

        Assert.Single(Queued());
        Assert.Single(tracker.Pending);
        Assert.Single(results, r => !r.WasAlreadyQueued);
        Assert.All(results, r => Assert.Equal(results[0].Transfer.TransferId, r.Transfer.TransferId));
    }

    /// <summary>The guard is per item, not a global "one transfer at a time".</summary>
    [Fact]
    public void Different_items_queue_independently() {
        var tracker = NewTracker();
        var sword = Item();
        var gun = Item("Fresh Revolver") with {
            BaseRecord = "records/items/gearweapons/guns1h/c030_gun1h.dbr", Seed = 7654321,
        };

        var first  = tracker.Queue(sword, Insert(sword), timeoutSeconds: 300);
        var second = tracker.Queue(gun, Insert(gun), timeoutSeconds: 300);

        Assert.False(first.WasAlreadyQueued);
        Assert.False(second.WasAlreadyQueued);
        Assert.Equal(2, Queued().Length);
    }

    /// <summary>
    /// Cancelling puts the item back within reach. The file is gone, so a fresh transfer writes
    /// the only one there is — refusing here would strand the item until a restart.
    /// </summary>
    [Fact]
    public void Cancelling_lets_the_item_be_sent_again() {
        var tracker = NewTracker();
        var item = Item();
        var id = Insert(item);

        var first = tracker.Queue(item, id, timeoutSeconds: 300);
        Assert.True(tracker.Cancel(first.Transfer.TransferId));
        Assert.Empty(Queued());

        var second = tracker.Queue(item, id, timeoutSeconds: 300);

        Assert.False(second.WasAlreadyQueued);
        Assert.NotEqual(first.Transfer.TransferId, second.Transfer.TransferId);
        Assert.Single(Queued());
        Assert.True(RowExists(id));   // cancelled, so the item never left the collection
    }

    /// <summary>
    /// The transfer finishing releases the item, and the row goes with it. Holding the claim
    /// after the file is gone would leak an entry per transfer for the life of the process.
    /// </summary>
    [Fact]
    public async Task Collection_by_the_game_deletes_the_row_and_releases_the_item() {
        var tracker = NewTracker();
        var item = Item();
        var id = Insert(item);

        var queued = tracker.Queue(item, id, timeoutSeconds: 300);

        // What the hook does when the player opens the transfer stash: creates the item and
        // moves the file out of outgoing.
        File.Delete(queued.Transfer.QueuedPath);
        await tracker.PollAsync(CancellationToken.None);

        Assert.Empty(tracker.Pending);
        Assert.False(RowExists(id));
        Assert.False(tracker.IsInFlight(id));
    }

    /// <summary>
    /// A slow transfer is still a transfer.
    ///
    /// The timeout used to drop the record and stop polling, which left the file sitting in
    /// outgoing with nothing watching it. Both halves of that are dupes waiting to happen: the
    /// item is claimable again even though its file is still there, and if the hook does collect
    /// it later nothing deletes the row, so the collection keeps a copy of an item the game now
    /// has. The timeout only reports now; the claim lasts as long as the file does.
    /// </summary>
    [Fact]
    public async Task A_transfer_the_game_has_not_taken_yet_is_still_claimed() {
        var tracker = NewTracker();
        var item = Item();
        var id = Insert(item);

        var queued = tracker.Queue(item, id, timeoutSeconds: 300);

        _now = _now.AddSeconds(301);
        await tracker.PollAsync(CancellationToken.None);

        // Told about, not given up on.
        Assert.Single(tracker.Pending);
        Assert.True(tracker.IsInFlight(id));

        var again = tracker.Queue(item, id, timeoutSeconds: 300);
        Assert.True(again.WasAlreadyQueued);
        Assert.Single(Queued());

        // Still watching, so the row still goes when the player finally opens the stash.
        File.Delete(queued.Transfer.QueuedPath);
        await tracker.PollAsync(CancellationToken.None);

        Assert.False(RowExists(id));
        Assert.Empty(tracker.Pending);
    }

    /// <summary>
    /// A transfer that was refused because the item is already queued must not be reported to
    /// the UI as a fresh queueing — and must not extend or restart the one in flight.
    /// </summary>
    [Fact]
    public void A_refused_transfer_leaves_the_original_untouched() {
        var tracker = NewTracker();
        var item = Item();
        var id = Insert(item);

        var first = tracker.Queue(item, id, timeoutSeconds: 300);

        _now = _now.AddSeconds(120);
        var second = tracker.Queue(item, id, timeoutSeconds: 3600);

        Assert.True(second.WasAlreadyQueued);
        Assert.Equal(first.Transfer.QueuedAt, second.Transfer.QueuedAt);
        Assert.Equal(first.Transfer.ExpiresAt, second.Transfer.ExpiresAt);
    }
}
