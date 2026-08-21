using IAGrim.Core.ItemStats;
using IAGrim.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IAGrim.Core.Tests;

/// <summary>
/// The colour a looted item is drawn in, which is <c>PlayerItem.Rarity</c> and is written once,
/// on import, by <see cref="NewItemDetails"/>.
///
/// It is computed across *all* of an item's records, and this port stores the game's stat rows
/// only for the records a collection already references — so the first time a kind of item is
/// looted, the rows for its base record are not there. Describing it anyway meant classifying it
/// from whatever else it carried: a set epic with a Silk Swatch socketed came out White, one with
/// an Ancient Armor Plate came out Green, and both took the component's level requirement of 15
/// rather than their own of 58 and 65. Nothing revisited them afterwards, because only a *missing*
/// rarity is ever looked at again.
/// </summary>
public class LootedItemColourTests : IDisposable {
    private const string Helm = "records/items/gearhead/c207_head.dbr";
    private const string Plate = "records/items/materia/ancientarmorplate.dbr";
    private const string Crafted = "records/items/lootaffixes/crafting/ad05_pierceresist.dbr";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"iagd-colour-{Guid.NewGuid():N}.db");

    public void Dispose() {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" }) {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private SqliteConnection Open() {
        var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        Schema.Apply(connection);
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql) {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>A record the parse found, with the stat rows the analysis pass would store.</summary>
    private static void Record(SqliteConnection connection, string record,
                               string? classification, double level) {
        Execute(connection,
            $"INSERT OR IGNORE INTO DatabaseItem_v2 (baserecord, name, hash) VALUES ('{record}', '', 0);");

        if (classification is null) return;

        Execute(connection, $"""
            INSERT INTO DatabaseItemStat_v2 (id_databaseitem, Stat, TextValue, val1)
            SELECT id_databaseitem, 'itemClassification', '{classification}', 0
              FROM DatabaseItem_v2 WHERE baserecord = '{record}';
            INSERT INTO DatabaseItemStat_v2 (id_databaseitem, Stat, TextValue, val1)
            SELECT id_databaseitem, 'levelRequirement', NULL, {level}
              FROM DatabaseItem_v2 WHERE baserecord = '{record}';
            """);
    }

    private static long Insert(SqliteConnection connection, string baseRecord,
                               string materia = "", string modifier = "") {
        Execute(connection, $"""
            INSERT INTO PlayerItem (baserecord, PrefixRecord, SuffixRecord, ModifierRecord,
                                    MateriaRecord, Seed, StackCount)
            VALUES ('{baseRecord}', '', '', '{modifier}', '{materia}', 0, 1);
            """);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static (string? Rarity, double? Level) Describe(SqliteConnection connection, long id) {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Rarity, LevelRequirement FROM PlayerItem WHERE Id = {id};";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetDouble(1));
    }

    [Fact]
    public void AnItemWhoseBaseRecordHasNotBeenReadIsNotColouredByItsComponent() {
        using var connection = Open();
        Record(connection, Helm, classification: null, level: 0);   // known, never analysed
        Record(connection, Plate, "Rare", 15);

        var id = Insert(connection, Helm, materia: Plate);
        var (_, skipped) = NewItemDetails.Apply(connection, [id]);

        Assert.Equal(1, skipped);
        Assert.Equal((null, null), Describe(connection, id));
    }

    [Fact]
    public void TheSameItemIsColouredOnceItsBaseRecordHasBeenRead() {
        using var connection = Open();
        Record(connection, Helm, "Epic", 65);
        Record(connection, Plate, "Rare", 15);

        var id = Insert(connection, Helm, materia: Plate);
        var (described, skipped) = NewItemDetails.Apply(connection, [id]);

        Assert.Equal(1, described);
        Assert.Equal(0, skipped);
        // Grim Dawn's Epic is IA's Blue, and the level is the item's own, not the component's.
        Assert.Equal(("Blue", 65d), Describe(connection, id));
    }

    /// <summary>
    /// <c>ModifierRecord</c> holds a crafting bonus and upstream's <c>GetRecordsForItem</c> does
    /// not return it, so neither does this. It is classified Magical, and counting it turned a
    /// plain crafted item Yellow here while the analysis pass — which never reads it — left the
    /// same item White. Two writers disagreeing about one column is a colour that changes
    /// depending on which of them ran last.
    /// </summary>
    [Fact]
    public void ACraftingBonusDoesNotColourTheItemItWasCraftedOnto() {
        using var connection = Open();
        Record(connection, Helm, "Common", 10);
        Record(connection, Crafted, "Magical", 1);

        var id = Insert(connection, Helm, modifier: Crafted);
        NewItemDetails.Apply(connection, [id]);

        Assert.Equal(("White", 10d), Describe(connection, id));
    }
}
