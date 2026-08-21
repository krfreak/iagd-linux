using IAGrim.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IAGrim.Core.Tests;

/// <summary>
/// An item that has arrived but has not been described yet — and the promise that it can still
/// be found.
///
/// Every item spends a moment in this state: the analysis pass gives it a rarity and a level
/// requirement just after it is imported. When Grim Dawn's data has not been read, the moment
/// lasts until it has been, and that is exactly when a new install is at its most fragile — the
/// hook is working, items are arriving, and there is nothing yet to describe them with.
///
/// What made it a bug report rather than a wait was where the items went. The search applies
/// upstream's own default upper bound of level 110, NULL fails that comparison, and so a looted
/// item was counted in the collection, listed under Grim Dawn, and absent from the item list
/// with no filter switched on to explain it. It came back only by pushing the maximum level past
/// 120, which turns the clause off altogether — at which point the level 94 items it had been
/// hiding were plainly visible.
///
/// Upstream never sees this: its LevelRequirement is a non-nullable double, so its undescribed
/// items are level 0 and compare fine.
/// </summary>
public class UnanalysedItemTests : IDisposable {
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"iagd-unanalysed-{Guid.NewGuid():N}.db");

    public void Dispose() {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" }) {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private SqliteConnection Open() {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        return connection;
    }

    /// <summary>The predicate ItemQuery builds for the maximum-level box, verbatim.</summary>
    private long MatchingDefaultLevelFilter() {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PlayerItem p WHERE p.LevelRequirement <= 110;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public void AnItemLootedBeforeTheGameDataWasReadIsStillFoundByTheDefaultFilter() {
        using (var store = new LootStore(_path)) {
            store.Insert(new LootedItem {
                BaseRecord = "records/items/upgraded/head/a01_head.dbr",
                Seed = 1234,
            });
        }

        Assert.Equal(1, MatchingDefaultLevelFilter());
    }

    /// <summary>
    /// The same repair for a collection written before this: those rows are already in people's
    /// databases, and telling them to loot the item again is not a fix. Schema.Apply runs on
    /// every start, so opening the client is all it takes.
    /// </summary>
    [Fact]
    public void ACollectionAlreadyHoldingNullsIsRepairedWhenItIsOpened() {
        using (var connection = Open()) {
            Schema.Apply(connection);
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO PlayerItem (baserecord, PrefixRecord, SuffixRecord, MateriaRecord, "
                + "Seed, LevelRequirement) VALUES ('records/items/x.dbr', '', '', '', 7, NULL);";
            insert.ExecuteNonQuery();
        }

        Assert.Equal(0, MatchingDefaultLevelFilter());

        using (var connection = Open()) {
            Schema.Apply(connection);
        }

        Assert.Equal(1, MatchingDefaultLevelFilter());
    }

    /// <summary>
    /// Zero is a placeholder, not an answer, and the analysis pass must still overwrite it. The
    /// pass selects on <c>Rarity IS NULL</c>, which this does not touch — asserted here because
    /// filling in a level as a way to keep an item visible would be a poor trade if it also
    /// convinced the client the item was already described.
    /// </summary>
    [Fact]
    public void FillingInTheLevelDoesNotMarkTheItemAsDescribed() {
        using (var store = new LootStore(_path)) {
            store.Insert(new LootedItem { BaseRecord = "records/items/y.dbr", Seed = 99 });
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PlayerItem WHERE Rarity IS NULL;";
        Assert.Equal(1, Convert.ToInt64(command.ExecuteScalar()));
    }
}
