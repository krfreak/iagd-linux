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
    /// <summary>
    /// True while a pass is running, for the status the UI shows.
    ///
    /// The pass takes ten seconds or more and rewrites what every filter reads. Running it
    /// silently means a client that looks finished while half its filters still match nothing —
    /// which is exactly how the stale mastery rows presented: an idle-looking window, a full
    /// collection, and an empty result.
    /// </summary>
    public static bool Running { get; private set; }

    /// <summary>The last line the pass reported, or null when it is not running.</summary>
    public static string? Step { get; private set; }

    /// <summary>One pass at a time: it rewrites the tables every other query reads.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Describes items that have no rarity yet, using the stat rows already stored.
    ///
    /// This is the cheap half and it runs first, because it needs no game archives and takes
    /// milliseconds. Upstream only triggers its full parse past fifty such items; below that it
    /// relies on having described each item as it was imported. This port needs the same safety
    /// net for items that arrived before that code existed, or during a version where it did not
    /// run — otherwise a single item sits there undescribed for ever, which is exactly what
    /// happened.
    /// </summary>
    /// <summary>Upstream's threshold, from PlayerItemDaoImpl.RequiresStatUpdate.</summary>
    private const int MissingRarityThreshold = 50;

    /// <returns>How many could not be described from stored data.</returns>
    private static int DescribeStragglers(string databasePath) {
        try {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();

            var ids = new List<long>();
            using (var command = connection.CreateCommand()) {
                command.CommandText =
                    $"SELECT Id FROM PlayerItem WHERE Rarity IS NULL LIMIT {NewItemDetails.MaxItemsPerBatch};";
                using var reader = command.ExecuteReader();
                while (reader.Read()) ids.Add(reader.GetInt64(0));
            }

            if (ids.Count == 0) return 0;

            var (described, skipped) = NewItemDetails.Apply(connection, ids);
            if (described > 0) Console.WriteLine($"stats: described {described:N0} item(s) from stored data.");
            return skipped;
        }
        catch (SqliteException) { return 0; }
    }

    /// <summary>
    /// Rewrites any stored name that is not the one the game data composes.
    ///
    /// Cheap and safe to call often: it reads five stat fields per record and writes only the
    /// rows that differ, measured at 150-190 ms over 15,177 items with nothing to change. It is
    /// called at startup and again whenever items arrive from the online backup, which are the
    /// two moments a foreign name can enter the collection — see <see cref="ItemNameRefresh"/>.
    /// </summary>
    /// <returns>How many names were rewritten.</returns>
    public static int RefreshNames(string databasePath) {
        try {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();

            var renamed = ItemNameRefresh.Run(connection, null, line => Console.WriteLine($"names: {line}."));
            if (renamed > 0) Console.WriteLine($"names: rewrote {renamed:N0} item name(s) from the game data.");
            return renamed;
        }
        catch (SqliteException) { return 0; }
    }

    /// <summary>Why a full pass is needed, or null when it is not.</summary>
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

            // Upstream's threshold, and it earns its keep: some items can never be described
            // — a quest item has no classification in the game's data at all — so a trigger of
            // "anything lacking a rarity" would rescan the archives at every launch for ever.
            // The cheap pass above has already handled everything describable from stored data,
            // so what remains here is a genuinely unparsed collection.
            var missingRarity = Count("SELECT COUNT(*) FROM PlayerItem WHERE Rarity IS NULL;");
            if (missingRarity > MissingRarityThreshold) {
                return $"{missingRarity:N0} item(s) have no rarity yet";
            }

            // Not upstream's condition but the same class of problem: a re-parse empties this
            // table, and without it the slot, damage-type and mastery filters match nothing.
            if (Count("SELECT COUNT(*) FROM DatabaseItemStat_v2;") == 0) {
                return "the game's stat rows were cleared by a re-parse";
            }

            // The rows are there but were written by an older version of the pass, which did not
            // write everything the filters now read. Nothing else notices this: the collection
            // has rarities, names and colours, and only the affected filter comes back empty.
            string? StoredVersion() {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Value FROM GameDataMeta WHERE Key = $key;";
                command.Parameters.AddWithValue("$key", StatPrecomputeService.VersionKey);
                try { return command.ExecuteScalar() as string; }
                catch (SqliteException) { return null; }
            }

            if (!int.TryParse(StoredVersion(), out var stored) || stored < StatPrecomputeService.Version) {
                return "the game's stat rows predate the current analysis";
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
        // The cheap passes first: most of the time they leave nothing for the expensive one.
        DescribeStragglers(databasePath);
        RefreshNames(databasePath);

        var reason = Needed(databasePath);
        if (reason is null) return Task.CompletedTask;

        if (gameDir is null) {
            Console.WriteLine($"stats: {reason}, but Grim Dawn was not found — set the game folder.");
            return Task.CompletedTask;
        }

        return Task.Run(async () => {
            if (!await Gate.WaitAsync(0, cancellationToken)) return;

            try {
                Running = true;
                Step = reason;
                Console.WriteLine($"stats: {reason}; computing.");
                await events.BroadcastAsync(
                    HostEvent.Message($"Analysing the collection: {reason}.", "info"), cancellationToken);

                var service = new StatPrecomputeService(databasePath, gameDir);
                var result = await Task.Run(() => service.Run(line => {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    // Picked up by the status poll, which broadcasts when anything visible moves.
                    Step = line.Trim();
                    Console.WriteLine($"stats: {Step}");
                }), cancellationToken);

                Console.WriteLine($"stats: {result.ItemsComputed:N0} of {result.ItemsProcessed:N0} items rolled.");
                await events.BroadcastAsync(
                    HostEvent.Message($"Analysed {result.ItemsComputed:N0} item(s).", "info"), cancellationToken);
            }
            catch (Exception ex) {
                // The collection is still perfectly usable without this; say so and carry on.
                Console.Error.WriteLine($"stats: pass failed: {ex.Message}");
            }
            finally {
                Running = false;
                Step = null;
                Gate.Release();
            }
        }, cancellationToken);
    }
}
