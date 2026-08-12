using IAGrim.Core.GameData;
using IAGrim.Platform;
using Microsoft.Data.Sqlite;

namespace IAGrim.Host;

/// <summary>
/// Read/write access to the collection for the API.
///
/// Every SQLite connection is opened per operation rather than held: the loot watcher writes
/// on a background timer while requests read, and WAL plus short-lived connections avoids
/// having to serialise them behind a lock.
/// </summary>
public sealed class CollectionService {
    private readonly string _databasePath;

    public CollectionService(string databasePath) {
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
    /// Paged search. Paging is not decoration: the UI loads on scroll, and a full collection
    /// is thousands of rows.
    /// </summary>
    public ItemPage Search(ItemQuery query, int skip, int take) {
        take = Math.Clamp(take, 1, 500);
        skip = Math.Max(skip, 0);

        using var connection = Open();
        var (where, parameters) = ItemQueryBuilder.Build(query);

        // The FROM/WHERE body is shared verbatim between the row query and the count, so the
        // total can never drift from what the paged query actually returns. Upstream makes
        // the same point in a comment on SearchForItems.
        // The LEFT JOIN on ReplicaItem2 is upstream's; the wildcard filter reaches the item's
        // tooltip lines through it.
        //
        // The template is joined twice — once for the item's own mod, once for vanilla — and the
        // columns are COALESCEd. A mod ships only the records it adds or changes and layers over
        // the base game, so a modded item with a base-game record has no mod template and must
        // fall back. Two indexed joins beat a correlated subquery per row, and unlike a subquery
        // they stay readable next to upstream's SQL.
        const string from = """
            FROM PlayerItem p
            LEFT JOIN ItemTemplate tm ON tm.Record = p.baserecord AND tm.Mod = IFNULL(p.Mod, '')
            LEFT JOIN ItemTemplate tv ON tv.Record = p.baserecord AND tv.Mod = ''
            LEFT OUTER JOIN ReplicaItem2 r ON p.Id = r.playeritemid
            """;

        int total;
        using (var count = connection.CreateCommand()) {
            count.CommandText = $"SELECT COUNT(*) {from} WHERE {where};";
            Bind(count, parameters);
            total = Convert.ToInt32(count.ExecuteScalar());
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.Id, p.Name, p.baserecord, p.Seed, p.IsHardcore,
                   COALESCE(tm.Name, tv.Name), COALESCE(tm.ItemClass, tv.ItemClass), COALESCE(tm.Quality, tv.Quality), COALESCE(tm.LevelRequirement, tv.LevelRequirement), COALESCE(tm.IconFile, tv.IconFile),
                   (SELECT rr.Text FROM ReplicaItemRow rr
                     WHERE rr.replicaitemid = r.Id AND rr.Type = 6
                     ORDER BY rr.Id LIMIT 1) AS RawName,
                   p.Rarity, p.PrefixRarity, p.StackCount
            {from}
            WHERE {where}
            ORDER BY p.LevelRequirement DESC, p.Id DESC
            LIMIT $take OFFSET $skip;
            """;
        Bind(command, parameters);
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$skip", skip);

        var items = new List<ItemSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            items.Add(ReadSummary(reader));
        }

        return new ItemPage(items, total, skip, take);
    }

    /// <summary>
    /// Query fragments use :name style, matching upstream's wording so the two stay
    /// diffable; Microsoft.Data.Sqlite accepts that prefix.
    /// </summary>
    private static void Bind(SqliteCommand command, Dictionary<string, object> parameters) {
        foreach (var (key, value) in parameters) {
            command.Parameters.AddWithValue(":" + key, value);
        }
    }

    public ItemDetail? Get(long id) {
        using var connection = Open();

        ItemSummary summary;
        using (var command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT p.Id, p.Name, p.baserecord, p.Seed, p.IsHardcore,
                       COALESCE(tm.Name, tv.Name), COALESCE(tm.ItemClass, tv.ItemClass), COALESCE(tm.Quality, tv.Quality), COALESCE(tm.LevelRequirement, tv.LevelRequirement), COALESCE(tm.IconFile, tv.IconFile),
                       (SELECT rr.Text FROM ReplicaItemRow rr
                         WHERE rr.replicaitemid = r.Id AND rr.Type = 6
                         ORDER BY rr.Id LIMIT 1) AS RawName,
                       p.Rarity, p.PrefixRarity, p.StackCount
                FROM PlayerItem p
                LEFT JOIN ItemTemplate tm ON tm.Record = p.baserecord AND tm.Mod = IFNULL(p.Mod, '')
                LEFT JOIN ItemTemplate tv ON tv.Record = p.baserecord AND tv.Mod = ''
                LEFT OUTER JOIN ReplicaItem2 r ON p.Id = r.playeritemid
                WHERE p.Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            summary = ReadSummary(reader);
        }

        var stats = new List<ItemStatLine>();
        using (var command = connection.CreateCommand()) {
            command.CommandText =
                """
                SELECT rr.Type, rr.Text FROM ReplicaItemRow rr
                 JOIN ReplicaItem2 r ON r.Id = rr.replicaitemid
                WHERE r.playeritemid = $id
                ORDER BY rr.Id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                stats.Add(new ItemStatLine(reader.GetInt32(0), reader.GetString(1)));
            }
        }

        // The skill the base record grants, if any. Upstream matches on the base record only —
        // its own TODO wonders whether affixes can grant skills — and that is preserved.
        ItemSkillInfo? skill = null;
        using (var command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT s.Name, s.Description, IFNULL(s.Level, 0), s.Trigger,
                       EXISTS (SELECT 1 FROM DatabaseItemStat_v2 st
                                WHERE st.id_databaseitem = s.id_databaseitem
                                  AND st.Stat = 'spawnObjects')
                FROM PlayerItem p
                JOIN DatabaseItem_v2 db ON db.baserecord = p.baserecord
                JOIN itemskill_mapping m ON m.id_databaseitem = db.id_databaseitem
                JOIN itemskill_v2 s ON s.id_skill = m.id_skill
                WHERE p.Id = $id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", id);
            try {
                using var reader = command.ExecuteReader();
                if (reader.Read()) {
                    skill = new ItemSkillInfo(
                        reader.IsDBNull(0) ? null : reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetInt64(4) != 0);
                }
            }
            catch (SqliteException) { /* skills not parsed yet */ }
        }

        return new ItemDetail(summary, stats, skill);
    }

    /// <summary>
    /// Display name, in decreasing order of fidelity:
    ///
    ///   1. the raw first tooltip line, which still carries Grim Dawn's colour codes and so
    ///      conveys rarity — the thing players actually scan for;
    ///   2. the stripped looted name;
    ///   3. the template name, which is the unrolled base ("Plagueborne Revolver" rather than
    ///      "Mythical Plagueborne Revolver"), since the game composes the displayed name from
    ///      the base plus its quality tier.
    /// </summary>
    private static ItemSummary ReadSummary(SqliteDataReader reader) {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

        var rawName = Text(10);
        var lootedName = Text(1);
        var templateName = Text(5);

        return new ItemSummary(
            Id:         reader.GetInt64(0),
            Name:       rawName ?? lootedName ?? templateName ?? "Unknown Item",
            BaseRecord: reader.GetString(2),
            Seed:       reader.GetInt64(3),
            IsHardcore: reader.GetInt64(4) != 0,
            ItemClass:  Text(6),
            Quality:    Text(7),
            Level:      reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            Icon:       Text(9),
            Rarity:     Text(11),
            PrefixRarity: reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
            StackCount: reader.IsDBNull(13) ? 1 : Math.Max(1, reader.GetInt64(13)));
    }

    /// <summary>
    /// Grim Dawn's masteries, discovered from the tag table rather than hardcoded — expansions
    /// add masteries, and a fixed list would silently omit them.
    ///
    /// Upstream's <c>GetValidClassItemTags</c>, including its normalisations: a base-game tag
    /// <c>tagSkillClassName03</c> and an expansion tag <c>tagGDX1Class07SkillName00A</c> both
    /// reduce to a class id, and dual-class combinations (four digits, "Witchblade") are
    /// dropped because they are not masteries an item can grant bonuses to.
    /// </summary>
    public IReadOnlyList<object> Classes() {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Tag, Name FROM ItemTag
             WHERE (Tag LIKE 'tagSkillClassName%' OR Tag LIKE 'tag%Class%SkillName00A')
               AND LENGTH(Name) > 1
             ORDER BY Tag;
            """;

        var classes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                var tag = reader.GetString(0);
                var name = reader.GetString(1);

                var id = System.Text.RegularExpressions.Regex.IsMatch(tag, @"tagGDX\d+Class(\d+)SkillName00A")
                    ? System.Text.RegularExpressions.Regex.Replace(tag, @"tagGDX\d+Class(\d+)SkillName00A", "class$1")
                    : tag.Replace("tagSkillClassName", "class");

                // Four-digit ids are dual-class combinations, which no item grants a bonus to.
                var digits = id["class".Length..];
                if (digits.Length is 0 or > 3 || !digits.All(char.IsDigit)) continue;

                // A localised name may carry gender variants; reduce it the same way item names are.
                classes[id] = StatTranslator.ItemNameCombinator.FilterGenderTag(name);
            }
        }
        catch (SqliteException) { /* tags not parsed yet */ }

        return classes
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => (object)new { Id = c.Key, Name = c.Value })
            .ToList();
    }

    /// <summary>
    /// Mods that matter here: any the player owns items from, plus any whose templates have been
    /// parsed. Both, because an installed-but-unplayed mod should be selectable, and a mod
    /// uninstalled since should not hide the items still in the collection.
    ///
    /// Vanilla is the empty string, matching PlayerItem.Mod and upstream's convention.
    /// </summary>
    public IReadOnlyList<object> Mods() {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Mod, SUM(Items) AS Items FROM (
                SELECT IFNULL(Mod, '') AS Mod, COUNT(*) AS Items FROM PlayerItem GROUP BY IFNULL(Mod, '')
                UNION ALL
                SELECT DISTINCT Mod, 0 FROM ItemTemplate
            )
            GROUP BY Mod
            ORDER BY (Mod = '') DESC, Mod;
            """;

        var mods = new List<object>();
        try {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                var name = reader.IsDBNull(0) ? "" : reader.GetString(0);
                mods.Add(new { Name = name, Items = reader.GetInt32(1) });
            }
        }
        catch (SqliteException) { /* not parsed yet */ }

        return mods;
    }

    public HostStatus Status(SteamPaths paths, PrefixBridge bridge, DateTime? gameStartedAt,
                             AppSettings? settings = null, bool attaching = false) {
        using var connection = Open();

        int Count(string table) {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            try { return Convert.ToInt32(command.ExecuteScalar()); }
            catch (SqliteException) { return 0; }   // table not created yet
        }

        string? Staleness(SteamPaths p, AppSettings? s) {
            s ??= AppSettings.Load();
            try {
                return IAGrim.Core.GameData.GameDataStatus
                    .Check(_databasePath, s.GameDir ?? p.GameDir, s.Language).Reason;
            }
            catch (Exception) { return null; }   // never let a status call fail the UI
        }

        int NeedingStats() {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM PlayerItem WHERE Rarity IS NULL;";
            try { return Convert.ToInt32(command.ExecuteScalar()); }
            catch (SqliteException) { return 0; }   // column not added yet
        }

        return new HostStatus(
            GameRunning:      gameStartedAt is not null,
            GameStartedAt:    gameStartedAt,
            HookAttached:     bridge.IsHookLive(gameStartedAt),
            PendingLootFiles: bridge.PendingLootFiles().Count(),
            ItemCount:        Count("PlayerItem"),
            TemplateCount:    Count("ItemTemplate"),
            GameDir:          paths.GameDir,
            BridgeDir:        bridge.Root,
            DatabaseFile:     _databasePath,
            ItemsNeedingStats: NeedingStats(),
            GameDataStale:    Staleness(paths, settings),
            Attaching:        attaching);
    }
}
