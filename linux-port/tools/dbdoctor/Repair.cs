using IAGrim.Platform;
using Microsoft.Data.Sqlite;

namespace IAGrim.DbDoctor;

/// <summary>What one repair did, or would do.</summary>
public sealed record RepairOutcome(string Id, string Summary, long Rows, IReadOnlyList<string>? Notes = null);

/// <summary>
/// The repairs, and the rules that keep them from making things worse.
///
/// Three of them are local tidying — mod tags, orphaned rows, timestamps — and change nothing
/// anyone else can see. The fourth, collapsing duplicates, reaches the server, and it is the
/// only reason this class needs care.
///
/// <b>Deleting a duplicate row without writing a tombstone is the bug this tool exists to
/// diagnose.</b> The row goes, the server still holds the item, the next download brings it
/// back, and the collection is exactly where it started with one more round-trip of confusion.
/// So the deletion is recorded through <c>CloudTombstone.Mark</c> — the same call the app makes
/// when the game takes an item — rather than through SQL of this tool's own. That call carries
/// upstream's rule with it: a tombstone only for an item the server already knows, because
/// <c>PlayerItemDaoImpl.Remove</c> returns early on <c>!item.IsCloudSynchronized</c>. The
/// tombstones go in before the rows come out, because once the row is gone so is the cloud id
/// that identifies it.
///
/// Everything happens in one transaction per repair, opened with BEGIN IMMEDIATE so a running
/// app is a refusal rather than a half-finished write.
/// </summary>
public sealed class Repair : IDisposable {
    private readonly SqliteConnection _connection;

    public Repair(string databasePath) {
        var connectionString = new SqliteConnectionStringBuilder {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        // Fail fast rather than sit on a lock. A collection the app has open should be reported
        // as such, not waited on for thirty seconds and then written to anyway.
        Execute("PRAGMA busy_timeout = 2000;");

        // Every repair below assumes the collection exists. A file without it is not a
        // collection this tool should be writing to, whatever else it may be.
        if (Count("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'PlayerItem';") == 0) {
            throw new InvalidOperationException(
                "this database has no PlayerItem table, so there is no collection to repair");
        }
    }

    /// <summary>
    /// A copy of the database, taken before anything is written.
    ///
    /// Named for what it precedes rather than the date alone, matching the
    /// <c>userdata.db.pre-dedupe-*</c> file an earlier manual clean-up left behind. Any
    /// sidecar WAL is checkpointed into the copy first, so the backup is a complete database
    /// rather than one that needs its journal to be readable.
    /// </summary>
    public string Backup(string databasePath) {
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var destination = $"{databasePath}.pre-repair-{stamp}";

        // Copied through SQLite rather than File.Copy: VACUUM INTO takes a consistent snapshot
        // of a live database, where a byte copy of one being written to is a torn file.
        using var command = _connection.CreateCommand();
        command.CommandText = "VACUUM INTO $destination;";
        command.Parameters.AddWithValue("$destination", destination);
        command.ExecuteNonQuery();

        return destination;
    }

    /// <summary>
    /// Collapses each duplicated item to its oldest copy, telling the server about every copy
    /// it removes.
    ///
    /// The guard worth explaining is the second condition on the tombstone insert: a cloud id
    /// that a surviving row also carries is never tombstoned. Two rows sharing one cloud id is
    /// its own finding (`shared-cloud-id`) and it is rare, but sending that id as a deletion
    /// would tell the server to drop the item this repair just decided to keep — and the other
    /// machine would lose it for good. Skipping those leaves a duplicate behind, which is the
    /// recoverable half of the choice.
    /// </summary>
    public RepairOutcome Duplicates(bool commit) {
        var notes = new List<string>();

        var redundant = Count($"SELECT COUNT(*) FROM PlayerItem WHERE {Sql.RedundantRows};");
        if (redundant == 0) return new RepairOutcome("duplicates", "no duplicate rows to remove", 0);

        // Which of those rows the server needs to be told about.
        const string tombstoneable = $"""
            {Sql.RedundantRows}
              AND cloudid IS NOT NULL AND cloudid <> ''
              AND cloud_hassync = 1
              AND cloudid NOT IN (
                  SELECT cloudid FROM PlayerItem
                  WHERE NOT ({Sql.RedundantRows}) AND cloudid IS NOT NULL AND cloudid <> '')
            """;

        var tombstones = Count($"SELECT COUNT(DISTINCT cloudid) FROM PlayerItem WHERE {tombstoneable};");

        var neverUploaded = Count($"""
            SELECT COUNT(*) FROM PlayerItem
            WHERE {Sql.RedundantRows} AND (cloudid IS NULL OR cloudid = '' OR cloud_hassync <> 1);
            """);

        if (neverUploaded > 0) {
            notes.Add($"{neverUploaded} of them were never uploaded — removed without a tombstone, "
                    + "which is upstream's rule: the server has nothing to forget.");
        }

        var shared = Count($"""
            SELECT COUNT(*) FROM PlayerItem
            WHERE {Sql.RedundantRows} AND cloudid IN (
                SELECT cloudid FROM PlayerItem
                WHERE NOT ({Sql.RedundantRows}) AND cloudid IS NOT NULL AND cloudid <> '');
            """);

        if (shared > 0) {
            notes.Add($"{shared} share a cloud id with a row being kept and are left alone — "
                    + "deleting that id would remove the kept item from your other machines.");
        }

        var summary = $"remove {redundant - shared} duplicate row(s), telling the server about {tombstones}";
        if (!commit) return new RepairOutcome("duplicates", summary, redundant - shared, notes);

        using var transaction = _connection.BeginTransaction(deferred: false);

        // The doomed ids are fixed in a temp table before anything is deleted, for two reasons.
        // Correctness: every predicate here is defined against PlayerItem, and a DELETE whose
        // subquery reads the table being deleted from is asking SQLite for a guarantee it does
        // not give. Speed: `RedundantRows` re-derives a GROUP BY over the whole collection each
        // time it is evaluated, and it appears in six statements.
        //
        // The rows kept back are those sharing a cloud id with a survivor, so that everything
        // left in `doomed` is safe both to tombstone and to delete.
        Execute("DROP TABLE IF EXISTS doomed;", transaction);
        Execute($"""
            CREATE TEMP TABLE doomed AS
            SELECT Id FROM PlayerItem
            WHERE {Sql.RedundantRows} AND (cloudid IS NULL OR cloudid = '' OR cloudid NOT IN (
                SELECT cloudid FROM PlayerItem
                WHERE NOT ({Sql.RedundantRows}) AND cloudid IS NOT NULL AND cloudid <> ''));
            """, transaction);

        // Tombstones before the rows: Mark reads the cloud id off the row it is told about, so
        // after the DELETE there would be nothing left to read. Called per item rather than as
        // one INSERT..SELECT so that the rule for *which* deletions the server is told about
        // stays in one place — CloudTombstone.Mark is what the app itself uses when an item is
        // transferred into the game, and a repair that decided this differently would be a
        // second opinion nobody asked for.
        foreach (var id in Ids("SELECT Id FROM doomed;", transaction)) {
            CloudTombstone.Mark(_connection, id, transaction);
        }

        foreach (var sql in new[] {
                     "DELETE FROM ReplicaItemRow WHERE replicaitemid IN (SELECT Id FROM ReplicaItem2 WHERE playeritemid IN (SELECT Id FROM doomed))",
                     "DELETE FROM ReplicaItem2 WHERE playeritemid IN (SELECT Id FROM doomed)",
                     "DELETE FROM PlayerItemRecord WHERE PlayerItemId IN (SELECT Id FROM doomed)",
                     "DELETE FROM ComputedItemStat WHERE playeritemid IN (SELECT Id FROM doomed)",
                     "DELETE FROM PlayerItem WHERE Id IN (SELECT Id FROM doomed)",
                 }) {
            Execute(sql, transaction);
        }

        var removed = Count("SELECT COUNT(*) FROM doomed;", transaction);
        Execute("DROP TABLE doomed;", transaction);

        transaction.Commit();
        return new RepairOutcome("duplicates", summary, removed, notes);
    }

    /// <summary>
    /// Tags items with the mod they came from.
    ///
    /// The mapping is the caller's, never guessed. <c>PlayerItem.Mod</c> holds the mod *folder*
    /// name — what Grim Dawn reports to the hook — and nothing in the collection records it once
    /// it is lost. A record path beginning `grimleague/` is a strong hint and no more: mods
    /// usually keep their records under `records/` like the base game, so a namespaced root is a
    /// convention of that mod's authors rather than a rule anything enforces. Guessing wrong
    /// writes a mod name that matches no installed folder, and the items stay exactly as
    /// unreadable while now claiming to come from somewhere real.
    /// </summary>
    public RepairOutcome ModTags(IReadOnlyDictionary<string, string> mapping, bool commit) {
        var notes = new List<string>();
        long total = 0;

        using var transaction = commit ? _connection.BeginTransaction(deferred: false) : null;

        foreach (var (root, mod) in mapping) {
            var sql = $"""
                {Sql.UntaggedModItem} AND {Sql.RecordRoot} = $root
                """;

            var affected = Count($"SELECT COUNT(*) FROM PlayerItem WHERE {sql};", transaction, ("$root", root));
            total += affected;
            notes.Add($"{affected,8}  {root}/… → Mod = '{mod}'");

            if (commit) {
                Execute($"UPDATE PlayerItem SET Mod = $mod WHERE {sql};", transaction,
                    ("$root", root), ("$mod", mod));
            }
        }

        transaction?.Commit();

        if (total > 0) {
            notes.Add("The server's copy still says base game until these are uploaded again; "
                    + "this repair does not force a re-upload.");
        }

        return new RepairOutcome("mod-tags", $"tag {total} item(s) with the mod they came from", total, notes);
    }

    /// <summary>
    /// Removes child rows whose parent is gone — the same four statements as
    /// <c>Schema.RemoveOrphanedRows</c>, which the app runs on every start.
    ///
    /// Worth doing here anyway for a collection that will be opened by the Windows tool, which
    /// has no equivalent sweep: SQLite reuses the row ids of deleted items, so a leftover child
    /// is inherited by the next item to take that id.
    /// </summary>
    public RepairOutcome Orphans(bool commit) {
        (string Table, string Sql)[] sweeps = [
            ("ReplicaItemRow", "DELETE FROM ReplicaItemRow WHERE replicaitemid NOT IN (SELECT Id FROM ReplicaItem2)"),
            ("ReplicaItem2", "DELETE FROM ReplicaItem2 WHERE playeritemid IS NOT NULL AND playeritemid NOT IN (SELECT Id FROM PlayerItem)"),
            ("PlayerItemRecord", "DELETE FROM PlayerItemRecord WHERE PlayerItemId NOT IN (SELECT Id FROM PlayerItem)"),
            ("ComputedItemStat", "DELETE FROM ComputedItemStat WHERE playeritemid NOT IN (SELECT Id FROM PlayerItem)"),
        ];

        var notes = new List<string>();
        long total = 0;

        using var transaction = commit ? _connection.BeginTransaction(deferred: false) : null;

        foreach (var (table, sql) in sweeps) {
            var counted = Count(sql.Replace("DELETE FROM", "SELECT COUNT(*) FROM"), transaction);
            if (counted == 0) continue;

            total += counted;
            notes.Add($"{counted,8}  {table}");

            if (commit) Execute(sql, transaction);
        }

        transaction?.Commit();
        return new RepairOutcome("orphans", $"remove {total} orphaned row(s)", total, notes);
    }

    /// <summary>Rewrites loot dates that were stored in seconds as the milliseconds upstream uses.</summary>
    public RepairOutcome Timestamps(bool commit) {
        var affected = Count($"SELECT COUNT(*) FROM PlayerItem WHERE {Sql.SecondsScaleTimestamp};");

        if (affected > 0 && commit) {
            using var transaction = _connection.BeginTransaction(deferred: false);
            Execute($"UPDATE PlayerItem SET created_at = created_at * 1000 WHERE {Sql.SecondsScaleTimestamp};",
                transaction);
            transaction.Commit();
        }

        return new RepairOutcome("timestamps", $"convert {affected} loot date(s) from seconds to milliseconds", affected);
    }

    // ---- plumbing -----------------------------------------------------------------------

    private long Count(string sql, SqliteTransaction? transaction = null,
                       params (string Name, object Value)[] parameters) {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);

        var result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private List<long> Ids(string sql, SqliteTransaction? transaction = null) {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private void Execute(string sql, SqliteTransaction? transaction = null,
                         params (string Name, object Value)[] parameters) {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
