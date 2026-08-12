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
        var statsByRecord = LoadStatsFor(wanted, petTargets, progress);
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

        transaction.Commit();
        return new PrecomputeResult(items.Count, computed, written, skipped,
                                    statsByRecord.Count, recordStats, petLinks);
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
    private Dictionary<string, List<DBStatRow>> LoadStatsFor(
        HashSet<string> wanted, HashSet<string> petRecords, Action<string>? progress) {

        var result = new Dictionary<string, List<DBStatRow>>(StringComparer.OrdinalIgnoreCase);

        foreach (var database in ItemDatabase.FindDatabases(_gameDir)) {
            progress?.Invoke($"scanning {Path.GetFileName(database)}");
            foreach (var (record, stats) in ItemDatabase.LoadAllStats(database, applyStatFilter: false)) {
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
        return result;
    }
}
