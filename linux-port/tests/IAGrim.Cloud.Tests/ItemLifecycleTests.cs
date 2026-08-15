using IAGrim.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IAGrim.Cloud.Tests;

/// <summary>
/// The two points in an item's life that online sync depends on, both of them in the ordinary
/// loot path rather than in the sync code: an item gets its cloud identity when it is created,
/// and leaves a tombstone when it is taken by the game.
///
/// Neither is conditional on being logged in. That is the point — an item looted today may be
/// uploaded next month, and by then it is too late to decide what its identity was.
/// </summary>
public class ItemLifecycleTests : IDisposable {
    private readonly string _path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"iagd-lifecycle-{Guid.NewGuid():N}.db");

    private static LootedItem Item(string name = "Fresh Revolver") => new() {
        Mod = "",
        BaseRecord = "records/items/gearweapons/guns1h/c030_gun1h.dbr",
        Seed = Random.Shared.NextInt64(1, int.MaxValue),
        StackCount = 1,
        Stats = [new LootStat(6, name)],
    };

    private (string? CloudId, long HasSync) ReadIdentity(long id) {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cloudid, IFNULL(cloud_hassync, 0) FROM PlayerItem WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (null, -1);
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1));
    }

    private List<string> Tombstones() {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM deletedplayeritem_v3;";
        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    /// Every looted item gets an id the server would accept, whether or not sync is on.
    /// Assigning it lazily at upload time is what lets the same item reach another machine
    /// twice — once over the live socket with no id, once over REST with one.
    /// </summary>
    [Fact]
    public void A_looted_item_gets_a_cloud_identity_immediately() {
        using var store = new LootStore(_path);
        var id = store.Insert(Item());

        var (cloudId, hasSync) = ReadIdentity(id);

        Assert.True(CloudIdentity.IsAcceptable(cloudId));
        Assert.Equal(0, hasSync);   // minted, not uploaded
    }

    [Fact]
    public void Every_item_gets_its_own_identity() {
        using var store = new LootStore(_path);

        var ids = Enumerable.Range(0, 50)
            .Select(i => ReadIdentity(store.Insert(Item($"Revolver {i}"))).CloudId)
            .ToList();

        Assert.Equal(50, ids.Distinct().Count());
    }

    /// <summary>
    /// An item the server knows about leaves a tombstone when the game takes it. Without it the
    /// user's other machine keeps its copy and uploads it back, so the item the player just
    /// moved into the stash returns to the collection and can be moved in a second time.
    /// </summary>
    [Fact]
    public void Transferring_a_synchronised_item_leaves_a_tombstone() {
        using var store = new LootStore(_path);
        var id = store.Insert(Item());

        // Pretend it has been uploaded.
        using (var connection = new SqliteConnection($"Data Source={_path}")) {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE PlayerItem SET cloud_hassync = 1 WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        var cloudId = ReadIdentity(id).CloudId;
        Assert.True(store.Delete(id));

        Assert.Equal([cloudId], Tombstones());
    }

    /// <summary>
    /// An item that was never uploaded leaves nothing behind. A tombstone for it would be an id
    /// the server has never seen, sent on every deletion sync from now on.
    /// </summary>
    [Fact]
    public void Transferring_an_unsynchronised_item_leaves_no_tombstone() {
        using var store = new LootStore(_path);
        var id = store.Insert(Item());

        Assert.True(store.Delete(id));

        Assert.Empty(Tombstones());
    }

    /// <summary>
    /// The tombstone is written from the row being deleted, so it has to be taken before the
    /// row goes. Deleting twice does not produce a second one.
    /// </summary>
    [Fact]
    public void Deleting_the_same_item_twice_leaves_one_tombstone() {
        using var store = new LootStore(_path);
        var id = store.Insert(Item());

        using (var connection = new SqliteConnection($"Data Source={_path}")) {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE PlayerItem SET cloud_hassync = 1 WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        Assert.True(store.Delete(id));
        Assert.False(store.Delete(id));   // already gone

        Assert.Single(Tombstones());
    }

    /// <summary>
    /// The database this port writes stays the one the Windows tool reads. The identity columns
    /// are upstream's, so a collection that has been synced here opens there with its sync state
    /// intact rather than offering the whole collection for upload again.
    /// </summary>
    [Fact]
    public void The_identity_columns_are_upstreams() {
        using var store = new LootStore(_path);
        store.Insert(Item());

        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();

        Assert.True(Schema.ColumnExists(connection, "PlayerItem", "cloudid"));
        Assert.True(Schema.ColumnExists(connection, "PlayerItem", "cloud_hassync"));
        Assert.True(Schema.TableExists(connection, "deletedplayeritem_v3"));

        // The column upstream adds from its BuddyItem mapping, and which the buddy insert names.
        Assert.True(Schema.ColumnExists(connection, "buddyitems_v6", "AffixRerollsUsed"));
    }

    public void Dispose() {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) {
            try { File.Delete(_path + suffix); } catch (IOException) { /* best effort */ }
        }
    }
}
