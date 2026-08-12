using IAGrim.Platform;
using Microsoft.Data.Sqlite;

namespace IAGrim.Core.Backup;

public sealed record MergeResult(int Considered, int Imported, int Duplicates, int Rejected);

/// <summary>How far a merge has got. <paramref name="Total"/> is known before the first row.</summary>
public sealed record MergeProgress(int Done, int Total, int Imported);

/// <summary>
/// Merges another Item Assistant collection into the current one, skipping exact duplicates.
///
/// This is for putting two collections together — a Windows database and a Linux one, or two
/// machines' worth of looting — without either overwriting the other.
///
/// **What counts as a duplicate.** Every field that describes what an item *is*: its records,
/// its seeds, reroll counts, stack size, and which branch it belongs to (mod, hardcore). Not
/// the row id, and not when it was looted — those describe the row rather than the item, and two
/// collections will never agree on them.
///
/// Base record plus seed is *not* enough here, though it is what the loot importer uses. That
/// works for live capture, where the game will not hand out the same seed twice for genuinely
/// different items. It is wrong for a merge: everything that does not roll — components,
/// potions, crafting materials, quest items — has a seed of zero, so record+seed would collapse
/// every stack of aether shards in both collections into one. Measured on a real stash, five
/// items carried seed 0.
///
/// The trade-off is the other way round: two genuinely identical items, looted separately, merge
/// into one. That is the safer error. An item wrongly dropped can be looted again; an item
/// wrongly duplicated silently inflates a collection and there is no way to tell afterwards
/// which copy was real.
/// </summary>
public static class CollectionMerge {
    /// <summary>
    /// The columns that describe the item rather than the row, each with the value to use when it
    /// is absent or null.
    ///
    /// **Absent matters.** Upstream has added columns over the years, and a collection left by an
    /// older version genuinely lacks them — a database written before AffixRerollsUsed existed is
    /// still a real collection someone wants merged in. Selecting a missing column is an error
    /// rather than a null, so the source query is built from the columns that are actually there
    /// and the rest are substituted here. The substituted value is the same one IFNULL supplies
    /// on the destination, so a column that one side never had still compares equal.
    /// </summary>
    private static readonly (string Column, string Default)[] IdentityColumns = [
        ("baserecord", "''"), ("PrefixRecord", "''"), ("SuffixRecord", "''"),
        ("ModifierRecord", "''"), ("TransmuteRecord", "''"), ("MateriaRecord", "''"),
        ("RelicCompletionBonusRecord", "''"), ("EnchantmentRecord", "''"),
        ("AscendantAffixNameRecord", "''"), ("AscendantAffix2hNameRecord", "''"),
        ("Seed", "0"), ("RelicSeed", "0"), ("EnchantmentSeed", "0"),
        ("RerollsUsed", "0"), ("AffixRerollsUsed", "0"),
        ("StackCount", "1"), ("Mod", "''"), ("IsHardcore", "0"),
    ];

    /// <summary>
    /// The identity columns as a SELECT list. Columns outside <paramref name="present"/> become
    /// their default rather than a reference SQLite would reject.
    /// </summary>
    private static string IdentitySelect(ISet<string>? present = null) =>
        string.Join(", ", IdentityColumns.Select(c =>
            present is null || present.Contains(c.Column)
                ? $"IFNULL({c.Column},{c.Default})"
                : c.Default));

    /// <summary>
    /// Reads <paramref name="sourcePath"/> and adds whatever the current collection does not
    /// already have.
    /// </summary>
    /// <param name="dryRun">Report what would happen without writing anything.</param>
    /// <param name="progress">
    /// Called once per source row. A merge of a large collection takes long enough that a caller
    /// needs to be able to show it moving; the callback is deliberately unthrottled, since only
    /// the caller knows what its own reporting costs.
    /// </param>
    public static MergeResult Merge(string databasePath, string sourcePath, bool dryRun = false,
                                    Action<MergeProgress>? progress = null) {
        if (!File.Exists(sourcePath)) {
            throw new FileNotFoundException($"no such database: {sourcePath}");
        }
        if (Path.GetFullPath(sourcePath) == Path.GetFullPath(databasePath)) {
            throw new InvalidDataException("the source and the destination are the same database");
        }

        // Read-only: a merge must never modify the collection being merged *from*.
        using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
        source.Open();

        if (!HasPlayerItems(source)) {
            throw new InvalidDataException($"{sourcePath} is not an Item Assistant database");
        }

        // What we already have, by identity. Held in memory because a merge compares every
        // source row against all of them, and a collection is thousands of rows rather than
        // millions.
        //
        // Read through its own short-lived connection, which is then closed before any writing
        // starts. SQLite takes one writer per database, and LootStore.Insert opens a transaction
        // on its own connection — holding a second one here deadlocks the merge against itself.
        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var destination = new SqliteConnection($"Data Source={databasePath}")) {
            destination.Open();
            Schema.Apply(destination);

            using var command = destination.CreateCommand();
            command.CommandText = $"SELECT {IdentitySelect()} FROM PlayerItem;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) existing.Add(Identity(reader));
        }

        // What this source actually carries, which decides how the query below is written.
        var columns = ColumnsOf(source, "PlayerItem");

        // Counted up front so progress can be a proportion rather than a spinner. One extra scan
        // of an indexed table, against a merge that reads every row anyway.
        var total = CountItems(source);

        var considered = 0;
        var imported = 0;
        var duplicates = 0;
        var rejected = 0;

        // The single writer. Each insert is its own transaction, which is what makes a
        // half-finished merge leave a coherent collection rather than a torn one.
        using var store = new LootStore(databasePath);

        using (var command = source.CreateCommand()) {
            command.CommandText = $"""
                SELECT {IdentitySelect(columns)}, Id, IFNULL(Name,'')
                FROM PlayerItem ORDER BY Id;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                considered++;
                progress?.Invoke(new MergeProgress(considered, Math.Max(total, considered), imported));

                var identity = Identity(reader);
                if (!existing.Add(identity)) { duplicates++; continue; }

                var item = ReadItem(reader) with { KnownName = Blank(reader, 19) };
                if (string.IsNullOrEmpty(item.BaseRecord)) { rejected++; continue; }

                if (dryRun) { imported++; continue; }

                var sourceId = reader.GetInt64(18);
                var newId = store.Insert(item with { Stats = ReadReplica(source, sourceId) });
                if (newId > 0) imported++;
            }
        }

        // The last report always lands, so a caller showing a bar can finish it rather than
        // leaving it stuck a few rows short.
        progress?.Invoke(new MergeProgress(considered, Math.Max(total, considered), imported));

        return new MergeResult(considered, imported, duplicates, rejected);
    }

    /// <summary>The column names of a table, for telling an older collection from a current one.</summary>
    private static HashSet<string> ColumnsOf(SqliteConnection connection, string table) {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static int CountItems(SqliteConnection connection) {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PlayerItem;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static bool HasPlayerItems(SqliteConnection connection) {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND lower(name)='playeritem';";
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// A single comparable string for the identity columns. Unit separators rather than a plain
    /// join, so that a record ending where the next begins cannot forge a match.
    /// </summary>
    private static string Identity(SqliteDataReader reader) {
        var parts = new string[18];
        for (var i = 0; i < 18; i++) {
            parts[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
        }
        return string.Join('', parts);
    }

    /// <summary>A text column, with empty read as absent.</summary>
    private static string? Blank(SqliteDataReader reader, int index) {
        if (reader.IsDBNull(index)) return null;
        var text = reader.GetString(index);
        return text.Length == 0 ? null : text;
    }

    private static LootedItem ReadItem(SqliteDataReader reader) {
        string Text(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i);
        long Number(int i) => reader.IsDBNull(i) ? 0 : Convert.ToInt64(reader.GetValue(i));
        string? Blank(int i) => Text(i).Length == 0 ? null : Text(i);

        return new LootedItem {
            BaseRecord                 = Text(0),
            PrefixRecord               = Blank(1),
            SuffixRecord               = Blank(2),
            ModifierRecord             = Blank(3),
            TransmuteRecord            = Blank(4),
            MateriaRecord              = Blank(5),
            RelicCompletionBonusRecord = Blank(6),
            EnchantmentRecord          = Blank(7),
            AscendantAffixNameRecord   = Blank(8),
            AscendantAffix2hNameRecord = Blank(9),
            Seed                       = Number(10),
            RelicSeed                  = Number(11),
            EnchantmentSeed            = Number(12),
            RerollsUsed                = Number(13),
            AffixRerollsUsed           = Number(14),
            StackCount                 = Math.Max(1, Number(15)),
            Mod                        = Text(16),
            IsHardcore                 = Number(17) != 0,
            Stats                      = [],
        };
    }

    /// <summary>
    /// The tooltip lines the source captured, so a merged item keeps the game's own rendering
    /// rather than needing the replica pathway to ask for it again.
    /// </summary>
    private static List<LootStat> ReadReplica(SqliteConnection source, long playerItemId) {
        var stats = new List<LootStat>();
        try {
            using var command = source.CreateCommand();
            command.CommandText = """
                SELECT rr.Type, rr.Text FROM ReplicaItemRow rr
                 JOIN ReplicaItem2 r ON r.Id = rr.replicaitemid
                WHERE r.playeritemid = $id
                ORDER BY rr.Id;
                """;
            command.Parameters.AddWithValue("$id", playerItemId);

            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                if (!reader.IsDBNull(1)) stats.Add(new LootStat(reader.GetInt32(0), reader.GetString(1)));
            }
        }
        catch (SqliteException) { /* older source without replica tables */ }
        return stats;
    }
}
