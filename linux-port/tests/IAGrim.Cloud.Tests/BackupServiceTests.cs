using IAGrim.Cloud.Data;
using IAGrim.Cloud.Dto;
using Xunit;

namespace IAGrim.Cloud.Tests;

/// <summary>
/// Two machines, one account, one real server.
///
/// This is the test that matters. Every way this feature can mangle a collection is a
/// disagreement between two clients — an item uploaded twice, a deletion that never crosses, a
/// timestamp that skips a window — and none of them are visible from one client alone. So each
/// test here runs a full <see cref="BackupService"/> against its own database and checks what
/// the *other* database ends up holding.
/// </summary>
[Collection(CloudServerCollection.Name)]
public class BackupServiceTests {
    private readonly CloudServerFixture _server;
    private readonly (string Email, string Token) _account;

    public BackupServiceTests(CloudServerFixture server) {
        _server = server;
        Skip.IfNot(server.Available, server.SkipReason);
        server.UseUris();

        // One account per test. These assert on absolute collection sizes, so a test that
        // inherited another's items would be asserting the wrong thing rather than failing
        // honestly. xunit builds this class once per test method.
        _account = server.NewAccount();
    }

    private Machine NewMachine(bool dualPc = true, bool realCooldowns = false) =>
        new(_account, dualPc, realCooldowns);

    /// <summary>
    /// A machine: its own collection database, its own settings, and a backup service logged
    /// into the shared test account.
    /// </summary>
    private sealed class Machine : IDisposable {
        public TestCollection Collection { get; } = new();
        public TestSettings Settings { get; }
        public BackupService Backup { get; }
        public List<long> Uploaded { get; } = [];

        private readonly bool _realCooldowns;
        private bool _shrunk;

        public Machine((string Email, string Token) account, bool dualPc = true,
                       bool realCooldowns = false) {
            _realCooldowns = realCooldowns;
            Settings = new TestSettings {
                CloudUser = account.Email,
                CloudAuthToken = account.Token,
                // The single-PC cooldowns are 54 minutes, which would let exactly one operation
                // happen per test run. Dual-PC is the same code path with the server's own
                // faster numbers, and it is the mode these tests are about anyway.
                UsingDualComputer = dualPc,
            };

            // The authentication result is cached process-wide for a day, as upstream caches it.
            AuthService.InvalidateCache();

            var auth = new AuthService(new AuthenticationProvider(Settings), Collection.Store);
            Backup = new BackupService(auth, Collection.Store, Settings, Uploaded.AddRange);
        }

        /// <summary>
        /// One pass of the loop the worker thread runs, after which the fetched cooldowns are
        /// replaced with one-second ones — see <see cref="FastCooldowns"/>. The first pass is
        /// what fetches them, so the real values are exercised either way; what goes is the
        /// waiting, which is all the ten-second deletion and download windows cost a test.
        /// </summary>
        /// <param name="search">
        /// False for a client nobody is looking at — the idle clock is what freezes downloads.
        /// </param>
        public void Execute(bool search = true) {
            if (search) Backup.OnSearch();
            Backup.Execute();
            if (!_realCooldowns && !_shrunk) _shrunk = FastCooldowns.TryApply(Backup);
        }

        /// <summary>Sleeps a whole server-side second: its timestamps have no finer resolution.</summary>
        public void Wait() =>
            Thread.Sleep(_realCooldowns ? 1100 : FastCooldowns.PumpIntervalMs);

        /// <summary>
        /// The trailing wait is not slack. Dropping it — on the theory that the caller asserts
        /// as soon as this returns — cost nothing measurable (1 m 58 s against 1 m 55 s) and made
        /// An_item_transferred_into_the_game_disappears_from_the_other_machine fail: the second
        /// machine pumps straight after the first, and what separates their passes is this sleep.
        /// </summary>
        public void Pump(int passes = 3) {
            for (var i = 0; i < passes; i++) {
                Execute();
                Wait();
            }
        }

        /// <summary>
        /// Pumps until something has happened, or gives up.
        ///
        /// Needed because the three operations are on separate clocks: uploads may run every
        /// second in dual-computer mode but deletions only every ten, so "I called Execute three
        /// times" is not the same as "the deletion has had a chance to go out".
        /// </summary>
        public bool PumpUntil(Func<bool> done, int seconds = 25) {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) {
                Execute();
                if (done()) return true;
                Wait();
            }
            return done();
        }

        public void Dispose() => Collection.Dispose();
    }

    /// <summary>
    /// The client obeys the numbers the server hands out, rather than any of its own.
    ///
    /// Every other test here replaces them with one-second windows so it does not have to wait
    /// (<see cref="FastCooldowns"/>), which leaves exactly one thing unguarded: a regression
    /// where the client stops taking the server's limits seriously and hammers a service run for
    /// free. That the windows then *gate* anything is <see cref="PacingTests"/>'s job and needs
    /// no server, so this asserts the wiring and stays fast.
    /// </summary>
    [SkippableFact]
    public void The_cooldowns_the_server_hands_out_are_the_ones_the_client_uses() {
        using var machine = NewMachine(realCooldowns: true);

        machine.Pump(passes: 1);

        var multiUsage = FastCooldowns.WindowsOf(machine.Backup, "MultiUsage");
        var regular = FastCooldowns.WindowsOf(machine.Backup, "Regular");

        // logincheck.go: 10 s deletion, 1 s upload, 10 s download for a dual-computer user;
        // 54 minutes across the board for everyone else.
        Assert.Equal((10000L, 1000L, 10000L), multiUsage);
        Assert.Equal((3240000L, 3240000L, 3240000L), regular);
    }

    [SkippableFact]
    public void A_looted_item_is_uploaded_and_marked_synchronised() {
        using var machine = NewMachine();
        var id = machine.Collection.AddLootedItem("Uploaded Revolver");

        Assert.Single(machine.Collection.Store.GetUnsynchronizedItems());

        machine.Pump();

        // Nothing is left to upload, and the item now carries the server's blessing rather than
        // being offered again on every pass.
        Assert.Empty(machine.Collection.Store.GetUnsynchronizedItems());
        Assert.Contains(id, machine.Uploaded);

        // And it survived a reopen: the flag is on disk, not in a field.
        machine.Collection.Store.Reopen();
        Assert.Empty(machine.Collection.Store.GetUnsynchronizedItems());
    }

    [SkippableFact]
    public void An_item_looted_on_one_machine_arrives_on_the_other() {
        using var first = NewMachine();
        using var second = NewMachine();

        first.Collection.AddLootedItem("Travelling Revolver");
        first.Pump();

        second.Pump();

        Assert.Equal(1, second.Collection.CountItems());

        // Its records came with it. Without them the item is in the collection but invisible to
        // the damage-type and pet-bonus filters, which read PlayerItemRecord rather than the row.
        var cloudId = second.Collection.Store.GetOnlineIds().Single();
        Assert.Equal(1, second.Collection.CountRecordsFor(cloudId));
    }

    /// <summary>
    /// The one that costs a collection if it is wrong: an item that arrives from the cloud must
    /// not be uploaded back. It arrives marked synchronised, so there is nothing to upload — and
    /// if that flag were missing it would go up under the same id, come back down on the third
    /// machine, and every client would slowly accumulate copies.
    /// </summary>
    [SkippableFact]
    public void A_downloaded_item_is_not_uploaded_again() {
        using var first = NewMachine();
        using var second = NewMachine();

        first.Collection.AddLootedItem("Boomerang Revolver");
        first.Pump();
        second.Pump();

        Assert.Equal(1, second.Collection.CountItems());
        Assert.Empty(second.Collection.Store.GetUnsynchronizedItems());
        Assert.Empty(second.Uploaded);
    }

    /// <summary>
    /// Downloading twice does not double the collection. The guard is the set of cloud ids
    /// already held, which is checked before anything is written.
    /// </summary>
    [SkippableFact]
    public void Downloading_twice_does_not_duplicate_the_collection() {
        using var first = NewMachine();
        using var second = NewMachine();

        first.Collection.AddLootedItem("Singular Revolver");
        first.Pump();

        second.Pump();
        Assert.Equal(1, second.Collection.CountItems());

        // Rewind the high-water mark: the same window is fetched again, exactly as it would be
        // after a crash between storing items and saving the timestamp.
        second.Settings.CloudUploadTimestamp = 0;
        second.Pump();

        Assert.Equal(1, second.Collection.CountItems());
    }

    /// <summary>
    /// Transferring an item into the game on one machine removes it from the other. Without the
    /// tombstone reaching the server, the second machine keeps its copy — and, worse, uploads it
    /// again so the item reappears on the first.
    /// </summary>
    [SkippableFact]
    public void An_item_transferred_into_the_game_disappears_from_the_other_machine() {
        using var first = NewMachine();
        using var second = NewMachine();

        var id = first.Collection.AddLootedItem("Departing Revolver");
        first.Pump();
        second.Pump();
        Assert.Equal(1, second.Collection.CountItems());

        first.Collection.TransferAway(id);
        Assert.Single(first.Collection.Store.GetItemsMarkedForOnlineDeletion());

        // Deletions run on their own, slower clock than uploads — ten seconds even in
        // dual-computer mode — so this waits for the real cooldown rather than assuming a fixed
        // number of passes is enough. The tombstone is cleared once the server has accepted it.
        Assert.True(first.PumpUntil(() => first.Collection.Store.GetItemsMarkedForOnlineDeletion().Count == 0),
            "the deletion was never sent to the server");

        Assert.True(second.PumpUntil(() => second.Collection.CountItems() == 0),
            "the deletion never reached the second machine");
    }

    /// <summary>
    /// A tombstone is dropped only once the server has accepted *that* id.
    ///
    /// Upstream clears the whole table after each accepted batch. On a clean run nothing shows:
    /// the later batches are already in memory and still go out. The damage needs a batch to
    /// fail — a dropped connection, a logout mid-pass — after which the loop returns with every
    /// unsent tombstone erased, and those deletions are never retried. Rather than orchestrate a
    /// mid-pass failure against a live server, this pins the invariant that makes such a failure
    /// survivable: what gets cleared is exactly what was sent.
    ///
    /// Deletions are spread over more than one batch, since with a single batch "clear the whole
    /// table" and "clear this batch" are the same act and the test would pass either way.
    /// </summary>
    [SkippableFact]
    public void Only_the_ids_the_server_accepted_are_cleared() {
        using var machine = NewMachine();

        const int count = 150;   // BatchUtil batches at 100
        var ids = new List<long>();
        for (var i = 0; i < count; i++) ids.Add(machine.Collection.AddLootedItem($"Doomed Revolver {i}"));

        Assert.True(machine.PumpUntil(() => machine.Collection.Store.GetUnsynchronizedItems().Count == 0),
            "the items were never uploaded");

        var expected = machine.Collection.Store.GetOnlineIds().ToHashSet(StringComparer.Ordinal);
        Assert.Equal(count, expected.Count);

        foreach (var id in ids) machine.Collection.TransferAway(id);

        // A real backup service, but watching how it clears.
        var recorder = new RecordingStore(machine.Collection.Store);
        AuthService.InvalidateCache();
        var auth = new AuthService(new AuthenticationProvider(machine.Settings), machine.Collection.Store);
        var backup = new BackupService(auth, recorder, machine.Settings);

        var deadline = DateTime.UtcNow.AddSeconds(25);
        var shrunk = false;
        while (DateTime.UtcNow < deadline && recorder.Store.GetItemsMarkedForOnlineDeletion().Count > 0) {
            backup.OnSearch();
            backup.Execute();
            if (!shrunk) shrunk = FastCooldowns.TryApply(backup);
            Thread.Sleep(FastCooldowns.PumpIntervalMs);
        }

        Assert.Empty(recorder.Store.GetItemsMarkedForOnlineDeletion());

        // Never the blunt one: that is the call that throws away tombstones for batches which
        // have not been sent yet.
        Assert.Equal(0, recorder.ClearedEverything);

        // And between them, the per-batch clears account for every id exactly once.
        Assert.Equal(count, recorder.ClearedIds.Count);
        Assert.Equal(expected, recorder.ClearedIds.ToHashSet(StringComparer.Ordinal));
        Assert.True(recorder.ClearCalls.Count > 1, "the deletions did not span more than one batch");
    }

    /// <summary>A real store that records how the deletion loop clears its tombstones.</summary>
    private sealed class RecordingStore(CloudItemStoreHandle store) : ICloudItemStore {
        public CloudItemStoreHandle Store { get; } = store;
        public int ClearedEverything { get; private set; }
        public List<IReadOnlyCollection<string>> ClearCalls { get; } = [];
        public List<string> ClearedIds => ClearCalls.SelectMany(call => call).ToList();

        public void ClearItemsMarkedForOnlineDeletion() {
            ClearedEverything++;
            Store.ClearItemsMarkedForOnlineDeletion();
        }

        public void ClearItemsMarkedForOnlineDeletion(IReadOnlyCollection<string> ids) {
            ClearCalls.Add(ids);
            Store.ClearItemsMarkedForOnlineDeletion(ids);
        }

        public IList<CloudItem> GetUnsynchronizedItems() => Store.GetUnsynchronizedItems();
        public void SetAsSynchronized(IList<CloudItem> items) => Store.SetAsSynchronized(items);
        public IList<string> GetOnlineIds() => Store.GetOnlineIds();
        public IList<ItemIdentifierDto> GetItemsMarkedForOnlineDeletion() =>
            Store.GetItemsMarkedForOnlineDeletion();
        public void Save(IList<CloudItem> items) => Store.Save(items);
        public void Delete(IList<DeleteItemDto> items) => Store.Delete(items);
        public void ResetOnlineSyncState() => Store.ResetOnlineSyncState();
    }

    /// <summary>
    /// An item deleted here and not yet reported is skipped on the way down, rather than being
    /// restored by the download that happens before the deletion has been sent.
    /// </summary>
    [SkippableFact]
    public void A_locally_deleted_item_is_not_downloaded_back() {
        // Real cooldowns, and they are the point: this needs a *pending* tombstone, and what
        // keeps it pending is the ten-second deletion window not having come round again. With
        // the shrunk ones the deletion goes out first and there is nothing left to skip on the
        // way down — the test would pass or fail on a race rather than on the behaviour.
        using var machine = NewMachine(realCooldowns: true);

        var id = machine.Collection.AddLootedItem("Doomed Revolver");
        machine.Pump();

        var cloudId = machine.Collection.Store.GetOnlineIds().Single();
        machine.Collection.TransferAway(id);
        Assert.Equal(0, machine.Collection.CountItems());

        // Ask for the whole collection again while the tombstone is still pending.
        machine.Settings.CloudUploadTimestamp = 0;
        machine.Backup.OnSearch();
        machine.Backup.Execute();

        Assert.Equal(0, machine.Collection.CountItems());
        Assert.Contains(machine.Collection.Store.GetItemsMarkedForOnlineDeletion(),
            item => item.Id == cloudId);
    }

    /// <summary>
    /// Nothing happens without a valid token — not a single request. A background loop that
    /// keeps calling after a logout is exactly the kind of traffic this port must not generate.
    /// </summary>
    [SkippableFact]
    public void A_logged_out_client_does_nothing() {
        using var machine = NewMachine();
        machine.Collection.AddLootedItem("Private Revolver");

        machine.Settings.CloudAuthToken = null;
        AuthService.InvalidateCache();

        machine.Pump();

        Assert.Single(machine.Collection.Store.GetUnsynchronizedItems());
        Assert.Empty(machine.Uploaded);
    }

    /// <summary>
    /// Downloads stop once nothing has been searched for 31 minutes. Uploads do not: loot still
    /// has to be backed up whether or not anyone is looking at the window.
    /// </summary>
    [SkippableFact]
    public void Downloads_freeze_when_the_client_goes_idle_but_uploads_do_not() {
        using var first = NewMachine();
        using var idle = NewMachine();

        first.Collection.AddLootedItem("Ignored Revolver");
        first.Pump();

        // Never calls OnSearch, so the idle clock started at construction and stays there.
        // Executing repeatedly must not download anything.
        for (var i = 0; i < 3; i++) {
            typeof(BackupService)
                .GetField("_lastSearchDt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(idle.Backup, DateTimeOffset.UtcNow.AddMinutes(-45));
            idle.Execute(search: false);
            idle.Wait();
        }

        Assert.Equal(0, idle.Collection.CountItems());

        // But an item looted while idle still goes up.
        idle.Collection.AddLootedItem("Backed Up Anyway");
        idle.Execute(search: false);
        idle.Wait();
        idle.Execute(search: false);
        Assert.Empty(idle.Collection.Store.GetUnsynchronizedItems());
    }

    /// <summary>
    /// A collection larger than one batch goes up in whole batches of 100, and all of it
    /// arrives. The server refuses 101 in one request, so getting this wrong means the tail of a
    /// large collection is never backed up.
    /// </summary>
    [SkippableFact]
    public void A_collection_larger_than_one_batch_is_uploaded_completely() {
        using var first = NewMachine();
        using var second = NewMachine();

        for (var i = 0; i < 250; i++) {
            first.Collection.AddLootedItem($"Bulk Revolver {i}");
        }

        first.Pump(4);
        Assert.Empty(first.Collection.Store.GetUnsynchronizedItems());

        second.Pump(4);
        Assert.Equal(250, second.Collection.CountItems());
    }

    /// <summary>
    /// An item with no cloud id — merged in from another collection, or written by a version of
    /// this port that predates the column — gets one before it is uploaded.
    /// </summary>
    [SkippableFact]
    public void An_item_without_a_cloud_id_is_given_one() {
        using var machine = NewMachine();
        machine.Collection.AddLootedItem("Anonymous Revolver", cloudId: "");

        machine.Pump();

        var stored = Assert.Single(machine.Collection.Store.GetOnlineIds());
        Assert.True(CloudIdentity.IsAcceptable(stored));
        Assert.Empty(machine.Collection.Store.GetUnsynchronizedItems());
    }

    /// <summary>
    /// The high-water mark only advances after a download has been stored. A timestamp saved
    /// past items that were never written is unrecoverable without a full resync.
    /// </summary>
    [SkippableFact]
    public void The_timestamp_advances_only_after_items_are_stored() {
        using var first = NewMachine();
        using var second = NewMachine();

        Assert.Equal(0, second.Settings.CloudUploadTimestamp);

        first.Collection.AddLootedItem("Timestamped Revolver");
        first.Pump();
        second.Pump();

        Assert.True(second.Settings.CloudUploadTimestamp > 0);
        Assert.Equal(1, second.Collection.CountItems());
        Assert.True(second.Settings.Saves > 0);
    }

    /// <summary>
    /// Losing the token mid-life resets every item's synchronised flag, so the collection is
    /// offered in full to whatever account is used next rather than being silently half-backed-up.
    /// </summary>
    [SkippableFact]
    public void A_revoked_token_resets_the_synchronised_flags() {
        using var machine = NewMachine();
        machine.Collection.AddLootedItem("Orphaned Revolver");
        machine.Pump();
        Assert.Empty(machine.Collection.Store.GetUnsynchronizedItems());

        machine.Settings.CloudAuthToken = Guid.NewGuid().ToString();   // a token the server does not know
        AuthService.InvalidateCache();

        var auth = new AuthService(new AuthenticationProvider(machine.Settings), machine.Collection.Store);
        Assert.Equal(AuthService.AccessStatus.Unauthorized, auth.CheckAuthentication());

        Assert.Single(machine.Collection.Store.GetUnsynchronizedItems());
        Assert.True(string.IsNullOrEmpty(machine.Settings.CloudAuthToken));
    }
}
