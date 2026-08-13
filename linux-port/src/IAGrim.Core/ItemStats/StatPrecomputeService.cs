using IAGrim.Core.GameData;
using IAGrim.Core.ItemStats.Dto;
using Microsoft.Data.Sqlite;

namespace IAGrim.Core.ItemStats;

public sealed record PrecomputeResult(
    int ItemsProcessed, int ItemsComputed, int StatsWritten, int Skipped,
    int RecordsIndexed, int RecordStatsWritten, int PetRecordsLinked);

/// <summary>
/// The analysis pass: everything the search needs that the hook does not provide.
///
/// Three outputs, all of them upstream's tables:
///
/// 1. <c>DatabaseItemStat_v2</c> — Grim Dawn's own stat rows for the records this collection
///    references. Every record-driven filter (damage type, retaliation, mastery, pet bonus,
///    slot) is a join against this table in upstream, so having it is what lets those queries
///    be ported verbatim instead of reinvented.
/// 2. <c>ComputedItemStat</c> — each item's *rolled* values, from replaying the game's random
///    stream over those rows (<see cref="SeedStatCalculator"/>).
/// 3. <c>PlayerItem.Rarity/PrefixRarity/LevelRequirement</c> and the pet-bonus rows of
///    <c>PlayerItemRecord</c>.
///
/// **Why only the referenced records.** Upstream stores the whole game database: measured
/// against this installation that is 4.8 million rows (~274 MB), matching upstream's own
/// ~210 MB file. Only records the collection actually touches can affect a filter over owned
/// items, so the ARZ is streamed and everything else discarded. The cost is re-parsing on a
/// full recompute (about 10 s); the saving is two orders of magnitude of storage.
///
/// The exception is the collection and set views, which browse items the player does *not*
/// own. Those read <c>ItemTemplate</c> instead — a denormalised row per record, which is cheap
/// because it holds six fields rather than every stat.
/// </summary>
public sealed class StatPrecomputeService {
    private readonly string _databasePath;
    private readonly string _gameDir;

    public StatPrecomputeService(string databasePath, string gameDir) {
        _databasePath = databasePath;
        _gameDir = gameDir;
    }

    public PrecomputeResult Run(Action<string>? progress = null) {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        Platform.Schema.Apply(connection);

        var items = LoadItems(connection);
        if (items.Count == 0) {
            return new PrecomputeResult(0, 0, 0, 0, 0, 0, 0);
        }

        // Records the collection references, plus the skill records granted skills point at —
        // the summoner filter tests those for a 'spawnObjects' stat, so they need rows too.
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items) {
            foreach (var record in item.Records()) wanted.Add(record);
        }
        foreach (var record in SkillRecords(connection)) wanted.Add(record);

        // Every component in the game, not just the ones this collection happens to have
        // socketed. The Components view describes all of them, and a component nobody owns has
        // no other reason to be here — 108 records, so the cost is nothing.
        foreach (var record in ComponentRecords(connection)) wanted.Add(record);

        progress?.Invoke($"{items.Count:N0} items referencing {wanted.Count:N0} records");

        // Pass 1: which records are pet-bonus targets, and which record points at which.
        // Needed before the stats are loaded, because a pet-bonus target's stats are stored
        // under "pet"-prefixed names and the set has to be known to prefix them.
        var (petTargets, petTargetsByRecord) = ScanPetBonusTargets(progress);

        foreach (var record in wanted.ToList()) {
            if (!petTargetsByRecord.TryGetValue(record, out var targets)) continue;
            foreach (var target in targets) wanted.Add(target);
        }

        // Pass 2: the stat rows themselves, unfiltered — this table is upstream's raw store and
        // the seed-engine filter belongs at the point of reading, not here. See StatFilter.
        var statsByRecord = LoadStatsFor(wanted, petTargets, progress, LoadTagNames(connection));
        progress?.Invoke($"loaded stats for {statsByRecord.Count:N0} records");

        using var transaction = connection.BeginTransaction();

        var recordStats = WriteDatabaseItemStats(connection, transaction, statsByRecord);
        progress?.Invoke($"{recordStats:N0} game stat rows stored");

        // The seed engine and the rarity rules both take upstream's filtered view of the rows.
        var filtered = statsByRecord.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(r => r.Stat is not null && StatFilter.Keep(r.Stat, r.Value)).ToList(),
            StringComparer.OrdinalIgnoreCase);

        var petLinks = WritePetRecords(connection, transaction, items, filtered);

        var computed = 0;
        var skipped = 0;
        var written = 0;

        using (var clear = connection.CreateCommand()) {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM ComputedItemStat;";
            clear.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO ComputedItemStat (playeritemid, stat, value) VALUES ($item, $stat, $value);";
        var itemParam = insert.Parameters.Add("$item", SqliteType.Integer);
        var statParam = insert.Parameters.Add("$stat", SqliteType.Text);
        var valueParam = insert.Parameters.Add("$value", SqliteType.Real);

        // Rarity, affix quality and level requirement. Written for every item, including the
        // ones the seed engine bails out on: they are read straight off the records and do not
        // depend on the roll, so a skipped roll must not also cost the item its rarity.
        using var details = connection.CreateCommand();
        details.Transaction = transaction;
        details.CommandText = """
            UPDATE PlayerItem
               SET Rarity = $rarity, PrefixRarity = $prefixRarity, LevelRequirement = $level
             WHERE Id = $id;
            """;
        var detailRarity = details.Parameters.Add("$rarity", SqliteType.Text);
        var detailPrefix = details.Parameters.Add("$prefixRarity", SqliteType.Integer);
        var detailLevel  = details.Parameters.Add("$level", SqliteType.Real);
        var detailId     = details.Parameters.Add("$id", SqliteType.Integer);

        foreach (var item in items) {
            List<DBStatRow> Rows(string? record) =>
                record is not null && filtered.TryGetValue(record, out var rows) ? rows : [];

            var detailRecords = item.Records().ToList();
            detailRarity.Value = ItemRarity.ForRecords(filtered, detailRecords);
            detailPrefix.Value = ItemRarity.GreenQualityLevelForRecords(filtered, detailRecords);
            detailLevel.Value  = ItemRarity.MinimumLevelForRecords(filtered, detailRecords);
            detailId.Value     = item.Id;
            details.ExecuteNonQuery();

            var baseRows = Rows(item.BaseRecord);
            if (baseRows.Count == 0 || item.Seed == 0) {
                skipped++;
                continue;
            }

            // Returns null when the roll cannot be trusted — no seed, or the item carries
            // rollable fields the engine does not model, which desyncs every later draw.
            // Storing approximate numbers would be worse than storing none: the filters would
            // silently lie.
            var stats = SeedStatCalculator.Compute(
                baseRows, Rows(item.PrefixRecord), Rows(item.SuffixRecord), (uint)item.Seed);

            if (stats is null) {
                skipped++;
                continue;
            }

            foreach (var (stat, value) in stats) {
                itemParam.Value = item.Id;
                statParam.Value = stat;
                valueParam.Value = value;
                insert.ExecuteNonQuery();
                written++;
            }
            computed++;
        }

        StampVersion(connection, transaction);
        transaction.Commit();
        return new PrecomputeResult(items.Count, computed, written, skipped,
                                    statsByRecord.Count, recordStats, petLinks);
    }

    /// <summary>
    /// What this pass writes, as a number that changes when the pass does.
    ///
    /// The rows in DatabaseItemStat_v2 are not the game's data alone: several are synthesised
    /// here the way upstream's parser synthesises them, and a filter reads what this pass wrote
    /// rather than what the archives hold. So when this pass learns to write a field it did not
    /// write before, every collection parsed before then is silently missing it — the mastery
    /// filter matched nothing at all for exactly that reason, on a database that looked complete
    /// by every other measure.
    ///
    /// Raise this whenever the rows written here change shape or content.
    /// </summary>
    public const int Version = 3;

    /// <summary>Where that number is kept. Read by StatRefresh to decide on a rebuild.</summary>
    public const string VersionKey = "stats.version";

    private static void StampVersion(SqliteConnection connection, SqliteTransaction transaction) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO GameDataMeta (Key, Value) VALUES ($key, $value) "
            + "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        command.Parameters.AddWithValue("$key", VersionKey);
        command.Parameters.AddWithValue("$value", Version.ToString());
        try { command.ExecuteNonQuery(); }
        catch (SqliteException) { /* pre-GameDataMeta database; the next start creates it */ }
    }

    /// <summary>
    /// Every component record the parse found, so the Components view can describe all of them.
    ///
    /// Components live under <c>records/items/materia/</c>, which is also where the crafting
    /// materials are; both are read, and the view decides which is which by whether the record
    /// says what it can be socketed into.
    /// </summary>
    private static List<string> ComponentRecords(SqliteConnection connection) {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT Record FROM ItemTemplate WHERE Record LIKE '%/materia/%';";
        var records = new List<string>();
        try {
            using var reader = command.ExecuteReader();
            while (reader.Read()) records.Add(reader.GetString(0));
        }
        catch (SqliteException) { /* not parsed yet */ }
        return records;
    }

    /// <summary>Records of skills items grant, so the summoner filter has stat rows to test.</summary>
    private static List<string> SkillRecords(SqliteConnection connection) {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Record FROM itemskill_v2 WHERE Record IS NOT NULL;";
        var records = new List<string>();
        try {
            using var reader = command.ExecuteReader();
            while (reader.Read()) records.Add(reader.GetString(0));
        }
        catch (SqliteException) { /* skills not parsed yet */ }
        return records;
    }

    /// <summary>
    /// Grim Dawn expresses a pet bonus as a <c>petBonusName</c> pointing at another record, and
    /// upstream stores every stat of such a record under a "pet"-prefixed name so that
    /// "attack speed on the pet" and "attack speed on me" are different fields. This finds the
    /// targets; <see cref="LoadStatsFor"/> applies the prefix.
    ///
    /// The scan is global rather than restricted to owned records, because upstream classifies
    /// over the whole database — a record referenced as a pet bonus anywhere is a pet record
    /// everywhere, and narrowing it would prefix the same record inconsistently.
    /// </summary>
    private (HashSet<string> Targets, Dictionary<string, List<string>> ByRecord) ScanPetBonusTargets(
        Action<string>? progress) {

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byRecord = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var database in ItemDatabase.FindDatabases(_gameDir)) {
            progress?.Invoke($"scanning {Path.GetFileName(database)} for pet bonuses");
            foreach (var (record, stats) in ItemDatabase.LoadAllStats(database, applyStatFilter: false)) {
                foreach (var stat in stats) {
                    if (stat.Stat != "petBonusName" || string.IsNullOrWhiteSpace(stat.TextValue)) continue;
                    targets.Add(stat.TextValue);
                    if (!byRecord.TryGetValue(record, out var list)) byRecord[record] = list = [];
                    list.Add(stat.TextValue);
                }
            }
        }

        progress?.Invoke($"{targets.Count:N0} pet-bonus records");
        return (targets, byRecord);
    }

    /// <summary>
    /// Fills in PlayerItemRecord: the item's own records, then its pet-bonus targets.
    ///
    /// Upstream's <c>UpdateRecords</c> and <c>UpdatePetRecords</c>. The core records are written
    /// at import too, but they are rewritten here so that a collection imported before this
    /// table existed — or copied in from elsewhere — is repaired rather than silently missing
    /// from every record-driven filter.
    ///
    /// The pet rows are what make an item findable by what its *pet* gets: the pet filter looks
    /// for PlayerItemRecord rows that are not one of the item's own core records, so these extra
    /// rows are the entire signal.
    /// </summary>
    private static int WritePetRecords(
        SqliteConnection connection, SqliteTransaction transaction,
        List<ItemRow> items, Dictionary<string, List<DBStatRow>> stats) {

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT OR IGNORE INTO PlayerItemRecord (PlayerItemId, Record) VALUES ($id, $record);";
        var id = insert.Parameters.Add("$id", SqliteType.Integer);
        var record = insert.Parameters.Add("$record", SqliteType.Text);

        foreach (var item in items) {
            foreach (var own in item.Records()) {
                id.Value = item.Id;
                record.Value = own;
                insert.ExecuteNonQuery();
            }
        }

        var written = 0;
        foreach (var item in items) {
            // Upstream's GetPetBonusRecords: the petBonusName targets of the item's own records.
            var targets = item.Records()
                .Where(stats.ContainsKey)
                .SelectMany(r => stats[r])
                .Where(s => s.Stat == "petBonusName")
                .Select(s => s.TextValue)
                .Where(t => !string.IsNullOrWhiteSpace(t));

            foreach (var target in targets) {
                id.Value = item.Id;
                record.Value = target!;
                written += insert.ExecuteNonQuery();
            }
        }
        return written;
    }

    /// <summary>
    /// Writes the game's stat rows into upstream's DatabaseItemStat_v2, keyed by the
    /// DatabaseItem_v2 id for the record. Records with no DatabaseItem_v2 row are skipped:
    /// those are loot tables and other non-items that 'iagd parse' does not keep.
    /// </summary>
    private static int WriteDatabaseItemStats(
        SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<string, List<DBStatRow>> statsByRecord) {

        var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand()) {
            command.Transaction = transaction;
            command.CommandText = "SELECT baserecord, id_databaseitem FROM DatabaseItem_v2;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                if (!reader.IsDBNull(0)) ids[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        using (var clear = connection.CreateCommand()) {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM DatabaseItemStat_v2;";
            clear.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO DatabaseItemStat_v2 (id_databaseitem, Stat, TextValue, val1)
            VALUES ($item, $stat, $text, $value);
            """;
        var itemParam  = insert.Parameters.Add("$item", SqliteType.Integer);
        var statParam  = insert.Parameters.Add("$stat", SqliteType.Text);
        var textParam  = insert.Parameters.Add("$text", SqliteType.Text);
        var valueParam = insert.Parameters.Add("$value", SqliteType.Real);

        var written = 0;
        foreach (var (record, rows) in statsByRecord) {
            if (!ids.TryGetValue(record, out var databaseItemId)) continue;

            foreach (var row in rows) {
                itemParam.Value  = databaseItemId;
                statParam.Value  = (object?)row.Stat ?? DBNull.Value;
                textParam.Value  = (object?)row.TextValue ?? DBNull.Value;
                valueParam.Value = row.Value;
                insert.ExecuteNonQuery();
                written++;
            }
        }
        return written;
    }

    private sealed record ItemRow(long Id, string BaseRecord, string? PrefixRecord,
                                  string? SuffixRecord, long Seed, string? MateriaRecord,
                                  string? AscendantAffixNameRecord,
                                  string? AscendantAffix2hNameRecord) {

        /// <summary>
        /// Every record the item is composed of, in upstream's order —
        /// <c>PlayerItemDaoImpl.GetRecordsForItem</c>.
        ///
        /// Note the seed engine uses only the first three of these. Adding a record to the roll
        /// would change the draw stream; adding one here only widens what the rarity and level
        /// rules consider, which is what upstream does.
        /// </summary>
        public IEnumerable<string> Records() {
            yield return BaseRecord;
            if (!string.IsNullOrWhiteSpace(PrefixRecord)) yield return PrefixRecord;
            if (!string.IsNullOrWhiteSpace(SuffixRecord)) yield return SuffixRecord;
            if (!string.IsNullOrWhiteSpace(MateriaRecord)) yield return MateriaRecord;
            if (!string.IsNullOrWhiteSpace(AscendantAffixNameRecord)) yield return AscendantAffixNameRecord;
            if (!string.IsNullOrWhiteSpace(AscendantAffix2hNameRecord)) yield return AscendantAffix2hNameRecord;
        }
    }

    private static List<ItemRow> LoadItems(SqliteConnection connection) {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, baserecord, PrefixRecord, SuffixRecord, Seed,
                   MateriaRecord, AscendantAffixNameRecord, AscendantAffix2hNameRecord
            FROM PlayerItem;
            """;

        var items = new List<ItemRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

            items.Add(new ItemRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                Text(2),
                Text(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                Text(5),
                Text(6),
                Text(7)));
        }
        return items;
    }

    /// <summary>
    /// Streams every .arz, keeping only the records asked for. Later archives override
    /// earlier ones, matching how expansions rebalance base-game items.
    /// </summary>
    /// <summary>tag → display text, as 'iagd parse' resolved it for the chosen language.</summary>
    private static Dictionary<string, string> LoadTagNames(SqliteConnection connection) {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Tag, Name FROM ItemTag;";
        try {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1)) tags[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch (SqliteException) { /* not parsed yet */ }
        return tags;
    }

    private Dictionary<string, List<DBStatRow>> LoadStatsFor(
        HashSet<string> wanted, HashSet<string> petRecords, Action<string>? progress,
        Dictionary<string, string> tagNames) {

        var result = new Dictionary<string, List<DBStatRow>>(StringComparer.OrdinalIgnoreCase);

        // "+2 to Black Death" is two stats in the game's data — a skill record and a level —
        // and one line on the item. Upstream merges them while parsing (ArzParser's
        // GetSpecialSkillAugments) into a single augmentSkill{i} carrying the skill's display
        // name, because nothing downstream can resolve a record to a name. Collected in the
        // same scan, since the skill records are streaming past anyway.
        var skillNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // A skill's tier, for the "Tier 3 Occultist skill" note upstream attaches to a granted
        // skill — and, more importantly, the thing whose presence decides whether that note (and
        // with it the class filter's augmentSkill{i}Extras row) exists at all.
        var skillTiers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var database in ItemDatabase.FindDatabases(_gameDir)) {
            progress?.Invoke($"scanning {Path.GetFileName(database)}");
            foreach (var (record, stats) in ItemDatabase.LoadAllStats(database, applyStatFilter: false)) {
                var displayName = stats.FirstOrDefault(s => s.Stat == "skillDisplayName")?.TextValue;
                if (displayName is not null) skillNames[record] = displayName;

                var tier = stats.FirstOrDefault(s => s.Stat == "skillTier");
                if (tier is not null) skillTiers[record] = tier.Value;

                if (!wanted.Contains(record)) continue;

                // A pet-bonus target's every stat is stored under a "pet"-prefixed name, so
                // that "attack speed" on the pet and on the player stay distinguishable.
                // Upstream does this in ArzParsingWrapper before the rows are ever stored.
                result[record] = petRecords.Contains(record)
                    ? stats.Select(s => new DBStatRow {
                          Record    = s.Record,
                          Stat      = "pet" + s.Stat,
                          Value     = s.Value,
                          TextValue = s.TextValue,
                      }).ToList()
                    : stats;
            }
        }

        AddSkillAugments(result, skillNames, skillTiers, tagNames);
        return result;
    }

    /// <summary>
    /// Turns each augmentSkillName/augmentSkillLevel pair into the single augmentSkill{i} row
    /// upstream's rules read, and the mastery pair into augmentMastery{i}.
    ///
    /// The skill's display name is a tag rather than text — <c>skillDisplayName</c> on the skill
    /// record — and the format strings ("+{0} to {3}", "+{0} to All Skills in {3}") come from
    /// upstream's own language table, so a resolved tag is all that is needed here.
    /// </summary>
    /// <summary>
    /// The mastery a skill record belongs to, as upstream's <c>ExtractClassFromRecord</c> reads
    /// it: the "playerclassNN" segment of the path is the class id the filters compare against.
    /// </summary>
    private static string? ClassOf(string record) {
        var match = System.Text.RegularExpressions.Regex.Match(record, @"/player(class\d+)/");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void AddSkillAugments(
        Dictionary<string, List<DBStatRow>> statsByRecord,
        Dictionary<string, string> skillNames,
        Dictionary<string, double> skillTiers,
        Dictionary<string, string> tagNames) {

        foreach (var (record, stats) in statsByRecord) {
            for (var i = 1; i <= 4; i++) {
                // The skill's name is stored resolved: the rules put TextValue straight into
                // the line ("+{0} to {3}") without a tag lookup of their own.
                //
                // Only when it resolves does anything get written — upstream gives up on the
                // whole pair at that point (GetSpecialSkillAugments continues past it), and the
                // Extras row below hangs off the same decision. A skill with no display name is
                // one the item modifies rather than grants, and neither belongs on a card nor in
                // a class filter's results.
                var granted = Merge(stats, $"augmentSkillName{i}", $"augmentSkillLevel{i}",
                                    $"augmentSkill{i}",
                                    skill => skillNames.TryGetValue(skill, out var tag)
                                          && tagNames.TryGetValue(tag, out var text) ? text : null);

                // The mastery's is the **class id**, which is what upstream stores here and what
                // its class filter compares against ("dbs.TextValue = 'class03'"). It renders too:
                // StatManager passes it through TryGetClassName, and the language table maps
                // class03 to Occultist.
                Merge(stats, $"augmentMasteryName{i}", $"augmentMasteryLevel{i}", $"augmentMastery{i}",
                      ClassOf);

                // "Tier 3 Occultist skill", and the row the class filter matches on for an item
                // that grants a specific skill. Upstream writes it beside augmentSkill{i} from
                // the skill's class and the tier of its root skill; this reads the tier off the
                // skill itself, which is where it sits for the class skills that carry one.
                if (granted) AddExtras(stats, i, skillTiers);
            }
        }

        static void AddExtras(List<DBStatRow> stats, int index, Dictionary<string, double> skillTiers) {
            var skill = stats.FirstOrDefault(s => s.Stat == $"augmentSkillName{index}")?.TextValue;
            var level = stats.FirstOrDefault(s => s.Stat == $"augmentSkillLevel{index}");
            if (skill is null || level is null) return;

            // No tier means upstream writes no Extras row, and an item that only *references* a
            // skill — a modifier, rather than "+2 to it" — has no business matching a class.
            if (!skillTiers.TryGetValue(skill, out var tier)) return;

            var className = ClassOf(skill);
            if (className is null) return;

            stats.Add(new DBStatRow {
                Record    = stats.FirstOrDefault()?.Record,
                Stat      = $"augmentSkill{index}Extras",
                Value     = tier,
                TextValue = className,
            });
        }

        static bool Merge(List<DBStatRow> stats, string nameStat, string levelStat, string merged,
                          Func<string, string?> resolve) {
            var name = stats.FirstOrDefault(s => s.Stat == nameStat)?.TextValue;
            var level = stats.FirstOrDefault(s => s.Stat == levelStat);
            if (name is null || level is null) return false;

            var resolved = resolve(name);
            if (resolved is null) return false;

            stats.Add(new DBStatRow {
                Record    = stats.FirstOrDefault()?.Record,
                Stat      = merged,
                Value     = level.Value,
                TextValue = resolved,
            });
            return true;
        }
    }
}
