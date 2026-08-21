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

    /// <summary>
    /// Renders the tooltip for items the hook never captured. Held for the life of the service
    /// because building it reads the whole tag table, which is 19,000 rows.
    /// </summary>
    private readonly IAGrim.Core.ItemStats.ItemStatText _statText;

    public CollectionService(string databasePath) {
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
    /// Paged search. Paging is not decoration: the UI loads on scroll, and a full collection
    /// is thousands of rows.
    /// </summary>
    /// <summary>
    /// Identical items collapse into one card, as they do upstream.
    ///
    /// The key is upstream's, from <c>ItemOperationsUtility.MergeStackSize</c>: base record plus
    /// prefix plus suffix. Two rolls of the same legendary are one entry offering "Transfer all
    /// (2)" rather than two entries side by side — which is what stops a stash of forty identical
    /// components filling the window.
    /// </summary>
    /// <summary>
    /// The tables every search reads, and the aliases <see cref="ItemQueryBuilder"/>'s fragments
    /// are written against.
    ///
    /// Internal rather than a local so that scripts/verify-search-filters.sh can run this port's
    /// own WHERE clauses against a database and compare the matched items with what upstream's
    /// SQL matches. A verification that built its own FROM would be checking a copy.
    /// </summary>
    internal const string SearchFrom = """
        FROM PlayerItem p
        LEFT JOIN ItemTemplate tm ON tm.Record = p.baserecord AND tm.Mod = IFNULL(p.Mod, '')
        LEFT JOIN ItemTemplate tv ON tv.Record = p.baserecord AND tv.Mod = ''
        LEFT OUTER JOIN ReplicaItem2 r ON p.Id = r.playeritemid
        """;

    private const string MergeKey =
        "IFNULL(p.baserecord,'') || '|' || IFNULL(p.PrefixRecord,'') || '|' || IFNULL(p.SuffixRecord,'')";

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
        const string from = SearchFrom;

        // Two totals, because they answer different questions. Paging walks *cards*, so the
        // scroll has to stop at the number of groups. What the window reports is *items*, which
        // is upstream's NumTotalItems — a COUNT over PlayerItem rows — and is the number a
        // player recognises as the size of their collection. They differ a lot: 7,483 items in
        // this collection are 3,669 cards.
        int total;
        int totalItems;
        using (var count = connection.CreateCommand()) {
            count.CommandText = $"""
                SELECT COUNT(*), IFNULL(SUM(n), 0) FROM (
                    SELECT COUNT(*) AS n {from} WHERE {where} GROUP BY {MergeKey}
                );
                """;
            Bind(count, parameters);
            using var reader = count.ExecuteReader();
            reader.Read();
            total = reader.GetInt32(0);
            totalItems = reader.GetInt32(1);
        }

        // Upstream's ordering, from PlayerItemDaoImpl.SearchForItems: name then id, with the
        // level in front when the user asks for it. Ascending in both cases — theirs is
        // "ORDER BY PI.levelrequirement, PI.name, PI.Id".
        //
        // "Newest first" is this port's own and sits in front of both: a card stands for several
        // rows, so it is dated by its most recent one — MAX(created_at) — which is what puts a
        // card back at the top when another copy of it is looted. created_at can be null on rows
        // written by older tools, so those sort as oldest rather than dropping out of the order.
        var orderBy = query.OrderByNewest
            ? "ORDER BY MAX(IFNULL(p.created_at, 0)) DESC, p.Name, MIN(p.Id)"
            : query.OrderByLevel
                ? "ORDER BY MIN(p.LevelRequirement), p.Name, MIN(p.Id)"
                : "ORDER BY p.Name, MIN(p.Id)";

        using var command = connection.CreateCommand();
        // MIN(p.Id) is not decoration: with a GROUP BY, SQLite takes the other columns from the
        // row that produced the min, so the card describes one real item rather than a mix of
        // several. Copies carries the rest.
        command.CommandText = $"""
            SELECT MIN(p.Id), p.Name, p.baserecord, p.Seed, p.IsHardcore,
                   COALESCE(tm.Name, tv.Name), COALESCE(tm.ItemClass, tv.ItemClass), COALESCE(tm.Quality, tv.Quality), p.LevelRequirement, COALESCE(tm.IconFile, tv.IconFile),
                   (SELECT rr.Text FROM ReplicaItemRow rr
                     WHERE rr.replicaitemid = r.Id AND rr.Type = 6
                     ORDER BY rr.Id LIMIT 1) AS RawName,
                   p.Rarity, p.PrefixRarity, p.StackCount,
                   COUNT(*) AS Copies, GROUP_CONCAT(p.Id) AS Ids
            {from}
            WHERE {where}
            GROUP BY {MergeKey}
            {orderBy}
            LIMIT $take OFFSET $skip;
            """;
        Bind(command, parameters);
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$skip", skip);

        var cards = new List<ItemCard>();
        var ids = new List<long>();
        using (var reader = command.ExecuteReader()) {
            while (reader.Read()) {
                var summary = ReadSummary(reader);
                var copies = reader.GetInt32(14);
                var duplicates = (reader.IsDBNull(15) ? "" : reader.GetString(15))
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(long.Parse)
                    .ToList();

                cards.Add(new ItemCard(summary, [], null, copies, duplicates));
                ids.Add(summary.Id);
            }
        }

        // The tooltip lines for the whole page in one query. Upstream renders every card fully,
        // so fetching them per card would be one round trip per item on every scroll.
        var stats = StatsFor(connection, ids);
        var skills = SkillsFor(connection, ids);
        for (var i = 0; i < cards.Count; i++) {
            var id = cards[i].Item.Id;
            var captured = stats.TryGetValue(id, out var lines) ? lines : [];

            cards[i] = cards[i] with {
                // Upstream's precedence: the captured tooltip when there is one, the computed
                // description otherwise (Item.tsx renders replicaStats *instead of* bodyStats).
                //
                // It is the better of the two by a distance, and not because of colour. The seed
                // engine only returns fields that *roll*, so a computed description has the
                // affix damage but not the weapon's own "17-41 Physical Damage", and no level or
                // attribute requirements. The captured line has everything, because the game
                // wrote it.
                //
                // What made a captured tooltip look character-specific was this port storing it
                // raw: the game's detail view carries colour codes meaning "better or worse than
                // what you are wearing" and the roll range behind each value. Upstream strips
                // both before storing, and so does this port now — see ReplicaService.Normalise.
                Stats = captured.Count > 0 ? captured : Computed(connection, id),
                Skill = skills.TryGetValue(id, out var skill) ? skill : null,
            };
        }

        return new ItemPage(cards, total, totalItems, skip, take);
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

    /// <summary>
    /// One item in the shape the list uses. For a freshly looted row, which is one card by
    /// definition — anything identical to it was already on screen under its own card.
    /// </summary>
    public ItemCard? Card(long id) {
        var detail = Get(id);
        return detail is null ? null : new ItemCard(detail.Item, detail.Stats, detail.Skill, 1, [id]);
    }

    /// <summary>
    /// The columns <see cref="ReadSummary"/> reads, for the queries that fetch whole rows rather
    /// than a group's representative. The aliases are <see cref="SearchFrom"/>'s.
    /// </summary>
    private const string SummaryColumns = """
        p.Id, p.Name, p.baserecord, p.Seed, p.IsHardcore,
        COALESCE(tm.Name, tv.Name), COALESCE(tm.ItemClass, tv.ItemClass), COALESCE(tm.Quality, tv.Quality), p.LevelRequirement, COALESCE(tm.IconFile, tv.IconFile),
        (SELECT rr.Text FROM ReplicaItemRow rr
          WHERE rr.replicaitemid = r.Id AND rr.Type = 6
          ORDER BY rr.Id LIMIT 1) AS RawName,
        p.Rarity, p.PrefixRarity, p.StackCount
        """;

    /// <summary>
    /// Several items in one round trip, each with its own tooltip, in the order asked for.
    ///
    /// This is what the comparison view reads. A card stands for every identical copy the player
    /// owns — identical meaning upstream's merge key, base record plus prefix plus suffix — and
    /// that is deliberately not the same as identical <em>stats</em>: two greens with the same
    /// affixes roll different values. Choosing which copy goes into the stash therefore means
    /// seeing each one, which is what upstream's ItemComparer shows.
    ///
    /// Upstream never needs a call like this: its search result already carries every item of
    /// every group. This port sends one card per group precisely so a page of a thousand items
    /// is not a thousand tooltips, and so pays for the copies when the player asks to see them.
    ///
    /// Ids that no longer exist are dropped rather than reported: a copy transferred from
    /// another window is gone, and the honest answer is the copies that remain.
    /// </summary>
    public IReadOnlyList<ItemDetail> Details(IReadOnlyList<long> ids) {
        // Far above any real group — the largest in a 7,600 item collection is around 25 — and
        // low enough that the id list stays a sane SQL statement.
        var wanted = ids.Distinct().Take(500).ToList();
        if (wanted.Count == 0) return [];

        using var connection = Open();

        // Inlined rather than parameterised for the same reason StatsFor inlines them: these are
        // row ids, already parsed as numbers, and SQLite caps parameter count.
        var idList = string.Join(",", wanted);

        var summaries = new Dictionary<long, ItemSummary>();
        using (var command = connection.CreateCommand()) {
            command.CommandText = $"""
                SELECT {SummaryColumns}
                {SearchFrom}
                WHERE p.Id IN ({idList});
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                var summary = ReadSummary(reader);
                summaries[summary.Id] = summary;
            }
        }

        var found = summaries.Keys.ToList();
        var stats = StatsFor(connection, found);
        var skills = SkillsFor(connection, found);

        var result = new List<ItemDetail>(summaries.Count);
        foreach (var id in wanted) {
            if (!summaries.TryGetValue(id, out var summary)) continue;

            // Search's precedence: the tooltip the game drew when there is one, the computed
            // description otherwise. A comparison that fell back inconsistently would be
            // comparing two different descriptions of the same item.
            var captured = stats.TryGetValue(id, out var lines) ? lines : [];
            result.Add(new ItemDetail(
                summary,
                captured.Count > 0 ? captured : Computed(connection, id),
                skills.TryGetValue(id, out var skill) ? skill : null));
        }

        return result;
    }

    public ItemDetail? Get(long id) {
        using var connection = Open();

        ItemSummary summary;
        using (var command = connection.CreateCommand()) {
            command.CommandText = $"""
                SELECT {SummaryColumns}
                {SearchFrom}
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
    /// <summary>
    /// Tooltip lines for a whole page of cards in one query.
    ///
    /// Upstream renders every card in full, so the alternative is a round trip per card on every
    /// scroll. The ids are inlined rather than parameterised because they are row ids this method
    /// just read out of the database, and SQLite has a hard limit on parameter count that a page
    /// of 500 would approach.
    /// </summary>
    private static Dictionary<long, List<ItemStatLine>> StatsFor(SqliteConnection connection, List<long> ids) {
        var result = new Dictionary<long, List<ItemStatLine>>();
        if (ids.Count == 0) return result;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT r.playeritemid, rr.Type, rr.Text FROM ReplicaItemRow rr
             JOIN ReplicaItem2 r ON r.Id = rr.replicaitemid
            WHERE r.playeritemid IN ({string.Join(",", ids)})
            ORDER BY r.playeritemid, rr.Id;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            var id = reader.GetInt64(0);
            if (!result.TryGetValue(id, out var lines)) result[id] = lines = [];
            lines.Add(new ItemStatLine(reader.GetInt32(1), reader.GetString(2)));
        }
        return result;
    }

    /// <summary>Granted skills for a page of cards. See <see cref="Get"/> for the single-item form.</summary>
    private static Dictionary<long, ItemSkillInfo> SkillsFor(SqliteConnection connection, List<long> ids) {
        var result = new Dictionary<long, ItemSkillInfo>();
        if (ids.Count == 0) return result;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.Id, s.Name, s.Description, IFNULL(s.Level, 0), s.Trigger,
                   EXISTS (SELECT 1 FROM DatabaseItemStat_v2 st
                            WHERE st.id_databaseitem = s.id_databaseitem
                              AND st.Stat = 'spawnObjects')
            FROM PlayerItem p
            JOIN DatabaseItem_v2 db ON db.baserecord = p.baserecord
            JOIN itemskill_mapping m ON m.id_databaseitem = db.id_databaseitem
            JOIN itemskill_v2 s ON s.id_skill = m.id_skill
            WHERE p.Id IN ({string.Join(",", ids)});
            """;

        try {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                var id = reader.GetInt64(0);
                if (result.ContainsKey(id)) continue;   // upstream takes the first
                result[id] = new ItemSkillInfo(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5) != 0);
            }
        }
        catch (SqliteException) { /* skills not parsed yet */ }
        return result;
    }

    /// <summary>Lines computed from the game database, for an item with no captured tooltip.</summary>
    private IReadOnlyList<ItemStatLine> Computed(SqliteConnection connection, long id) {
        if (!_statText.Available) return [];
        return _statText.Describe(connection, id)
            .Select(line => new ItemStatLine(
                line.TextClass, line.Text,
                line.Section?.ToString().ToLowerInvariant(),
                line.Modifier, line.Label, line.Skill, line.Extras))
            .ToList();
    }

    private static ItemSummary ReadSummary(SqliteDataReader reader) {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

        // An empty name is as absent as a null one, and both occur: the column is written by
        // several paths and one of them stored "" for an item the game named nowhere.
        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        var rawName = Text(10);
        var storedName = Text(1);
        var templateName = Text(5);

        return new ItemSummary(
            Id:         reader.GetInt64(0),
            // The stored name first, which is upstream's only answer: it sends
            // PureItemName(item.Name) and nothing else. It is the composed one
            // (ItemNameComposer), so two copies of an item are captioned identically however
            // each of them arrived. The tooltip line behind it is the game's display text, set
            // markers and all, and is a fallback rather than a source — it is all there is for a
            // record the parsed game data does not describe.
            Name:       Blank(storedName) ?? Blank(rawName) ?? Blank(templateName) ?? "Unknown Item",
            BaseRecord: reader.GetString(2),
            Seed:       reader.GetInt64(3),
            IsHardcore: reader.GetInt64(4) != 0,
            ItemClass:  Text(6),
            Quality:    Text(7),
            // The *item's* requirement, not its base record's. Upstream sends
            // PlayerItem.MinimumLevel, which is LevelRequirement — the highest across every
            // record the item is made of. A shield whose base record has no requirement of its
            // own but whose affixes need level 92 read "Level Requirement: Any" while the level
            // filter, which uses this same column, excluded it below 92.
            Level:      reader.IsDBNull(8) ? 0 : (int)reader.GetDouble(8),
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
    /// The branches a search can be scoped to: one entry per (mod, hardcore) pair the collection
    /// holds.
    ///
    /// Upstream's <c>PlayerItemDaoImpl.GetModSelection</c>, which fills the dropdown its search
    /// is always scoped by — a search there is never "everything", because the game keeps a
    /// separate transfer stash per mod and per hardcore branch, and an item cannot cross either.
    /// Its two "even if we have no items, at least list vanilla/nomod" rules are kept.
    ///
    /// Vanilla is the empty string, matching PlayerItem.Mod and upstream's convention. Mods that
    /// have been parsed but never played are listed too, so a fresh install can be pointed at
    /// one; upstream has no equivalent only because it discovers mods a different way.
    /// </summary>
    public IReadOnlyList<object> Mods() {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Mod, Hardcore, SUM(Items) AS Items FROM (
                SELECT IFNULL(Mod, '') AS Mod, IsHardcore <> 0 AS Hardcore, COUNT(*) AS Items
                  FROM PlayerItem GROUP BY IFNULL(Mod, ''), IsHardcore <> 0
                UNION ALL
                SELECT DISTINCT Mod, 0, 0 FROM ItemTemplate
            )
            GROUP BY Mod, Hardcore
            ORDER BY Hardcore, (Mod = '') DESC, Mod;
            """;

        var branches = new List<(string Mod, bool Hardcore, int Items)>();
        try {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                branches.Add((reader.IsDBNull(0) ? "" : reader.GetString(0),
                              reader.GetBoolean(1), reader.GetInt32(2)));
            }
        }
        catch (SqliteException) { /* not parsed yet */ }

        // Upstream's rule: if a branch has items from a mod, vanilla on that branch is offered
        // too, since that is where the game puts anything looted without the mod loaded.
        foreach (var hardcore in new[] { false, true }) {
            if (branches.Any(b => b.Hardcore == hardcore)
                && !branches.Any(b => b.Hardcore == hardcore && b.Mod.Length == 0)) {
                branches.Add(("", hardcore, 0));
            }
        }

        return branches
            .OrderBy(b => b.Hardcore).ThenByDescending(b => b.Mod.Length == 0)
            .ThenBy(b => b.Mod, StringComparer.OrdinalIgnoreCase)
            .Select(b => (object)new { Name = b.Mod, Hardcore = b.Hardcore, Items = b.Items })
            .ToList();
    }

    public HostStatus Status(SteamPaths paths, PrefixBridge bridge, DateTime? gameStartedAt,
                             AppSettings? settings = null, bool attaching = false,
                             bool parsing = false, string? parseStep = null) {
        using var connection = Open();

        // The installation this client is actually using, which is not always the one discovery
        // found. Someone whose game lives outside a Steam library — or whose library Steam
        // describes in a way discovery cannot read — sets the folder by hand in Settings, and
        // reporting only what discovery found told them "installation not found" over a path
        // they had just typed in. The UI takes this for "there is nothing to read": it disables
        // Load Database, so the templates stayed at zero and every explanation of why pointed at
        // a missing game folder rather than at the button that was greyed out.
        var current = settings ?? AppSettings.Load();
        var gameDir = current.GameDir ?? paths.GameDir;

        int Count(string table) {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            try { return Convert.ToInt32(command.ExecuteScalar()); }
            catch (SqliteException) { return 0; }   // table not created yet
        }

        string? Staleness() {
            try {
                return IAGrim.Core.GameData.GameDataStatus
                    .Check(_databasePath, gameDir, current.Language).Reason;
            }
            catch (Exception) { return null; }   // never let a status call fail the UI
        }

        int NeedingStats() {
            try {
                // A re-parse empties DatabaseItemStat_v2 — every id_databaseitem it referenced
                // has just been reassigned — but leaves each item's Rarity in place. Counting
                // rarities alone therefore reports "nothing to do" while every record-driven
                // filter (slot, damage type, mastery, pet bonus) silently matches nothing. Ask
                // the table that actually got cleared.
                using var stats = connection.CreateCommand();
                stats.CommandText =
                    "SELECT (SELECT COUNT(*) FROM PlayerItem), " +
                    "       (SELECT EXISTS (SELECT 1 FROM DatabaseItemStat_v2));";
                using (var reader = stats.ExecuteReader()) {
                    if (reader.Read() && reader.GetInt32(0) > 0 && reader.GetInt32(1) == 0) {
                        return reader.GetInt32(0);
                    }
                }

                // Items that *can* be described and have not been. Not simply "no rarity": a
                // quest item — records/storyelements/questassets/... — carries no
                // itemClassification at all, so nothing will ever give it one. Counting those
                // put a warning on screen that no amount of analysis could clear, and would
                // have had the client rescanning the game archives at every launch to chase it.
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*) FROM PlayerItem p
                     WHERE p.Rarity IS NULL
                       AND EXISTS (SELECT 1 FROM DatabaseItem_v2 db
                                    JOIN DatabaseItemStat_v2 s ON s.id_databaseitem = db.id_databaseitem
                                   WHERE db.baserecord = p.baserecord);
                    """;
                return Convert.ToInt32(command.ExecuteScalar());
            }
            catch (SqliteException) { return 0; }   // tables not created yet
        }

        return new HostStatus(
            GameRunning:      gameStartedAt is not null,
            GameStartedAt:    gameStartedAt,
            HookAttached:     bridge.IsHookLive(gameStartedAt),
            PendingLootFiles: bridge.PendingLootFiles().Count(),
            ItemCount:        Count("PlayerItem"),
            TemplateCount:    Count("ItemTemplate"),
            GameDir:          gameDir,
            BridgeDir:        bridge.Root,
            DatabaseFile:     _databasePath,
            ItemsNeedingStats: NeedingStats(),
            GameDataStale:    Staleness(),
            Attaching:        attaching,
            ParsingGameData:  parsing,
            ParseStep:        parseStep,
            // Read rather than passed: the pass is a static service reachable from every caller
            // of this method, and threading it through four call sites would only make it
            // possible for one of them to forget and report an idle client during a rebuild.
            Analysing:        StatRefresh.Running,
            AnalysisStep:     StatRefresh.Step);
    }
}
