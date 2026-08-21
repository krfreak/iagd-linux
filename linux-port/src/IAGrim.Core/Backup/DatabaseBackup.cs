using Microsoft.Data.Sqlite;

namespace IAGrim.Core.Backup;

/// <summary>
/// Point-in-time copies of the collection.
///
/// The collection is the only irreplaceable thing here: templates, icons and computed stats all
/// regenerate from the game files in seconds, but a looted item exists nowhere else once its
/// loot file has been consumed. Upstream protects this with cloud sync, which this port does not
/// have — so it protects it with local copies instead.
///
/// **Copied with <c>VACUUM INTO</c>, not with File.Copy.** The database runs in WAL mode, so at
/// any moment part of the committed state lives in <c>-wal</c> rather than in the main file.
/// Copying the file alone can therefore produce a backup missing the most recent items, or a
/// torn one — and it would look like it worked. <c>VACUUM INTO</c> takes a read transaction, so
/// it captures a consistent snapshot including the WAL, and compacts it on the way out. It is
/// also safe while the host is running, which matters because that is when backups happen.
/// </summary>
public static class DatabaseBackup {
    public sealed record BackupInfo(string Path, DateTime Created, long Bytes);

    /// <summary>
    /// How many copies to keep. Enough to survive a bad day of not noticing, not so many that
    /// they dominate the data directory.
    /// </summary>
    public const int KeepCount = 10;

    /// <summary>
    /// Writes a timestamped copy and prunes old ones.
    /// </summary>
    /// <param name="reason">
    /// Folded into the filename, so a directory listing says why each copy exists — "before a
    /// re-parse" and "the daily one" are different kinds of reassuring.
    /// </param>
    public static BackupInfo Create(string databasePath, string backupDir, string reason = "manual") {
        Directory.CreateDirectory(backupDir);

        var safeReason = new string(reason.Where(c => char.IsLetterOrDigit(c) || c is '-').ToArray());
        if (safeReason.Length == 0) safeReason = "manual";

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(backupDir, $"userdata-{stamp}-{safeReason}.db");

        // Two backups within the same second — a merge immediately followed by an import, a
        // script running the CLI twice back to back — would otherwise collide on this filename.
        // VACUUM INTO refuses to write over an existing file, so the second backup would throw
        // before it copied anything, and the operation it was meant to protect would fail with
        // an opaque SQLite error instead of getting the copy it asked for.
        var suffix = 1;
        while (File.Exists(target)) {
            target = Path.Combine(backupDir, $"userdata-{stamp}-{safeReason}-{++suffix}.db");
        }

        using (var connection = new SqliteConnection($"Data Source={databasePath}")) {
            connection.Open();
            using var command = connection.CreateCommand();
            // The path is a literal because VACUUM INTO does not accept a bound parameter.
            // Single quotes are doubled, which is the only escape SQLite string literals have.
            command.CommandText = $"VACUUM INTO '{target.Replace("'", "''")}';";
            command.ExecuteNonQuery();
        }

        Prune(backupDir);
        return new BackupInfo(target, File.GetLastWriteTime(target), new FileInfo(target).Length);
    }

    /// <summary>Existing backups, newest first.</summary>
    public static IReadOnlyList<BackupInfo> List(string backupDir) {
        if (!Directory.Exists(backupDir)) return [];

        return Directory.GetFiles(backupDir, "userdata-*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTime)
            .Select(file => new BackupInfo(file.FullName, file.LastWriteTime, file.Length))
            .ToList();
    }

    /// <summary>
    /// Restores a backup over the live database.
    ///
    /// The current database is copied aside first, unconditionally. Restoring is the operation
    /// most likely to be done in a panic and regretted — "I restored the wrong one" has to stay
    /// recoverable.
    /// </summary>
    public static string Restore(string backupPath, string databasePath) {
        if (!File.Exists(backupPath)) {
            throw new FileNotFoundException($"No such backup: {backupPath}");
        }

        var displaced = databasePath + $".replaced-{DateTime.Now:yyyyMMdd-HHmmss}";
        if (File.Exists(databasePath)) {
            File.Copy(databasePath, displaced, overwrite: true);
        }

        // The WAL and shared-memory files belong to the database being replaced. Leaving them
        // would let SQLite replay a log against a file it was never written for.
        foreach (var sidecar in new[] { databasePath + "-wal", databasePath + "-shm" }) {
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }

        File.Copy(backupPath, databasePath, overwrite: true);
        return displaced;
    }

    /// <summary>
    /// Keeps the newest <see cref="KeepCount"/> and deletes the rest.
    ///
    /// Deliberately count-based rather than age-based: someone who has not played in six months
    /// should still have their backups when they come back.
    /// </summary>
    private static void Prune(string backupDir) {
        foreach (var old in List(backupDir).Skip(KeepCount)) {
            try { File.Delete(old.Path); }
            catch (IOException) { /* in use or gone; the next run will retry */ }
        }
    }
}
