using IAGrim.Core.ItemStats;
using IAGrim.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IAGrim.Core.Tests;

/// <summary>
/// What an item is called, and — the point of all of this — that copies of one item are called
/// the same thing however each of them got here.
///
/// The collection these tests build is the one that produced the bug: three copies of a set
/// item, one looted through the hook, one restored from the online backup by a client that
/// wrote the game's colour markers into the name, and one merged in from another database. The
/// comparison view showed the three side by side under three different names.
/// </summary>
public class ItemNameTests : IDisposable {
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"iagd-names-{Guid.NewGuid():N}.db");

    private readonly SqliteConnection _connection;

    public ItemNameTests() {
        _connection = new SqliteConnection($"Data Source={_path}");
        _connection.Open();
        Schema.Apply(_connection);
        SeedGameData();
    }

    public void Dispose() {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" }) {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private const string Coat = "records/storyelements/signs/signt.dbr";
    private const string Prefix = "records/items/lootaffixes/prefix/aa004.dbr";
    private const string Suffix = "records/items/lootaffixes/suffix/bb010.dbr";
    private const string Component = "records/items/materia/compa_bysmiel.dbr";

    /// <summary>
    /// A miniature of what <c>iagd parse</c> writes: the tag table, and the records' name
    /// fields in <c>DatabaseItemStat_v2</c>.
    /// </summary>
    private void SeedGameData() {
        Execute("INSERT INTO ItemTag (Tag, Name) VALUES ('tagItemNameOrder', '{%_s0}{%_s1}{%_s2}{%_s3}{%_s4}')");
        Execute("INSERT INTO ItemTag (Tag, Name) VALUES ('tagCoat', 'Lokarr''s Coat')");
        Execute("INSERT INTO ItemTag (Tag, Name) VALUES ('tagStyleUniqueTier3', 'Mythical')");
        Execute("INSERT INTO ItemTag (Tag, Name) VALUES ('tagPrefixAA004', 'Shrewd')");
        Execute("INSERT INTO ItemTag (Tag, Name) VALUES ('tagSuffixBB010', 'of the Aether')");
        Execute("INSERT INTO ItemTag (Tag, Name) VALUES ('tagBindings', 'Bindings of Bysmiel')");

        AddRecord(1, Coat, ("itemNameTag", "tagCoat"));
        AddRecord(2, Prefix, ("lootRandomizerName", "tagPrefixAA004"));
        AddRecord(3, Suffix, ("lootRandomizerName", "tagSuffixBB010"));
        AddRecord(4, Component, ("description", "tagBindings"));
    }

    private void AddRecord(long id, string record, params (string Stat, string Tag)[] stats) {
        Execute($"INSERT INTO DatabaseItem_v2 (id_databaseitem, baserecord, name, hash) VALUES ({id}, '{record}', '', 0)");
        foreach (var (stat, tag) in stats) {
            Execute("INSERT INTO DatabaseItemStat_v2 (id_databaseitem, Stat, TextValue, val1) "
                    + $"VALUES ({id}, '{stat}', '{tag}', 0)");
        }
    }

    /// <param name="name">The name the item arrived with, as its own source spelled it.</param>
    private long AddItem(string name, string? prefix = null, string? suffix = null,
                         string? materia = null) {
        Execute("INSERT INTO PlayerItem (baserecord, PrefixRecord, SuffixRecord, MateriaRecord, "
                + "Seed, Name, namelowercase, Rarity) VALUES "
                + $"('{Coat}', '{prefix ?? ""}', '{suffix ?? ""}', '{materia ?? ""}', 0, "
                + $"'{name.Replace("'", "''")}', '{name.Replace("'", "''").ToLowerInvariant()}', 'Epic')");

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid();";
        return (long)command.ExecuteScalar()!;
    }

    private void Execute(string sql) {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string? NameOf(long id) {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT Name FROM PlayerItem WHERE Id = {id};";
        return command.ExecuteScalar() as string;
    }

    private string? LowercaseOf(long id) {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT namelowercase FROM PlayerItem WHERE Id = {id};";
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// The bug, as it presented: one item, three names, because each copy kept the spelling of
    /// whatever wrote it. The hook's copy carries the game's set marker, the restored copy
    /// carries an older client's colour token, and the merged copy carries neither.
    /// </summary>
    [Fact]
    public void CopiesOfOneItemGetOneName() {
        var looted = AddItem("(S) Lokarr's Coat");
        var restored = AddItem("(S) {}Lokarr's Coat");
        var merged = AddItem("Lokarr's Coat");

        // Two rewrites, not three: the merged copy was already spelled the way the game data
        // spells it, and a row that does not change is not written.
        Assert.Equal(2, ItemNameRefresh.Run(_connection));

        Assert.Equal("Lokarr's Coat", NameOf(looted));
        Assert.Equal("Lokarr's Coat", NameOf(restored));
        Assert.Equal("Lokarr's Coat", NameOf(merged));
    }

    /// <summary>
    /// Affixes and the socketed component, in upstream's order: prefix, quality, style, name,
    /// suffix, with the component appended in brackets.
    /// </summary>
    [Fact]
    public void AffixesAndComponentAreComposedInUpstreamsOrder() {
        var item = AddItem("whatever the wire said", Prefix, Suffix, Component);

        ItemNameRefresh.Run(_connection);

        Assert.Equal("Shrewd Lokarr's Coat of the Aether [Bindings of Bysmiel]", NameOf(item));
    }

    /// <summary>The search reads the lowercase column, so it has to move with the name.</summary>
    [Fact]
    public void LowercaseNameFollows() {
        var item = AddItem("(S) {}Lokarr's Coat");

        ItemNameRefresh.Run(_connection);

        Assert.Equal("lokarr's coat", LowercaseOf(item));
    }

    /// <summary>
    /// Rewriting only what differs is what makes this safe to run at every start and after
    /// every download.
    /// </summary>
    [Fact]
    public void RefreshIsIdempotent() {
        AddItem("(S) Lokarr's Coat");
        AddItem("Lokarr's Coat");

        Assert.Equal(1, ItemNameRefresh.Run(_connection));
        Assert.Equal(0, ItemNameRefresh.Run(_connection));
    }

    /// <summary>
    /// An item the game data cannot name keeps what it has. Blanking it would lose it from the
    /// name search, which is worse than a name in the wrong spelling.
    /// </summary>
    [Fact]
    public void UnknownRecordKeepsItsStoredName() {
        Execute("INSERT INTO PlayerItem (baserecord, PrefixRecord, SuffixRecord, MateriaRecord, "
                + "Seed, Name, namelowercase) VALUES "
                + "('records/items/from/a/mod/nobody/parsed.dbr', '', '', '', 0, "
                + "'Modded Blade', 'modded blade')");

        Assert.Equal(0, ItemNameRefresh.Run(_connection));

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Name FROM PlayerItem WHERE baserecord LIKE '%mod%';";
        Assert.Equal("Modded Blade", command.ExecuteScalar() as string);
    }

    /// <summary>
    /// With no parsed game data there is nothing to compose from, and the names an unparsed
    /// collection arrived with are all it has.
    /// </summary>
    [Fact]
    public void UnparsedCollectionIsLeftAlone() {
        Execute("DELETE FROM ItemTag");
        var item = AddItem("(S) Lokarr's Coat");

        Assert.Equal(0, ItemNameRefresh.Run(_connection));
        Assert.Equal("(S) Lokarr's Coat", NameOf(item));
    }

    /// <summary>
    /// The name an item is imported with, as <c>NewItemDetails</c> writes it — the path a
    /// freshly looted item takes, where upstream also replaces the name it was handed.
    /// </summary>
    [Fact]
    public void ImportComposesTheNameToo() {
        var item = AddItem("(S) ^BLokarr's Coat", Prefix);

        var (described, skipped) = NewItemDetails.Apply(_connection, [item]);

        Assert.Equal(1, described);
        Assert.Equal(0, skipped);
        Assert.Equal("Shrewd Lokarr's Coat", NameOf(item));
    }
}
