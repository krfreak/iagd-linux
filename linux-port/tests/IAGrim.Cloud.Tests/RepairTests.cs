using IAGrim.DbDoctor;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IAGrim.Cloud.Tests;

/// <summary>
/// The repair, against the server it is repairing the collection's relationship with.
///
/// Collapsing duplicates locally is easy to get right and worthless on its own: the whole
/// failure this tool exists for is a collection whose local state is fine and whose server state
/// disagrees. So the test that matters is not "the rows went away" — it is "the rows went away
/// and *stayed* away across a download", which needs a real server holding a real copy.
///
/// The setup reproduces the shape found in the field: one item present twice under two cloud
/// ids, both uploaded, and nothing in the tombstone table, because the tombstones that should
/// have been there were erased before they were sent.
/// </summary>
[Collection(CloudServerCollection.Name)]
public class RepairTests {
    private readonly CloudServerFixture _server;
    private readonly (string Email, string Token) _account;

    public RepairTests(CloudServerFixture server) {
        _server = server;
        Skip.IfNot(server.Available, server.SkipReason);
        server.UseUris();
        _account = server.NewAccount();
    }

    /// <summary>
    /// A machine whose collection holds <paramref name="copies"/> copies of one item, each with
    /// its own cloud id, all uploaded to the shared account.
    /// </summary>
    private sealed class Doubled : IDisposable {
        public TestCollection Collection { get; } = new();
        public BackupService Backup { get; }
        public TestSettings Settings { get; }
        public long Seed { get; } = Random.Shared.NextInt64(1, int.MaxValue);

        private bool _shrunk;

        public Doubled((string Email, string Token) account, int copies = 2) {
            Settings = new TestSettings {
                CloudUser = account.Email,
                CloudAuthToken = account.Token,
                UsingDualComputer = true,
            };

            AuthService.InvalidateCache();
            var auth = new AuthService(new AuthenticationProvider(Settings), Collection.Store);
            Backup = new BackupService(auth, Collection.Store, Settings, null);

            // The same item by upstream's equality key — one base record, one seed, no affixes —
            // inserted `copies` times. Distinct cloud ids is what makes this the lost-deletion
            // shape rather than an import run twice.
            for (var i = 0; i < copies; i++) Insert($"Doubled Revolver {i}");
        }

        private void Insert(string name) {
            using var connection = new SqliteConnection($"Data Source={Collection.Path}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO PlayerItem (
                    baserecord, PrefixRecord, SuffixRecord, ModifierRecord, TransmuteRecord,
                    MateriaRecord, RelicCompletionBonusRecord, EnchantmentRecord,
                    AscendantAffixNameRecord, AscendantAffix2hNameRecord,
                    Seed, StackCount, created_at, Name, namelowercase, Rarity, LevelRequirement,
                    Mod, IsHardcore, cloudid, cloud_hassync
                ) VALUES (
                    'records/items/gearweapons/guns1h/c030_gun1h.dbr', '', '', '', '',
                    '', '', '', '', '',
                    $seed, 1, 1700000000000, $name, $lower, 'Blue', 94,
                    '', 0, $cloudId, 0
                );
                """;
            command.Parameters.AddWithValue("$seed", Seed);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$lower", name.ToLowerInvariant());
            command.Parameters.AddWithValue("$cloudId", CloudIdentity.New());
            command.ExecuteNonQuery();
        }

        public void Execute() {
            Backup.OnSearch();
            Backup.Execute();
            if (!_shrunk) _shrunk = FastCooldowns.TryApply(Backup);
        }

        public bool PumpUntil(Func<bool> done, int seconds = 25) {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) {
                Execute();
                if (done()) return true;
                Thread.Sleep(FastCooldowns.PumpIntervalMs);
            }
            return done();
        }

        public int Items() => Collection.CountItems();

        public int Tombstones() {
            using var connection = new SqliteConnection($"Data Source={Collection.Path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM deletedplayeritem_v3;";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>
        /// Wipes the tombstone table without sending anything, which is what upstream's
        /// <c>SyncDeletions</c> does to every deletion still queued when a batch fails.
        /// </summary>
        public void LoseTombstones() {
            using var connection = new SqliteConnection($"Data Source={Collection.Path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM deletedplayeritem_v3;";
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Deletes an item the way a repair must not: the row goes, nothing is recorded.
        /// </summary>
        public void DeleteOneCopyWithoutATombstone() {
            using var connection = new SqliteConnection($"Data Source={Collection.Path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM PlayerItem WHERE Id = (SELECT MAX(Id) FROM PlayerItem);";
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Puts the client back to a full sync, as logging in on a fresh install does and as
        /// upstream's "sync from scratch" button does explicitly (<c>OnlineSettings</c>, and
        /// this port's <c>CloudWorker</c>, both set the timestamp to 0).
        ///
        /// This is the moment a lost deletion actually bites. Downloads are served
        /// <c>WHERE ts &gt; lastTimestamp</c>, so a collection that has been polling all along
        /// never re-fetches an item it already has; the copies come back when the high-water
        /// mark is reset and the server hands over everything it still holds.
        /// </summary>
        public void ResyncFromScratch() {
            Settings.CloudUploadTimestamp = 0;
            typeof(BackupService)
                .GetField("_hasSyncedDownOnce", System.Reflection.BindingFlags.NonPublic
                                              | System.Reflection.BindingFlags.Instance)!
                .SetValue(Backup, false);
        }

        public void Dispose() => Collection.Dispose();
    }

    /// <summary>
    /// The control: deleting the row and nothing else is what makes items come back.
    ///
    /// Without this test the one below proves nothing — an item that was never going to return
    /// cannot demonstrate that a tombstone stopped it returning. So this reproduces the user's
    /// loop end to end against the real server: delete locally with no tombstone, re-sync, and
    /// watch the copy walk back in.
    /// </summary>
    [SkippableFact]
    public void Without_a_tombstone_the_deleted_copy_comes_back() {
        using var machine = new Doubled(_account);

        Assert.True(machine.PumpUntil(() => machine.Collection.Store.GetUnsynchronizedItems().Count == 0),
            "both copies should have been uploaded");
        Assert.Equal(2, machine.Items());

        machine.DeleteOneCopyWithoutATombstone();
        Assert.Equal(1, machine.Items());
        Assert.Equal(0, machine.Tombstones());

        machine.ResyncFromScratch();

        Assert.True(machine.PumpUntil(() => machine.Items() == 2),
            "the server still holds the copy, so a full re-sync should hand it back");
    }

    /// <summary>
    /// The repair removes the extra copy, and it stays gone across a full re-sync.
    ///
    /// Same setup as the control, same re-sync, opposite outcome — the only difference being
    /// that the repair recorded the deletion where the client would find it and send it on.
    /// </summary>
    [SkippableFact]
    public void A_repaired_duplicate_does_not_come_back() {
        using var machine = new Doubled(_account);

        Assert.True(machine.PumpUntil(() => machine.Collection.Store.GetUnsynchronizedItems().Count == 0),
            "both copies should have been uploaded");
        Assert.Equal(2, machine.Items());

        // The state the field reports: the server holds both, the client is told nothing is
        // pending, and the tombstones that would have said otherwise are gone.
        machine.LoseTombstones();
        Assert.Equal(0, machine.Tombstones());

        machine.Collection.Store.Dispose();
        using (var repair = new Repair(machine.Collection.Path)) {
            var outcome = repair.Duplicates(commit: true);
            Assert.Equal(1, outcome.Rows);
        }
        machine.Collection.Store.Reopen();

        Assert.Equal(1, machine.Items());
        Assert.Equal(1, machine.Tombstones());

        Assert.True(machine.PumpUntil(() => machine.Tombstones() == 0),
            "the repair's tombstone should have reached the server");

        machine.ResyncFromScratch();
        machine.PumpUntil(() => false, seconds: 6);

        Assert.Equal(1, machine.Items());
    }

    /// <summary>
    /// A second machine, which still holds both copies, is told about the deletion too.
    ///
    /// This is what separates a repair from a local tidy-up: the account is shared, and a
    /// deletion the server accepts has to reach every client. Without it the other machine goes
    /// on holding an item the first one deliberately removed.
    /// </summary>
    [SkippableFact]
    public void The_deletion_reaches_the_other_machine() {
        using var first = new Doubled(_account);
        Assert.True(first.PumpUntil(() => first.Collection.Store.GetUnsynchronizedItems().Count == 0));

        using var second = new Doubled(_account, copies: 0);
        Assert.True(second.PumpUntil(() => second.Items() == 2), "the second machine should see both copies");

        first.LoseTombstones();
        first.Collection.Store.Dispose();
        using (var repair = new Repair(first.Collection.Path)) repair.Duplicates(commit: true);
        first.Collection.Store.Reopen();

        Assert.True(first.PumpUntil(() => first.Tombstones() == 0));

        // From scratch, so the assertion does not depend on which second the deletion landed in:
        // the server's timestamps are whole seconds and downloads are served `WHERE ts > ?`, so
        // a machine whose high-water mark is already past that second would never be told.
        second.ResyncFromScratch();

        Assert.True(second.PumpUntil(() => second.Items() == 1),
            "the second machine should have dropped the copy the repair deleted");
    }

    /// <summary>
    /// An item that was never uploaded is removed without a tombstone.
    ///
    /// Upstream's rule, carried by <c>CloudTombstone.Mark</c>: a tombstone for an id the server
    /// has never seen is a stray deletion the client would keep offering it forever.
    /// </summary>
    [SkippableFact]
    public void A_duplicate_that_was_never_uploaded_leaves_no_tombstone() {
        using var machine = new Doubled(_account);

        machine.Collection.Store.Dispose();
        using (var repair = new Repair(machine.Collection.Path)) {
            var outcome = repair.Duplicates(commit: true);
            Assert.Equal(1, outcome.Rows);
        }
        machine.Collection.Store.Reopen();

        Assert.Equal(1, machine.Items());
        Assert.Equal(0, machine.Tombstones());
    }
}
