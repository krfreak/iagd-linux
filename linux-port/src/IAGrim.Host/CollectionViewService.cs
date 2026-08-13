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
/// One component, and what it does.
///
/// <paramref name="Slots"/> is what the record says it may be socketed into, read from the flags
/// the game sets on it (<c>chest</c>, <c>sword2h</c>, …) rather than from its FileDescription,
/// which is developer text and says things like "All Armor (renamed to Antivenom Salve)".
/// </summary>
public sealed record ComponentEntry(
    string BaseRecord,
    string? Name,
    string? Icon,
    int LevelRequirement,
    IReadOnlyList<string> Slots,
    ItemSkillInfo? Skill,
    IReadOnlyList<ItemStatLine> Stats,
    int NumOwned);

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

    /// <summary>Renders a component's stat lines; see <see cref="Components"/>.</summary>
    private readonly IAGrim.Core.ItemStats.ItemStatText _statText;

    public CollectionViewService(string databasePath) {
        _databasePath = databasePath;
        _statText = new IAGrim.Core.ItemStats.ItemStatText(databasePath);

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

    /// <summary>
    /// Every component in the game, with what it grants and where it can go.
    ///
    /// **Upstream has no such page.** Its "Components" nav entry opens
    /// <c>grimdawn.evilsoft.net/enchantments/</c> in a browser — a site belonging to the same
    /// author, which this port does not send anyone to for the reasons its nav carries no
    /// Discord or Patreon link either. Everything the page needs is in Grim Dawn's own data,
    /// which this client already reads, so it is built here instead of linked away.
    ///
    /// Components share <c>records/items/materia/</c> with the crafting materials. A component
    /// is the one that says what it can be socketed into; a Scrap Metal says nothing, and is
    /// left out.
    /// </summary>
    public IReadOnlyList<ComponentEntry> Components(string? nameFilter = null) {
        using var connection = Open();

        var slots = SlotFlags(connection);
        var owned = OwnedCounts(connection);
        var skills = new Dictionary<string, ItemSkillInfo>(StringComparer.OrdinalIgnoreCase);

        using (var command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT db.baserecord, s.Name, s.Description, IFNULL(s.Level, 0), s.Trigger,
                       EXISTS (SELECT 1 FROM DatabaseItemStat_v2 st
                                WHERE st.id_databaseitem = s.id_databaseitem
                                  AND st.Stat = 'spawnObjects')
                FROM DatabaseItem_v2 db
                JOIN itemskill_mapping m ON m.id_databaseitem = db.id_databaseitem
                JOIN itemskill_v2 s ON s.id_skill = m.id_skill
                WHERE db.baserecord LIKE '%/materia/%';
                """;
            try {
                using var reader = command.ExecuteReader();
                while (reader.Read()) {
                    skills[reader.GetString(0)] = new ItemSkillInfo(
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetInt64(5) != 0);
                }
            }
            catch (SqliteException) { /* skills not parsed yet */ }
        }

        var components = new List<ComponentEntry>();
        using (var command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT Record, Name, IconFile, IFNULL(LevelRequirement, 0)
                FROM ItemTemplate
                WHERE Mod = '' AND Record LIKE '%/materia/%' AND Name IS NOT NULL
                ORDER BY Name;
                """;

            try {
                using var reader = command.ExecuteReader();
                while (reader.Read()) {
                    var record = reader.GetString(0);
                    if (!slots.TryGetValue(record, out var fits) || fits.Count == 0) continue;

                    components.Add(new ComponentEntry(
                        BaseRecord:       record,
                        Name:             reader.IsDBNull(1) ? null : reader.GetString(1),
                        Icon:             reader.IsDBNull(2) ? null : reader.GetString(2),
                        LevelRequirement: (int)reader.GetDouble(3),
                        Slots:            fits,
                        Skill:            skills.GetValueOrDefault(record),
                        Stats:            [],
                        NumOwned:         owned.GetValueOrDefault(record)));
                }
            }
            catch (SqliteException) { return []; }
        }

        // The stat lines, through the same renderer an item's card uses.
        var described = components
            .Select(component => component with {
                Stats = _statText.Available
                    ? _statText.DescribeRecord(connection, component.BaseRecord)
                        .Select(line => new ItemStatLine(
                            line.TextClass, line.Text,
                            line.Section?.ToString().ToLowerInvariant(),
                            line.Modifier, line.Label, line.Skill, line.Extras))
                        .ToList()
                    : [],
            })
            .ToList();

        if (string.IsNullOrWhiteSpace(nameFilter)) return described;

        // Filtered here rather than in SQL, and on everything the card shows: the useful search
        // on a components page is "which one gives lightning damage", and no component is
        // *named* Lightning. A hundred rows make the cost of doing it in memory irrelevant.
        var needle = nameFilter.Trim();
        bool Matches(ComponentEntry component) =>
            Contains(component.Name, needle)
            || component.Slots.Any(slot => Contains(slot, needle))
            || Contains(component.Skill?.Name, needle)
            || Contains(component.Skill?.Description, needle)
            || component.Stats.Any(stat => Contains(stat.Text, needle));

        return described.Where(Matches).ToList();
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What each component may be socketed into, from the flags the record carries.
    ///
    /// The game marks a component with one flag per item type it fits — <c>chest</c>,
    /// <c>sword2h</c>, <c>ranged1h</c>. Records under materia that carry none of them are the
    /// crafting materials, and are how a component is told apart from a Scrap Metal.
    /// </summary>
    private static Dictionary<string, List<string>> SlotFlags(SqliteConnection connection) {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT db.baserecord, dbs.stat
            FROM DatabaseItem_v2 db
            JOIN DatabaseItemStat_v2 dbs ON dbs.id_databaseitem = db.id_databaseitem
            WHERE db.baserecord LIKE '%/materia/%'
              AND dbs.stat IN ({string.Join(", ", ComponentSlots.Select(s => $"'{s}'"))})
              AND dbs.val1 > 0;
            """;
        try {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                var record = reader.GetString(0);
                if (!result.TryGetValue(record, out var list)) result[record] = list = [];
                list.Add(reader.GetString(1));
            }
        }
        catch (SqliteException) { return result; }

        foreach (var list in result.Values) {
            list.Sort(StringComparer.Ordinal);
        }
        return result;
    }

    /// <summary>The item types a component can be socketed into, as the game names them.</summary>
    private static readonly string[] ComponentSlots = [
        "head", "shoulders", "chest", "hands", "waist", "legs", "feet",
        "amulet", "medal", "ring", "offhand", "shield",
        "axe", "axe2h", "dagger", "mace", "mace2h", "scepter",
        "sword", "sword2h", "spear2h", "ranged1h", "ranged2h",
    ];

    /// <summary>How many of each component the player has, loose or socketed into something.</summary>
    private static Dictionary<string, int> OwnedCounts(SqliteConnection connection) {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MateriaRecord, COUNT(*) FROM PlayerItem
            WHERE MateriaRecord IS NOT NULL AND MateriaRecord != ''
            GROUP BY MateriaRecord;
            """;
        try {
            using var reader = command.ExecuteReader();
            while (reader.Read()) result[reader.GetString(0)] = reader.GetInt32(1);
        }
        catch (SqliteException) { /* fine */ }
        return result;
    }
}
