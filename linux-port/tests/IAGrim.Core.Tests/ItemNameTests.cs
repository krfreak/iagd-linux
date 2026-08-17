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
    /// The same, but with a component in the socket — the case that broke. A component is a
    /// vanilla record and is nearly always parsed, so an item whose *base* record is not leaves
    /// exactly one nameable part, and the composed name was the bracketed component on its own:
    /// "Modded Blade" became " [Bindings of Bysmiel]".
    /// </summary>
    [Fact]
    public void UnknownRecordWithAComponentKeepsItsStoredNameToo() {
        Execute("INSERT INTO PlayerItem (baserecord, PrefixRecord, SuffixRecord, MateriaRecord, "
                + "Seed, Name, namelowercase) VALUES "
                + $"('records/items/from/a/mod/nobody/parsed.dbr', '', '', '{Component}', 0, "
                + "'Modded Blade', 'modded blade')");

        Assert.Equal(0, ItemNameRefresh.Run(_connection));

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Name FROM PlayerItem WHERE baserecord LIKE '%mod%';";
        Assert.Equal("Modded Blade", command.ExecuteScalar() as string);
    }

    /// <summary>
    /// The same again, with an affix instead of a component — the case a player actually
    /// reported. A magical item has exactly one affix, that affix is a vanilla record and so is
    /// parsed even when the modded base record it sits on is not, and the composed name was the
    /// affix on its own: the game's "Ancient Warmaul of Ruin" was stored as "of Ruin", and the
    /// prefix version as "Mighty".
    ///
    /// Rarity and level requirement look right on such an item, which is what made it hard to
    /// spot: they are read across all of the records at once, so the affix supplies both.
    /// </summary>
    [Theory]
    [InlineData(true, false, "Ancient Warmaul of the Aether")]
    [InlineData(false, true, "Shrewd Ancient Warmaul")]
    [InlineData(true, true, "Shrewd Ancient Warmaul of the Aether")]
    public void UnknownRecordWithAnAffixKeepsItsStoredName(bool suffix, bool prefix, string stored) {
        var item = AddUnparsedItem(stored, prefix ? Prefix : null, suffix ? Suffix : null);

        Assert.Equal(0, ItemNameRefresh.Run(_connection));
        Assert.Equal(stored, NameOf(item));

        NewItemDetails.Apply(_connection, [item]);
        Assert.Equal(stored, NameOf(item));
        Assert.Equal(stored.ToLowerInvariant(), LowercaseOf(item));
    }

    /// <summary>
    /// A cropped name that was written elsewhere — by this port before the guard, by the Windows
    /// tool, by another client — and arrived through a merge or the online backup. Nothing asks
    /// to describe such an item again, and composing agrees with the crop, so without this it
    /// would keep the affix for ever.
    /// </summary>
    [Fact]
    public void AnAffixOnlyNameThatArrivedFromElsewhereIsRepairedFromTheTooltip() {
        var item = AddUnparsedItem("of the Aether", null, Suffix);
        AddTooltip(item, "^BAncient Warmaul of the Aether");

        Assert.Equal(1, ItemNameRefresh.Run(_connection));
        Assert.Equal("Ancient Warmaul of the Aether", NameOf(item));
        Assert.Equal("ancient warmaul of the aether", LowercaseOf(item));

        // And having repaired it, it stays repaired.
        Assert.Equal(0, ItemNameRefresh.Run(_connection));
    }

    /// <summary>
    /// The same crop with no tooltip to fall back on keeps what it has. Blanking it would drop
    /// it out of the name search, and parsing the mod its base record comes from repairs it
    /// properly — through the ordinary path, on the composed name's own merits.
    /// </summary>
    [Fact]
    public void AnAffixOnlyNameWithNoTooltipIsLeftAlone() {
        var item = AddUnparsedItem("of the Aether", null, Suffix);

        Assert.Equal(0, ItemNameRefresh.Run(_connection));
        Assert.Equal("of the Aether", NameOf(item));
    }

    /// <summary>
    /// An item the game itself draws under an affix-only name — so the tooltip agrees with the
    /// stored name and there is nothing to repair. Rewriting the row to the value it already
    /// holds would make every sweep report work and never settle.
    /// </summary>
    [Fact]
    public void ATooltipThatAgreesWithTheCropIsNotARepair() {
        var item = AddUnparsedItem("of the Aether", null, Suffix);
        AddTooltip(item, "^Bof the Aether");

        Assert.Equal(0, ItemNameRefresh.Run(_connection));
        Assert.Equal("of the Aether", NameOf(item));
    }

    /// <summary>
    /// A real name that happens to sit on an item with an unparsed base record is not a crop and
    /// is not touched — the repair keys on the name being *exactly* what the affixes compose to.
    /// </summary>
    [Fact]
    public void ATooltipNamedItemIsNotMistakenForACrop() {
        var item = AddUnparsedItem("Ancient Warmaul of the Aether", null, Suffix);
        AddTooltip(item, "^BAncient Warmaul of the Aether");

        Assert.Equal(0, ItemNameRefresh.Run(_connection));
        Assert.Equal("Ancient Warmaul of the Aether", NameOf(item));
    }

    /// <summary>An item on a base record the game data says nothing about.</summary>
    private long AddUnparsedItem(string name, string? prefix, string? suffix) {
        Execute("INSERT INTO PlayerItem (baserecord, PrefixRecord, SuffixRecord, MateriaRecord, "
                + "Seed, Name, namelowercase) VALUES "
                + $"('records/items/from/a/mod/nobody/parsed.dbr', '{prefix ?? ""}', '{suffix ?? ""}', "
                + $"'', 0, '{name.Replace("'", "''")}', '{name.Replace("'", "''").ToLowerInvariant()}')");

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid();";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// The tooltip the game drew for an item, as the hook captures it: text class 6 is the name
    /// line, and it still carries the game's colour codes.
    /// </summary>
    private void AddTooltip(long item, string nameLine) {
        Execute($"INSERT INTO ReplicaItem2 (playeritemid) VALUES ({item})");

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid();";
        var replica = (long)command.ExecuteScalar()!;

        Execute("INSERT INTO ReplicaItemRow (replicaitemid, Type, Text, TextLowercase) VALUES "
                + $"({replica}, 6, '{nameLine.Replace("'", "''")}', "
                + $"'{nameLine.Replace("'", "''").ToLowerInvariant()}')");
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

    /// <summary>
    /// The import path reaches the same case, and is the one a player actually hits: a component
    /// is one of the records <c>Records()</c> offers, so an item whose base record is unparsed
    /// still passes the "some record is known" gate on the component alone, and gets described.
    /// It must be described without being renamed after the thing in its socket.
    /// </summary>
    [Fact]
    public void ImportOfAnUnknownRecordWithAComponentKeepsItsStoredName() {
        Execute("INSERT INTO PlayerItem (baserecord, PrefixRecord, SuffixRecord, MateriaRecord, "
                + "Seed, Name, namelowercase) VALUES "
                + $"('records/items/from/a/mod/nobody/parsed.dbr', '', '', '{Component}', 0, "
                + "'Modded Blade', 'modded blade')");

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT last_insert_rowid();";
        var item = (long)command.ExecuteScalar()!;

        NewItemDetails.Apply(_connection, [item]);

        Assert.Equal("Modded Blade", NameOf(item));
        Assert.Equal("modded blade", LowercaseOf(item));
    }
}
