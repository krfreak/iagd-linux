using Microsoft.Data.Sqlite;

namespace IAGrim.Platform;

/// <summary>The outcome of pointing this port at a particular database.</summary>
public sealed record DatabaseChoice(string Path, bool Existing, bool Adopted, string? BackupPath) {
    /// <summary>Null when nothing needs saying; otherwise a line worth printing at startup.</summary>
    public string? Notice =>
        !Adopted ? null
      : BackupPath is null
            ? $"Opening an existing collection: {Path}"
            : $"Opening an existing collection: {Path}\n"
              + $"  A copy was saved first: {BackupPath}";
}

/// <summary>
/// Opening a database that this port did not create.
///
/// The schema here is upstream's, so an IAGD database from a Windows install — or from a Wine
/// prefix, or a second collection kept elsewhere — can be opened directly rather than imported.
/// Two things have to happen carefully when that is someone's real collection:
///
/// 1. **Refuse the wrong file.** A path typo, or a database belonging to something else
///    entirely, must not have IAGD's tables created inside it. So an existing file is inspected
///    first and rejected unless it actually looks like an IAGD database.
/// 2. **Copy it before changing it.** This port adds tables upstream does not have
///    (ItemTemplate, GameDataMeta) and indices, which is safe and reversible but is still a
///    modification of a file the user did not make with this program. A copy is taken the first
///    time, and only the first time — detected by whether those additive tables already exist,
///    which needs no state of our own.
/// </summary>
public static class DatabaseSelection {
    /// <summary>Tables that mean "this is an IAGD collection".</summary>
    private static readonly string[] Signature = ["PlayerItem", "PlayerItemRecord"];

    /// <summary>
    /// Checks the chosen database and, when it is an existing collection this port has not
    /// opened before, copies it aside.
    /// </summary>
    /// <exception cref="InvalidDataException">The file exists but is not an IAGD database.</exception>
    public static DatabaseChoice Prepare(string path) {
        var isDefault = !LinuxPaths.IsDatabaseOverridden;

        if (!File.Exists(path)) {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is not null && !Directory.Exists(directory)) {
                throw new InvalidDataException($"the directory {directory} does not exist");
            }
            // A new file at a chosen location is a legitimate way to keep a second collection.
            return new DatabaseChoice(path, Existing: false, Adopted: false, BackupPath: null);
        }

        var (looksRight, hasOurTables) = Inspect(path);
        if (!looksRight) {
            throw new InvalidDataException(
                $"{path} is not an Item Assistant database (no PlayerItem table). "
                + "Refusing to create tables in it.");
        }

        // Already ours, or the default location: nothing to announce.
        if (hasOurTables || isDefault) {
            return new DatabaseChoice(path, Existing: true, Adopted: false, BackupPath: null);
        }

        string? backup = null;
        try {
            backup = DatabaseBackupCopy(path);
        }
        catch (Exception) {
            // A failed copy must not stop the collection opening; the schema changes are
            // additive and upstream reads the file regardless.
        }

        return new DatabaseChoice(path, Existing: true, Adopted: true, BackupPath: backup);
    }

    /// <summary>Does this file look like an IAGD database, and has this port opened it before?</summary>
    private static (bool LooksRight, bool HasOurTables) Inspect(string path) {
        try {
            // Read-only: this runs before any decision to modify the file.
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT LOWER(name) FROM sqlite_master WHERE type = 'table';";

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));

            return (Signature.All(tables.Contains), tables.Contains("ItemTemplate"));
        }
        catch (SqliteException) {
            return (false, false);   // not SQLite, or unreadable
        }
    }

    /// <summary>
    /// Copies the database into this port's own backup directory — never next to the original,
    /// which may be inside a Wine prefix or a directory the user did not expect us to write to.
    /// </summary>
    private static string DatabaseBackupCopy(string path) {
        var directory = LinuxPaths.BackupDir;
        var target = Path.Combine(directory,
            $"adopted-{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileNameWithoutExtension(path)}.db");

        // VACUUM INTO rather than File.Copy, for the WAL reason documented on DatabaseBackup:
        // part of the committed state can live outside the main file.
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"VACUUM INTO '{target.Replace("'", "''")}';";
        command.ExecuteNonQuery();

        return target;
    }
}
