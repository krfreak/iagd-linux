using IAGrim.Platform;

namespace IAGrim.Cloud.Tests;

/// <summary>Settings in memory, so a test does not touch <c>~/.config</c>.</summary>
public sealed class TestSettings : ICloudSettings {
    public string? CloudUser { get; set; }
    public string? CloudAuthToken { get; set; }
    public long CloudUploadTimestamp { get; set; }
    public bool UsingDualComputer { get; set; }
    public long? BuddySyncUserIdV3 { get; set; }
    public bool OptOutOfBackups { get; set; }
    public DateTime LastCharSyncUtc { get; set; }

    /// <summary>How many times the settings were persisted, so a test can assert one happened.</summary>
    public int Saves { get; private set; }

    public void Save() => Saves++;
}

/// <summary>
/// A collection database in a temp directory, with the cloud store open on it.
///
/// Two of these against one account is how the "second PC" tests work: the same collection seen
/// by two independent clients, which is the situation every duplication and every lost-deletion
/// bug in this feature comes from.
/// </summary>
public sealed class TestCollection : IDisposable {
    public string Path { get; }
    public CloudItemStoreHandle Store { get; }

    public TestCollection() {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"iagd-cloud-{Guid.NewGuid():N}.db");
        Store = new CloudItemStoreHandle(Path);
    }

    /// <summary>Puts an item in the collection as if it had just been looted: no upload yet.</summary>
    public long AddLootedItem(string name = "Looted Revolver", string? cloudId = null) {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PlayerItem (
                baserecord, PrefixRecord, SuffixRecord, ModifierRecord, TransmuteRecord,
                MateriaRecord, RelicCompletionBonusRecord, EnchantmentRecord,
                AscendantAffixNameRecord, AscendantAffix2hNameRecord,
                Seed, StackCount, created_at, Name, namelowercase, Rarity, LevelRequirement,
                Mod, IsHardcore, cloudid, cloud_hassync
            ) VALUES (
                'records/items/gearweapons/guns1h/c030_gun1h.dbr', '', '', '', '',
                '', '', '', '', '',
                $seed, 1, 1700000000000, $name, $lower, 'Blue', 94,
                '', 0, $cloudId, 0
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$seed", Random.Shared.NextInt64(1, int.MaxValue));
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$lower", name.ToLowerInvariant());
        command.Parameters.AddWithValue("$cloudId", (object?)cloudId ?? CloudIdentity.New());
        return (long)command.ExecuteScalar()!;
    }

    public int CountItems() {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PlayerItem;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int CountRecordsFor(string cloudId) {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM PlayerItemRecord WHERE PlayerItemId IN " +
            "(SELECT Id FROM PlayerItem WHERE cloudid = $id);";
        command.Parameters.AddWithValue("$id", cloudId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Marks an item deleted the way a transfer into the game does.</summary>
    public void TransferAway(long itemId) {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path}");
        connection.Open();

        using var transaction = connection.BeginTransaction();
        IAGrim.Platform.CloudTombstone.Mark(connection, itemId, transaction);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM PlayerItem WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", itemId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Dispose() {
        Store.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) {
            try { File.Delete(Path + suffix); } catch (IOException) { /* best effort */ }
        }
    }
}

/// <summary>
/// A <see cref="IAGrim.Cloud.Data.CloudItemStore"/> that can be reopened, so a test can prove
/// something reached disk rather than a cache.
/// </summary>
public sealed class CloudItemStoreHandle : IAGrim.Cloud.Data.ICloudItemStore, IDisposable {
    private readonly string _path;
    private IAGrim.Cloud.Data.CloudItemStore _store;

    public CloudItemStoreHandle(string path) {
        _path = path;
        _store = new IAGrim.Cloud.Data.CloudItemStore(path);
    }

    public void Reopen() {
        _store.Dispose();
        _store = new IAGrim.Cloud.Data.CloudItemStore(_path);
    }

    public IList<CloudItem> GetUnsynchronizedItems() => _store.GetUnsynchronizedItems();
    public void SetAsSynchronized(IList<CloudItem> items) => _store.SetAsSynchronized(items);
    public IList<string> GetOnlineIds() => _store.GetOnlineIds();
    public IList<IAGrim.Cloud.Dto.ItemIdentifierDto> GetItemsMarkedForOnlineDeletion() => _store.GetItemsMarkedForOnlineDeletion();
    public void ClearItemsMarkedForOnlineDeletion() => _store.ClearItemsMarkedForOnlineDeletion();
    public void Save(IList<CloudItem> items) => _store.Save(items);
    public void Delete(IList<IAGrim.Cloud.Dto.DeleteItemDto> items) => _store.Delete(items);
    public void ResetOnlineSyncState() => _store.ResetOnlineSyncState();

    public void Dispose() => _store.Dispose();
}
