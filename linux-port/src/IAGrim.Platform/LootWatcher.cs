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
    /// </summary>
    private void Consume(string file) {
        try {
            Directory.CreateDirectory(_backupDir);
            var destination = Path.Combine(_backupDir, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
            File.Delete(file);
        }
        catch (IOException) {
            // Left in place; the next pass will see it as a duplicate and retry the move.
        }
    }

    public void Dispose() { }
}
