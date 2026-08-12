using Microsoft.Data.Sqlite;

namespace IAGrim.Host;

/// <summary>One legendary/epic the game defines, and how many of it the player owns.</summary>
public sealed record CollectionEntry(
    string BaseRecord,
    string? Name,
    string? Icon,
    string Quality,
    int NumOwnedSc,
    int NumOwnedHc);

/// <summary>How many owned items fall in each rarity/slot bucket.</summary>
public sealed record CollectionAggregateRow(int Num, string Quality, string Slot);

/// <summary>A set, the items in it, and how much of it the player has.</summary>
public sealed record SetEntry(string SetRecord, string Name, IReadOnlyList<SetMember> Items) {
    public int OwnedCount => Items.Count(i => i.Owned);
    public int TotalCount => Items.Count;
}

public sealed record SetMember(string BaseRecord, string? Name, string? Icon, bool Owned);

/// <summary>
/// The "what am I missing" views, as distinct from item search: a checklist of every legendary
/// and epic in the game against what the player owns.
///
/// Ported from upstream's <c>ItemCollectionDaoImpl</c> (collection and aggregates) and
/// <c>DatabaseItemDaoImpl.GetItemSetAssociations</c> (sets). Upstream's SQL reads from
/// <c>DatabaseItemStat_v2</c>, pivoting stat rows at query time; this schema stores the same
/// handful of fields as columns on ItemTemplate, so the shape differs while the selection does
/// not. Each such spot is marked PORT:.
/// </summary>
public sealed class CollectionViewService {
    private readonly string _databasePath;

    public CollectionViewService(string databasePath) {
        _databasePath = databasePath;

        // A database this port has never opened — most importantly, one copied from a Windows
        // IAGD install — has upstream's tables but not the few this port adds. Creating them
        // here means the very first search works, rather than throwing on a missing
        // ItemTemplate. They are empty until 'iagd parse' runs; names still resolve because
        // upstream stores the composed name on the item itself.
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        IAGrim.Platform.Schema.Apply(connection);
    }

    private SqliteConnection Open() {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Every legendary and epic the game defines, with owned counts.
    ///
    /// Upstream's filter, kept intact: itemClassification is Legendary or Epic, the record is
    /// not under /crafting/, and the item has a name. The crafting exclusion matters — blueprint
    /// records carry the same classification as the item they produce, so without it every
    /// legendary appears twice.
    /// </summary>
    public IReadOnlyList<CollectionEntry> Collection(ItemQuery query) {
        var fragments = new List<string>();
        var parameters = new Dictionary<string, object>();

        // This view browses the game's items rather than the player's, so it is scoped to one
        // mod's template set rather than resolved per item. Null means vanilla, matching the
        // search filter's convention.
        fragments.Add("AND t.Mod = :templateMod");
        parameters["templateMod"] = query.Mod ?? "";

        if (query.Slot.Count > 0) {
            // Upstream: AND TextValue in ( :class ). SQLite cannot parameterise an IN list, and
            // the values reach here from a query string, so they are restricted to the shape an
            // item class actually has rather than escaped.
            var classes = string.Join(", ", query.Slot
                .Where(c => c.Length is > 0 and <= 64 && c.All(ch => char.IsLetterOrDigit(ch) || ch is '_'))
                .Select(c => $"'{c}'"));
            if (classes.Length > 0) fragments.Add($"AND t.ItemClass IN ({classes})");
        }

        if (!string.IsNullOrWhiteSpace(query.Wildcard)) {
            // PORT: upstream keeps a precomputed namelowercase column; LOWER() here instead.
            fragments.Add("AND LOWER(t.Name) LIKE :name");
            parameters["name"] = $"%{query.Wildcard.Trim().ToLowerInvariant()}%";
        }

        if (query.MinimumLevel > 0) {
            fragments.Add("AND t.LevelRequirement >= :minlevel");
            parameters["minlevel"] = query.MinimumLevel;
        }
        if (query.MaximumLevel is > 0 and < 120) {
            fragments.Add("AND t.LevelRequirement <= :maxlevel");
            parameters["maxlevel"] = query.MaximumLevel;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.Record, t.Name, t.IconFile, t.Classification,
                   (SELECT COUNT(*) FROM PlayerItem p
                     WHERE p.baserecord = t.Record AND IFNULL(p.Mod,'') = t.Mod
                       AND NOT p.IsHardcore) AS NumOwnedSc,
                   (SELECT COUNT(*) FROM PlayerItem p
                     WHERE p.baserecord = t.Record AND IFNULL(p.Mod,'') = t.Mod
                       AND p.IsHardcore) AS NumOwnedHc
            FROM ItemTemplate t
            WHERE (t.Classification = 'Legendary' OR t.Classification = 'Epic')
              AND t.Record NOT LIKE '%/crafting/%'
              AND t.Name IS NOT NULL
              AND t.Name != ''
              {string.Join("\n              ", fragments)}
            ORDER BY t.Name ASC;
            """;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(":" + key, value);

        var results = new List<CollectionEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            results.Add(new CollectionEntry(
                BaseRecord: reader.GetString(0),
                Name:       reader.IsDBNull(1) ? null : reader.GetString(1),
                Icon:       reader.IsDBNull(2) ? null : reader.GetString(2),
                Quality:    reader.IsDBNull(3) ? "" : reader.GetString(3),
                NumOwnedSc: reader.GetInt32(4),
                NumOwnedHc: reader.GetInt32(5)));
        }
        return results;
    }

    /// <summary>
    /// How many of the player's items are purple, blue, green, by slot.
    ///
    /// Upstream's <c>GetItemAggregateStats</c>. The quality label appends the affix count for
    /// greens, so "Green2" (a double-rare) is a separate bucket from "Green" — that distinction
    /// is the whole point of the PrefixRarity column.
    /// </summary>
    public IReadOnlyList<CollectionAggregateRow> Aggregate() {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(p.Id) AS Num,
                   p.Rarity || (CASE WHEN p.PrefixRarity <= 1 THEN '' ELSE p.PrefixRarity END) AS Quality,
                   COALESCE(tm.ItemClass, tv.ItemClass) AS Slot
            FROM PlayerItem p
            -- Resolved mod-first, exactly as the search does. An inner join on the item's own
            -- mod would silently drop every modded item that uses a base-game record, which is
            -- most of them.
            LEFT JOIN ItemTemplate tm ON tm.Record = p.baserecord AND tm.Mod = IFNULL(p.Mod, '')
            LEFT JOIN ItemTemplate tv ON tv.Record = p.baserecord AND tv.Mod = ''
            WHERE COALESCE(tm.ItemClass, tv.ItemClass) IS NOT NULL
              AND p.Rarity != 'White'
              AND p.Rarity != 'Yellow'
              AND p.Rarity != 'Unknown'
            GROUP BY p.Rarity, COALESCE(tm.ItemClass, tv.ItemClass)
            ORDER BY Slot, p.Rarity;
            """;

        var results = new List<CollectionAggregateRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            results.Add(new CollectionAggregateRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2)));
        }
        return results;
    }

    /// <summary>
    /// Item sets, with which pieces the player owns.
    ///
    /// Upstream's <c>GetItemSetAssociations</c> resolves an item's <c>itemSetName</c> to the set
    /// record, then that record's <c>setName</c> tag to a display name — two hops, because the
    /// set's name lives on the set rather than on its members. Both hops are resolved during the
    /// game-data parse here and stored as ItemTemplate.SetRecord / SetName.
    ///
    /// Upstream exposes this as a flat record→set list consumed by the UI; the grouping and the
    /// owned counts are this port's, since there is no WinForms grid to hand it to.
    /// </summary>
    public IReadOnlyList<SetEntry> Sets(string? nameFilter = null) {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.SetRecord, s.SetName, t.Record, t.Name, t.IconFile,
                   EXISTS (SELECT 1 FROM PlayerItem p
                            WHERE p.baserecord = t.Record AND IFNULL(p.Mod,'') = t.Mod) AS Owned
            FROM ItemTemplate t
            JOIN ItemTemplate s ON s.Record = t.SetRecord AND s.Mod = t.Mod
            WHERE t.SetRecord IS NOT NULL
              AND s.SetName IS NOT NULL
              AND ($filter IS NULL
                   OR LOWER(s.SetName) LIKE '%' || LOWER($filter) || '%'
                   OR LOWER(t.Name) LIKE '%' || LOWER($filter) || '%')
            ORDER BY s.SetName, t.Name;
            """;
        command.Parameters.AddWithValue("$filter", (object?)nameFilter ?? DBNull.Value);

        var sets = new Dictionary<string, (string Name, List<SetMember> Items)>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            var setRecord = reader.GetString(0);
            var setName   = reader.GetString(1);
            if (!sets.TryGetValue(setRecord, out var entry)) {
                entry = (setName, []);
                sets[setRecord] = entry;
            }
            entry.Items.Add(new SetMember(
                BaseRecord: reader.GetString(2),
                Name:       reader.IsDBNull(3) ? null : reader.GetString(3),
                Icon:       reader.IsDBNull(4) ? null : reader.GetString(4),
                Owned:      reader.GetInt64(5) != 0));
        }

        return sets
            .Select(kv => new SetEntry(kv.Key, kv.Value.Name, kv.Value.Items))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
