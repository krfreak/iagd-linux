using IAGrim.Core.ItemStats.Dto;
using Microsoft.Data.Sqlite;

namespace IAGrim.Core.ItemStats;

/// <summary>
/// Brings every stored item's name back to the one <see cref="ItemNameComposer"/> computes.
///
/// **Why a sweep and not just the import paths.** Items looted here are named as they arrive
/// (<see cref="NewItemDetails"/>) and the analysis pass names everything it touches, which is
/// where upstream does it too. Neither covers an item that was *inserted with a name already on
/// it*: the online backup stores the name the server sent, a merge stores the name the other
/// collection had, and both then look complete — they have a rarity and a level, so nothing
/// asks to describe them again. Their names stay in whatever form the machine that first saw
/// the item used, which is how one collection ends up holding "Lokarr's Coat" and
/// "(S) {}Lokarr's Coat" side by side.
///
/// So this runs over the whole collection: cheap, because a name needs five stat fields per
/// record rather than the item's whole stat block, and idempotent, because it rewrites only the
/// rows whose stored name differs from the computed one. A clean collection matches nothing and
/// costs one query — the same discipline as <c>ReplicaService.NormaliseStoredRows</c>.
///
/// An item whose records are not in the parsed game data — a mod that was never parsed, a record
/// the game dropped — keeps the name it has. A composed empty string is not an improvement on
/// a stale name, and blanking it would lose the item from the name search entirely.
/// </summary>
public static class ItemNameRefresh {
    /// <param name="ids">Restrict to these items, or null for the whole collection.</param>
    /// <returns>How many names were rewritten.</returns>
    public static int Run(SqliteConnection connection, IReadOnlyList<long>? ids = null) {
        var composer = ItemNameComposer.Load(connection);
        if (composer is null) return 0;

        var statsByRecord = LoadNameStats(connection);
        if (statsByRecord.Count == 0) return 0;

        var updates = new List<(long Id, string Name)>();

        using (var command = connection.CreateCommand()) {
            var scope = ids is null ? "" : $" WHERE Id IN ({string.Join(",", ids)})";
            command.CommandText =
                "SELECT Id, baserecord, PrefixRecord, SuffixRecord, MateriaRecord, Name "
                + $"FROM PlayerItem{scope};";

            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

                var composed = composer.Compose(statsByRecord, Text(1), Text(2), Text(3), Text(4));
                if (composed.Length == 0) continue;
                if (string.Equals(composed, Text(5), StringComparison.Ordinal)) continue;

                updates.Add((reader.GetInt64(0), composed));
            }
        }

        if (updates.Count == 0) return 0;

        using var transaction = connection.BeginTransaction();
        using (var update = connection.CreateCommand()) {
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE PlayerItem SET Name = $name, namelowercase = $lower WHERE Id = $id;";
            var name = update.Parameters.Add("$name", SqliteType.Text);
            var lower = update.Parameters.Add("$lower", SqliteType.Text);
            var id = update.Parameters.Add("$id", SqliteType.Integer);

            foreach (var (item, composed) in updates) {
                name.Value = composed;
                lower.Value = composed.ToLowerInvariant();
                id.Value = item;
                update.ExecuteNonQuery();
            }
        }
        transaction.Commit();

        return updates.Count;
    }

    /// <summary>
    /// The name fields of every record in the parsed game data, keyed by record.
    ///
    /// Deliberately narrow: five stats out of the hundred and fifty thousand rows this table
    /// holds for a real collection, which is four thousand rows rather than all of them.
    /// Every one of the five is whitelisted by <see cref="StatFilter"/>, so what comes back is
    /// already the filtered view the rest of the port composes names from.
    /// </summary>
    private static Dictionary<string, List<DBStatRow>> LoadNameStats(SqliteConnection connection) {
        var result = new Dictionary<string, List<DBStatRow>>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT db.baserecord, dbs.Stat, dbs.TextValue, dbs.val1
            FROM DatabaseItem_v2 db
            JOIN DatabaseItemStat_v2 dbs ON dbs.id_databaseitem = db.id_databaseitem
            WHERE dbs.Stat IN ({string.Join(", ", ItemNameComposer.NameStats.Select(stat => $"'{stat}'"))});
            """;

        try {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                var record = reader.GetString(0);
                if (!result.TryGetValue(record, out var rows)) result[record] = rows = [];
                rows.Add(new DBStatRow {
                    Record = record,
                    Stat = reader.IsDBNull(1) ? null : reader.GetString(1),
                    TextValue = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Value = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                });
            }
        }
        catch (SqliteException) { return result; }   // not parsed yet

        return result;
    }
}
