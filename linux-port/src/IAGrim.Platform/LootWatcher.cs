namespace IAGrim.Platform;

/// <summary>Reports what happened to a single loot file.</summary>
/// <param name="Id">The new row, so a caller can show exactly the item that arrived.</param>
public sealed record LootImportResult(string File, LootedItem? Item, string? Error, bool Duplicate,
                                      long Id = 0);

/// <summary>
/// Imports loot files the hook drops into the bridge.
///
/// Deliberately polling rather than inotify. The files are written from inside a Wine
/// process, and the hook creates them and then writes them in separate steps, so an
/// inotify Created event routinely fires on a file that is still empty — exactly the
/// zero-byte state seen during development. Polling with a settle check is simpler and
/// does not race the writer. Upstream polls too.
/// </summary>
public sealed class LootWatcher : IDisposable {
    private readonly PrefixBridge _bridge;
    private readonly LootStore _store;
    private readonly string _backupDir;
    private readonly HashSet<string> _failed = [];

    /// <summary>Files younger than this are assumed to still be mid-write.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromMilliseconds(750);

    public event Action<LootImportResult>? OnImported;

    public LootWatcher(PrefixBridge bridge, LootStore store, string? backupDir = null) {
        _bridge = bridge;
        _store = store;
        _backupDir = backupDir ?? LinuxPaths.LootBackupDir;
    }

    /// <summary>
    /// One import pass. Returns what it did, so a caller can drive this from a timer, a
    /// test, or a one-shot CLI without owning a loop.
    /// </summary>
    public IReadOnlyList<LootImportResult> ImportPending() {
        var results = new List<LootImportResult>();

        foreach (var file in _bridge.PendingLootFiles()) {
            if (_failed.Contains(file)) continue;

            // Skip files the hook may still be writing.
            try {
                if (DateTime.Now - File.GetLastWriteTime(file) < SettleTime) continue;
                if (new FileInfo(file).Length == 0) continue;
            }
            catch (IOException) {
                continue;
            }

            var result = Import(file);
            results.Add(result);
            OnImported?.Invoke(result);
        }

        return results;
    }

    private LootImportResult Import(string file) {
        var item = LootCsv.ParseFile(file, out var error);

        if (item is null) {
            // Keep the file and stop retrying it: it is the only copy of whatever the hook
            // pulled out of the stash, so deleting an unparseable one loses an item.
            _failed.Add(file);
            return new LootImportResult(file, null, error, false);
        }

        if (_store.Exists(item)) {
            Consume(file);
            return new LootImportResult(file, item, null, Duplicate: true);
        }

        var id = _store.Insert(item);
        Consume(file);
        return new LootImportResult(file, item, null, false, id);
    }

    /// <summary>
    /// Removes the file from the queue once it is safely in the database — but keeps a copy.
    /// The item exists nowhere else at this point: the hook already took it out of the game.
    /// The copy is not kept forever; see <see cref="PruneBackups"/>.
    /// </summary>
    private void Consume(string file) {
        try {
            Directory.CreateDirectory(_backupDir);
            var destination = BackupTarget(file, _backupDir);
            File.Copy(file, destination, overwrite: true);
            File.Delete(file);
        }
        catch (IOException) {
            // Left in place; the next pass will see it as a duplicate and retry the move.
        }
    }

    /// <summary>
    /// Where a consumed loot file is copied to, avoiding the one case where the obvious answer
    /// loses an item.
    ///
    /// The hook names files at random (<c>HookDll/Hook/InventorySack_AddItem.cpp</c>,
    /// <c>randomFilename</c>) rather than after anything about the item, so a name already in
    /// the backup directory is nearly always this same file's earlier copy — <c>File.Copy</c>
    /// succeeded on an earlier pass and the <c>File.Delete</c> after it did not. Overwriting
    /// that is correct and is why the copy is unconditional rather than skipped.
    ///
    /// A name that is already here holding *different* bytes is a different item, and
    /// overwriting it would destroy the only record that it ever existed. Upstream has the same
    /// case and answers it the same way, with a distinct name rather than a clobber
    /// (<c>CsvParsingService.Handle</c>, the <c>-conflict.csv</c> branch).
    /// </summary>
    public static string BackupTarget(string file, string backupDir) {
        var destination = Path.Combine(backupDir, Path.GetFileName(file));

        if (File.Exists(destination) && !SameContent(file, destination)) {
            return Path.Combine(backupDir,
                                $"{Path.GetFileNameWithoutExtension(file)}-{Guid.NewGuid():N}-conflict.csv");
        }

        return destination;
    }

    /// <summary>Whole-file comparison, which loot files are small enough for — one item.</summary>
    private static bool SameContent(string a, string b) {
        try {
            return new FileInfo(a).Length == new FileInfo(b).Length
                && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (IOException) {
            return false;   // unreadable counts as different: a new name keeps both
        }
    }

    /// <summary>
    /// How long a consumed loot file is kept. Upstream's own number for the same files
    /// (<c>CsvParsingService.Start</c> deletes anything in <c>ingoing/deleted</c> more than
    /// three days old), and this port is already more generous than upstream in what it puts
    /// there: upstream deletes a successfully looted file outright and only keeps refused
    /// duplicates.
    /// </summary>
    public static readonly TimeSpan BackupRetention = TimeSpan.FromDays(3);

    /// <summary>What the backup directory is holding right now.</summary>
    /// <param name="Expired">
    /// How much of it a sweep would take. Shown before the button is pressed, because "delete
    /// 460 of 884 files" is a decision and "clean up" on its own is not.
    /// </param>
    public sealed record BackupUsage(string Path, int Files, long Bytes, int Expired, long ExpiredBytes);

    /// <summary>
    /// Counts the directory without changing it, for the Settings page's cleanup panel.
    ///
    /// Deliberately not folded into <c>/api/settings</c>: walking the whole directory is the
    /// expensive part, and the settings page loads on every visit while this is asked for once
    /// per panel.
    /// </summary>
    public static BackupUsage InspectBackups(string? backupDir = null, TimeSpan? retention = null) {
        var directory = backupDir ?? LinuxPaths.LootBackupDir;
        var cutoff = DateTime.Now - (retention ?? BackupRetention);
        var files = 0;
        var expired = 0;
        long bytes = 0;
        long expiredBytes = 0;

        try {
            foreach (var path in Directory.EnumerateFiles(directory, "*.csv")) {
                try {
                    var info = new FileInfo(path);
                    var length = info.Length;
                    files++;
                    bytes += length;

                    if (info.LastWriteTime <= cutoff) {
                        expired++;
                        expiredBytes += length;
                    }
                }
                catch (IOException) {
                    // Deleted between the listing and the stat; it is not there to report.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Same as the sweep below: no directory yet, or not ours to read.
        }

        return new BackupUsage(directory, files, bytes, expired, expiredBytes);
    }

    /// <summary>
    /// Deletes loot files kept past <see cref="BackupRetention"/>, and returns how many.
    ///
    /// Nothing else empties this directory, and every item that has ever been looted passes
    /// through it — a collection built up over a few months leaves tens of thousands of files
    /// behind, each one a copy of a row the database already has. Upstream sweeps the
    /// equivalent directory once when it starts, and so does this: callers run it at startup,
    /// not per pass, because the host builds a new <see cref="LootWatcher"/> every two seconds
    /// and re-walking the directory that often is the cost this is meant to avoid.
    ///
    /// The Settings page can also ask for it on demand (<c>POST /api/loot-backup/prune</c>),
    /// which is the same sweep with the same three days rather than a second policy — someone
    /// who has just noticed the directory should not have to restart the client to empty it.
    /// </summary>
    public static int PruneBackups(string? backupDir = null, TimeSpan? retention = null) {
        var directory = backupDir ?? LinuxPaths.LootBackupDir;
        var cutoff = DateTime.Now - (retention ?? BackupRetention);
        var removed = 0;

        try {
            foreach (var file in Directory.EnumerateFiles(directory, "*.csv")) {
                try {
                    if (File.GetLastWriteTime(file) > cutoff) continue;
                    File.Delete(file);
                    removed++;
                }
                catch (IOException) {
                    // Gone or held open; the next run sweeps it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // No backup directory yet, or not ours to read. Nothing to clean up either way.
        }

        return removed;
    }

    public void Dispose() { }
}
