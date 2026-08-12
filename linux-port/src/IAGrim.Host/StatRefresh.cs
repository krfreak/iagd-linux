using IAGrim.Core.ItemStats;
using IAGrim.Platform;
using Microsoft.Data.Sqlite;

namespace IAGrim.Host;

/// <summary>
/// Runs the analysis pass when the collection needs it, without being asked.
///
/// Upstream does this: <c>RequiresStatUpdate</c> is checked at startup, and a parse runs when
/// more than fifty items lack a rarity or the skills table is empty. Nothing about that is
/// optional from the user's point of view — an item with no rarity is drawn in the "unknown"
/// colour rather than as the epic it is, and every record-driven filter matches nothing.
///
/// This port had left it as a command to run by hand (<c>iagd stats</c>), which meant a merged
/// collection sat there grey and unfilterable until someone read the documentation. The command
/// still exists for when you want it now rather than at the next start.
/// </summary>
public static class StatRefresh {
    /// <summary>Upstream's threshold, from PlayerItemDaoImpl.RequiresStatUpdate.</summary>
    private const int MissingRarityThreshold = 50;

    /// <summary>Why a pass is needed, or null when it is not.</summary>
    public static string? Needed(string databasePath) {
        try {
            using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            connection.Open();

            long Count(string sql) {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                try { return Convert.ToInt64(command.ExecuteScalar()); }
                catch (SqliteException) { return 0; }
            }

            if (Count("SELECT COUNT(*) FROM PlayerItem;") == 0) return null;

            var missingRarity = Count("SELECT COUNT(*) FROM PlayerItem WHERE Rarity IS NULL;");
            if (missingRarity > MissingRarityThreshold) {
                return $"{missingRarity:N0} item(s) have no rarity yet";
            }

            // Not upstream's condition but the same class of problem: a re-parse empties this
            // table, and without it the slot, damage-type and mastery filters match nothing.
            if (Count("SELECT COUNT(*) FROM DatabaseItemStat_v2;") == 0) {
                return "the game's stat rows were cleared by a re-parse";
            }

            if (Count("SELECT COUNT(*) FROM itemskill_v2;") == 0) {
                return "no item skills have been read yet";
            }

            return null;
        }
        catch (SqliteException) { return null; }
    }

    /// <summary>
    /// Runs the pass in the background, reporting through the same channel a merge uses.
    /// Returns immediately; the collection is usable throughout, just incompletely described.
    /// </summary>
    public static Task RunIfNeededAsync(string databasePath, string? gameDir, EventHub events,
                                        CancellationToken cancellationToken) {
        var reason = Needed(databasePath);
        if (reason is null) return Task.CompletedTask;

        if (gameDir is null) {
            Console.WriteLine($"stats: {reason}, but Grim Dawn was not found — set the game folder.");
            return Task.CompletedTask;
        }

        return Task.Run(async () => {
            try {
                Console.WriteLine($"stats: {reason}; computing.");
                await events.BroadcastAsync(
                    HostEvent.Message($"Analysing the collection: {reason}.", "info"), cancellationToken);

                var service = new StatPrecomputeService(databasePath, gameDir);
                var result = await Task.Run(() => service.Run(), cancellationToken);

                Console.WriteLine($"stats: {result.ItemsComputed:N0} of {result.ItemsProcessed:N0} items rolled.");
                await events.BroadcastAsync(
                    HostEvent.Message($"Analysed {result.ItemsComputed:N0} item(s).", "info"), cancellationToken);
            }
            catch (Exception ex) {
                // The collection is still perfectly usable without this; say so and carry on.
                Console.Error.WriteLine($"stats: pass failed: {ex.Message}");
            }
        }, cancellationToken);
    }
}
