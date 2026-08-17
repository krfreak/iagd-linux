using IAGrim.Core.ItemStats.Dto;
using IAGrim.Platform;
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
    /// <param name="report">Told about repairs, which are rarer and less obvious than rewrites.</param>
    /// <returns>How many names were rewritten.</returns>
    public static int Run(SqliteConnection connection, IReadOnlyList<long>? ids = null,
                          Action<string>? report = null) {
        var composer = ItemNameComposer.Load(connection);
        if (composer is null) return 0;

        var statsByRecord = LoadNameStats(connection);
        if (statsByRecord.Count == 0) return 0;

        var updates = new List<(long Id, string Name)>();

        // Items whose stored name is decoration rather than a name, with that name. Collected
        // rather than fixed in place because the tooltips they are repaired from are one query
        // for the batch.
        var cropped = new List<(long Id, string Name)>();

        using (var command = connection.CreateCommand()) {
            var scope = ids is null ? "" : $" WHERE Id IN ({string.Join(",", ids)})";
            command.CommandText =
                "SELECT Id, baserecord, PrefixRecord, SuffixRecord, MateriaRecord, Name "
                + $"FROM PlayerItem{scope};";

            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

                var stored = Text(5);
                var composed = composer.Compose(statsByRecord, Text(1), Text(2), Text(3), Text(4));

                if (composed.Length == 0) {
                    // Nothing to compose from, so the stored name normally stands. The exception
                    // is a name that is not one: an item whose base record is unparsed but whose
                    // affixes are not was named after its affixes alone by every client that
                    // composed without requiring a core — this port before the guard in
                    // ItemNameComposer, and the Windows tool still. Such a name arrives here
                    // through a merge or the online backup as readily as it was written here, so
                    // recognising it is what stops it settling in permanently: nothing else will
                    // ever ask about this item again, and a later sweep agrees with the crop.
                    if (!string.IsNullOrEmpty(stored)
                        && string.Equals(
                            stored,
                            composer.AffixOnlyName(statsByRecord, Text(1), Text(2), Text(3), Text(4)),
                            StringComparison.Ordinal)) {
                        cropped.Add((reader.GetInt64(0), stored));
                    }
                    continue;
                }

                if (string.Equals(composed, stored, StringComparison.Ordinal)) continue;

                updates.Add((reader.GetInt64(0), composed));
            }
        }

        if (cropped.Count > 0) updates.AddRange(RepairFromTooltips(connection, cropped, report));

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
    /// Grim Dawn's GameTextClass for the line of a tooltip that holds the item's name — the same
    /// one <c>LootStore.AttachReplica</c> reads.
    /// </summary>
    private const int NameTextClass = 6;

    /// <summary>
    /// The game's own name for items this cannot compose one for, out of the tooltips already
    /// stored against them.
    ///
    /// This is the fallback the port already uses for an item whose records are not in the game
    /// data — <c>AttachReplica</c> fills the name column from the same line — so a repair puts
    /// the item exactly where it would have been had the crop never been written, rather than
    /// inventing a name of its own. It carries the game's display markers, which is what that
    /// path stores for such an item too.
    ///
    /// An item with no tooltip keeps the crop. There is nothing better to give it here, and
    /// parsing the mod its base record comes from repairs it properly at the next sweep — the
    /// composed name then wins on its own merits, through the ordinary path above.
    /// </summary>
    private static List<(long Id, string Name)> RepairFromTooltips(
        SqliteConnection connection, List<(long Id, string Name)> cropped, Action<string>? report) {

        var repaired = new List<(long Id, string Name)>();

        using (var command = connection.CreateCommand()) {
            command.CommandText = $"""
                SELECT ri.playeritemid, r.Text
                FROM ReplicaItem2 ri
                JOIN ReplicaItemRow r ON r.replicaitemid = ri.Id
                WHERE r.Type = {NameTextClass}
                  AND ri.playeritemid IN ({string.Join(",", cropped.Select(item => item.Id))});
                """;

            var stored = cropped.ToDictionary(item => item.Id, item => item.Name);
            var seen = new HashSet<long>();
            try {
                using var reader = command.ExecuteReader();
                while (reader.Read()) {
                    if (reader.IsDBNull(1)) continue;

                    var id = reader.GetInt64(0);
                    var name = LootedItem.StripColourCodes(reader.GetString(1));
                    // Upstream has no ordinal column on these rows and relies on insertion
                    // order; the first name line is the name, as AttachReplica reads it.
                    if (name.Length == 0 || !seen.Add(id)) continue;

                    // A tooltip that says the same thing is not a repair. It is possible — the
                    // game draws an affix-only name for an item that genuinely has one — and
                    // rewriting a row to the value it already holds would make every sweep
                    // report work and never settle.
                    if (stored.TryGetValue(id, out var current)
                        && string.Equals(current, name, StringComparison.Ordinal)) {
                        continue;
                    }

                    repaired.Add((id, name));
                }
            }
            catch (SqliteException) { return repaired; }   // no tooltips stored yet
        }

        if (report is not null) {
            report($"restored {repaired.Count:N0} item name(s) from the game's own tooltip"
                   + (cropped.Count > repaired.Count
                       ? $"; {cropped.Count - repaired.Count:N0} more have no tooltip to restore from "
                         + "and keep their affix, which parsing the mod they come from will fix"
                       : ""));
        }

        return repaired;
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
