using IAGrim.Core.ItemStats.Dto;
using Microsoft.Data.Sqlite;

namespace IAGrim.Core.ItemStats;

/// <summary>
/// Fills in rarity, affix quality, level requirement and rolled values for items that have just
/// arrived, without re-reading the game archives.
///
/// **Upstream does this on import.** `PlayerItemDaoImpl` stores the items, then — as long as
/// fewer than five hundred came at once — immediately updates each one's name, rarity and level
/// and its pet records; only a bulk import defers to the batch pass. Without it a freshly looted
/// item has no rarity, so it is drawn in the "unknown" colour instead of as the epic it is, and
/// it has no rolled values, so its card shows the record's base numbers rather than what the
/// item actually rolled.
///
/// The stat rows come from `DatabaseItemStat_v2`, which the batch pass already populated for the
/// records this collection uses. A record never owned before is not there — a genuinely new kind
/// of item — and that item is left for the next full pass rather than guessed at.
/// </summary>
public static class NewItemDetails {
    /// <summary>Upstream's threshold: beyond this, the batch pass is the cheaper answer.</summary>
    public const int MaxItemsPerBatch = 500;

    /// <summary>
    /// Updates the given items. Returns how many were described; items whose records are not in
    /// the stat table are counted as skipped.
    /// </summary>
    public static (int Described, int Skipped) Apply(SqliteConnection connection, IReadOnlyList<long> ids) {
        if (ids.Count == 0 || ids.Count > MaxItemsPerBatch) return (0, ids.Count);

        var items = LoadItems(connection, ids);
        if (items.Count == 0) return (0, 0);

        var wanted = items.SelectMany(item => item.Records()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var statsByRecord = LoadStats(connection, wanted);

        // The seed engine and the rarity rules both take upstream's filtered view of the rows.
        var filtered = statsByRecord.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(r => r.Stat is not null && StatFilter.Keep(r.Stat, r.Value)).ToList(),
            StringComparer.OrdinalIgnoreCase);

        var described = 0;
        var skipped = 0;

        using var transaction = connection.BeginTransaction();

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

        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM ComputedItemStat WHERE playeritemid = $id;";
        var clearId = clear.Parameters.Add("$id", SqliteType.Integer);

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO ComputedItemStat (playeritemid, stat, value) VALUES ($item, $stat, $value);";
        var insertItem  = insert.Parameters.Add("$item", SqliteType.Integer);
        var insertStat  = insert.Parameters.Add("$stat", SqliteType.Text);
        var insertValue = insert.Parameters.Add("$value", SqliteType.Real);

        foreach (var item in items) {
            List<DBStatRow> Rows(string? record) =>
                record is not null && filtered.TryGetValue(record, out var rows) ? rows : [];

            var records = item.Records().ToList();
            if (!records.Any(record => filtered.ContainsKey(record))) {
                skipped++;   // an unknown record; the full pass will reach it
                continue;
            }

            detailRarity.Value = ItemRarity.ForRecords(filtered, records);
            detailPrefix.Value = ItemRarity.GreenQualityLevelForRecords(filtered, records);
            detailLevel.Value  = ItemRarity.MinimumLevelForRecords(filtered, records);
            detailId.Value     = item.Id;
            details.ExecuteNonQuery();
            described++;

            var baseRows = Rows(item.BaseRecord);
            if (baseRows.Count == 0 || item.Seed == 0) continue;

            // Null when the roll cannot be trusted; storing approximate numbers would make the
            // filters lie, so nothing is stored and the card falls back to the record's values.
            var rolled = SeedStatCalculator.Compute(
                baseRows, Rows(item.PrefixRecord), Rows(item.SuffixRecord), (uint)item.Seed);
            if (rolled is null) continue;

            clearId.Value = item.Id;
            clear.ExecuteNonQuery();

            foreach (var (stat, value) in rolled) {
                insertItem.Value  = item.Id;
                insertStat.Value  = stat;
                insertValue.Value = value;
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return (described, skipped);
    }

    private sealed record Row(long Id, string BaseRecord, string? PrefixRecord, string? SuffixRecord,
                              string? ModifierRecord, string? MateriaRecord,
                              string? AscendantAffixNameRecord, string? AscendantAffix2hNameRecord,
                              long Seed) {
        /// <summary>The item's records, in upstream's order — matching StatPrecomputeService.</summary>
        public IEnumerable<string> Records() {
            if (!string.IsNullOrWhiteSpace(BaseRecord)) yield return BaseRecord;
            if (!string.IsNullOrWhiteSpace(PrefixRecord)) yield return PrefixRecord;
            if (!string.IsNullOrWhiteSpace(SuffixRecord)) yield return SuffixRecord;
            if (!string.IsNullOrWhiteSpace(ModifierRecord)) yield return ModifierRecord;
            if (!string.IsNullOrWhiteSpace(MateriaRecord)) yield return MateriaRecord;
            if (!string.IsNullOrWhiteSpace(AscendantAffixNameRecord)) yield return AscendantAffixNameRecord;
            if (!string.IsNullOrWhiteSpace(AscendantAffix2hNameRecord)) yield return AscendantAffix2hNameRecord;
        }
    }

    private static List<Row> LoadItems(SqliteConnection connection, IReadOnlyList<long> ids) {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, baserecord, PrefixRecord, SuffixRecord, ModifierRecord, MateriaRecord,
                   AscendantAffixNameRecord, AscendantAffix2hNameRecord, IFNULL(Seed, 0)
            FROM PlayerItem WHERE Id IN ({string.Join(",", ids)});
            """;

        var items = new List<Row>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            items.Add(new Row(reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1),
                              Text(2), Text(3), Text(4), Text(5), Text(6), Text(7),
                              reader.GetInt64(8)));
        }
        return items;
    }

    private static Dictionary<string, List<DBStatRow>> LoadStats(
        SqliteConnection connection, HashSet<string> records) {

        var result = new Dictionary<string, List<DBStatRow>>(StringComparer.OrdinalIgnoreCase);
        if (records.Count == 0) return result;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT db.baserecord, dbs.Stat, dbs.TextValue, dbs.val1
            FROM DatabaseItem_v2 db
            JOIN DatabaseItemStat_v2 dbs ON dbs.id_databaseitem = db.id_databaseitem
            WHERE db.baserecord = $record;
            """;
        var parameter = command.Parameters.Add("$record", SqliteType.Text);

        foreach (var record in records) {
            parameter.Value = record;
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                var key = reader.GetString(0);
                if (!result.TryGetValue(key, out var rows)) result[key] = rows = [];
                rows.Add(new DBStatRow {
                    Record    = key,
                    Stat      = reader.IsDBNull(1) ? null : reader.GetString(1),
                    TextValue = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Value     = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                });
            }
        }
        return result;
    }
}
