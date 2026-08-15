using IAGrim.Cloud.Dto;
using IAGrim.Platform;
using Microsoft.Data.Sqlite;

namespace IAGrim.Cloud.Data;

/// <summary>
/// The collection, as online sync needs to see it — the cloud-relevant half of upstream's
/// <c>IPlayerItemDao</c>.
/// </summary>
public interface ICloudItemStore {
    /// <summary>Items that have never been uploaded: <c>cloud_hassync</c> null or 0.</summary>
    IList<CloudItem> GetUnsynchronizedItems();

    /// <summary>Records a successful upload: writes the cloud id and sets the flag.</summary>
    void SetAsSynchronized(IList<CloudItem> items);

    /// <summary>Every cloud id this collection already holds, so a download can skip them.</summary>
    IList<string> GetOnlineIds();

    /// <summary>Tombstones: items deleted here that the server has not been told about yet.</summary>
    IList<ItemIdentifierDto> GetItemsMarkedForOnlineDeletion();

    /// <summary>Drops the tombstones, once the server has accepted them.</summary>
    void ClearItemsMarkedForOnlineDeletion();

    /// <summary>Stores items that came down from the cloud.</summary>
    void Save(IList<CloudItem> items);

    /// <summary>Removes items deleted on another machine.</summary>
    void Delete(IList<DeleteItemDto> items);

    /// <summary>
    /// Marks every item as never-uploaded. Run when the account changes or the token turns out
    /// to be dead, because the collection now has to be offered to whatever account comes next.
    /// </summary>
    void ResetOnlineSyncState();
}

/// <summary>
/// <see cref="ICloudItemStore"/> over upstream's own tables.
///
/// Three columns carry the whole feature and all three are upstream's:
/// <c>PlayerItem.cloudid</c> (the identity the server knows an item by),
/// <c>PlayerItem.cloud_hassync</c> (has it been uploaded), and <c>deletedplayeritem_v3</c> (a
/// tombstone per item deleted here, so the deletion can be replayed to the server and from there
/// to the user's other machines).
///
/// The tombstone table is the reason a transfer does not resurrect: without it, the machine that
/// still has the item uploads it again and the item the player just moved into the game reappears
/// in the collection.
/// </summary>
public sealed class CloudItemStore : ICloudItemStore, IDisposable {
    private readonly SqliteConnection _connection;

    public CloudItemStore(string databasePath) {
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        Schema.Apply(_connection);
    }

    private const string ItemColumns = """
        Id, cloudid, cloud_hassync, baserecord, PrefixRecord, SuffixRecord, ModifierRecord,
        TransmuteRecord, MateriaRecord, RelicCompletionBonusRecord, EnchantmentRecord,
        AscendantAffixNameRecord, AscendantAffix2hNameRecord, Seed, RelicSeed, EnchantmentSeed,
        MateriaCombines, StackCount, RerollsUsed, AffixRerollsUsed, created_at, PrefixRarity,
        Name, namelowercase, Rarity, LevelRequirement, Mod, IsHardcore
        """;

    public IList<CloudItem> GetUnsynchronizedItems() {
        using var command = _connection.CreateCommand();
        // Null and 0 both mean "not uploaded". Upstream tests for exactly this pair, and rows
        // written by any tool that predates the column have NULL.
        command.CommandText =
            $"SELECT {ItemColumns} FROM PlayerItem WHERE cloud_hassync IS NULL OR cloud_hassync = 0;";

        var items = new List<CloudItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(Read(reader));
        return items;
    }

    public void SetAsSynchronized(IList<CloudItem> items) {
        using var transaction = _connection.BeginTransaction();
        foreach (var item in items) {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE PlayerItem SET cloud_hassync = 1, cloudid = $uuid WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$uuid", (object?)item.CloudId ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IList<string> GetOnlineIds() {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT cloudid FROM PlayerItem WHERE cloudid IS NOT NULL;";

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    public IList<ItemIdentifierDto> GetItemsMarkedForOnlineDeletion() {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id FROM deletedplayeritem_v3;";

        var items = new List<ItemIdentifierDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            if (!reader.IsDBNull(0)) items.Add(new ItemIdentifierDto { Id = reader.GetString(0) });
        }
        return items;
    }

    public void ClearItemsMarkedForOnlineDeletion() {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM deletedplayeritem_v3;";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Stores items that arrived from the cloud.
    ///
    /// The guard is upstream's and it is the only thing standing between a flaky connection and
    /// a duplicated collection: an item is stored if it is not marked synchronised, *or* if its
    /// cloud id is not already here. Everything coming down is marked synchronised, so in
    /// practice this is "skip anything we already own", by cloud id.
    /// </summary>
    public void Save(IList<CloudItem> items) {
        if (items.Count == 0) return;

        var known = new HashSet<string>(GetOnlineIds(), StringComparer.Ordinal);

        using var transaction = _connection.BeginTransaction();
        foreach (var item in items) {
            if (item.IsCloudSynchronized && item.CloudId is not null && !known.Add(item.CloudId)) {
                continue;
            }

            var id = Insert(item, transaction);
            InsertRecords(id, item, transaction);
        }
        transaction.Commit();
    }

    private long Insert(CloudItem item, SqliteTransaction transaction) {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO PlayerItem (
                cloudid, cloud_hassync, baserecord, PrefixRecord, SuffixRecord, ModifierRecord,
                TransmuteRecord, MateriaRecord, RelicCompletionBonusRecord, EnchantmentRecord,
                AscendantAffixNameRecord, AscendantAffix2hNameRecord, Seed, RelicSeed,
                EnchantmentSeed, MateriaCombines, StackCount, RerollsUsed, AffixRerollsUsed,
                created_at, PrefixRarity, Name, namelowercase, Rarity, LevelRequirement, Mod,
                IsHardcore
            ) VALUES (
                $cloudId, $hasSync, $base, $prefix, $suffix, $modifier,
                $transmute, $materia, $relicBonus, $enchantment,
                $asc1, $asc2, $seed, $relicSeed,
                $enchantmentSeed, $materiaCombines, $stack, $rerolls, $affixRerolls,
                $created, $prefixRarity, $name, $nameLower, $rarity, $level, $mod,
                $hardcore
            );
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$cloudId", (object?)item.CloudId ?? DBNull.Value);
        command.Parameters.AddWithValue("$hasSync", item.IsCloudSynchronized ? 1 : 0);
        // Records are stored as "" rather than NULL, which is what upstream's rows contain and
        // what several of its filters compare against. See Schema.NormaliseToUpstreamValues.
        command.Parameters.AddWithValue("$base", item.BaseRecord ?? "");
        command.Parameters.AddWithValue("$prefix", item.PrefixRecord ?? "");
        command.Parameters.AddWithValue("$suffix", item.SuffixRecord ?? "");
        command.Parameters.AddWithValue("$modifier", item.ModifierRecord ?? "");
        command.Parameters.AddWithValue("$transmute", item.TransmuteRecord ?? "");
        command.Parameters.AddWithValue("$materia", item.MateriaRecord ?? "");
        command.Parameters.AddWithValue("$relicBonus", item.RelicCompletionBonusRecord ?? "");
        command.Parameters.AddWithValue("$enchantment", item.EnchantmentRecord ?? "");
        command.Parameters.AddWithValue("$asc1", item.AscendantAffixNameRecord ?? "");
        command.Parameters.AddWithValue("$asc2", item.AscendantAffix2hNameRecord ?? "");
        command.Parameters.AddWithValue("$seed", item.Seed);
        command.Parameters.AddWithValue("$relicSeed", item.RelicSeed);
        command.Parameters.AddWithValue("$enchantmentSeed", item.EnchantmentSeed);
        command.Parameters.AddWithValue("$materiaCombines", item.MateriaCombines);
        command.Parameters.AddWithValue("$stack", item.StackCount);
        command.Parameters.AddWithValue("$rerolls", item.RerollsUsed);
        command.Parameters.AddWithValue("$affixRerolls", item.AffixRerollsUsed);
        command.Parameters.AddWithValue("$created", (object?)item.CreationDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$prefixRarity", item.PrefixRarity);
        command.Parameters.AddWithValue("$name", (object?)item.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$nameLower", (object?)item.NameLowercase ?? DBNull.Value);
        command.Parameters.AddWithValue("$rarity", (object?)item.Rarity ?? DBNull.Value);
        command.Parameters.AddWithValue("$level", item.LevelRequirement);
        command.Parameters.AddWithValue("$mod", (object?)item.Mod ?? DBNull.Value);
        command.Parameters.AddWithValue("$hardcore", item.IsHardcore ? 1 : 0);

        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// The records an item is made of, in <c>PlayerItemRecord</c>. Upstream's
    /// <c>GetRecordsForItem</c> set: base, prefix, suffix, materia and the two ascendant affixes.
    /// The damage-type and pet-bonus filters read this table rather than the item row, so an
    /// item stored without its records is invisible to most of the search.
    /// </summary>
    private void InsertRecords(long id, CloudItem item, SqliteTransaction transaction) {
        string?[] records = [
            item.BaseRecord, item.PrefixRecord, item.SuffixRecord, item.MateriaRecord,
            item.AscendantAffixNameRecord, item.AscendantAffix2hNameRecord,
        ];

        foreach (var record in records) {
            if (string.IsNullOrEmpty(record)) continue;

            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT OR IGNORE INTO PlayerItemRecord (PlayerItemId, Record) VALUES ($id, $record);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$record", record);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Removes items deleted on another machine, by cloud id.
    ///
    /// <b>This clears more tables than upstream's does.</b> Upstream deletes ComputedItemStat,
    /// ReplicaItem2 and PlayerItem, and leaves ReplicaItemRow and PlayerItemRecord behind. Those
    /// leftovers are not inert here: both parent tables use an INTEGER primary key, which SQLite
    /// makes a rowid alias and *reuses* after a delete, so the next item to arrive inherits a
    /// departed item's tooltip lines and records — wrong stats on the card and wrong matches in
    /// the filters. This port hit exactly that bug once already (see LootStore.Delete) and
    /// Schema.RemoveOrphanedRows sweeps up after it on every start.
    ///
    /// So the end state is the same as upstream's after a restart; deleting the rows here just
    /// closes the window in between. Nothing the server sees changes, and no item survives or
    /// disappears that would not have either way.
    /// </summary>
    public void Delete(IList<DeleteItemDto> items) {
        using var transaction = _connection.BeginTransaction();

        foreach (var item in items) {
            if (string.IsNullOrEmpty(item.Id)) continue;

            foreach (var sql in new[] {
                         "DELETE FROM ComputedItemStat WHERE playeritemid IN (SELECT Id FROM PlayerItem WHERE cloudid = $uuid)",
                         "DELETE FROM ReplicaItemRow WHERE replicaitemid IN (SELECT Id FROM ReplicaItem2 WHERE playeritemid IN (SELECT Id FROM PlayerItem WHERE cloudid = $uuid))",
                         "DELETE FROM ReplicaItem2 WHERE playeritemid IN (SELECT Id FROM PlayerItem WHERE cloudid = $uuid)",
                         "DELETE FROM PlayerItemRecord WHERE PlayerItemId IN (SELECT Id FROM PlayerItem WHERE cloudid = $uuid)",
                         "DELETE FROM PlayerItem WHERE cloudid = $uuid",
                     }) {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$uuid", item.Id);
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    public void ResetOnlineSyncState() {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE PlayerItem SET cloud_hassync = 0;";
        command.ExecuteNonQuery();
    }

    private static CloudItem Read(SqliteDataReader reader) {
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        long Number(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt64(i);

        return new CloudItem {
            Id = reader.GetInt64(0),
            CloudId = Text(1),
            IsCloudSynchronized = Number(2) != 0,
            BaseRecord = Text(3),
            PrefixRecord = Text(4),
            SuffixRecord = Text(5),
            ModifierRecord = Text(6),
            TransmuteRecord = Text(7),
            MateriaRecord = Text(8),
            RelicCompletionBonusRecord = Text(9),
            EnchantmentRecord = Text(10),
            AscendantAffixNameRecord = Text(11),
            AscendantAffix2hNameRecord = Text(12),
            Seed = Number(13),
            RelicSeed = Number(14),
            EnchantmentSeed = Number(15),
            MateriaCombines = Number(16),
            StackCount = Number(17),
            RerollsUsed = Number(18),
            AffixRerollsUsed = Number(19),
            CreationDate = reader.IsDBNull(20) ? null : reader.GetInt64(20),
            PrefixRarity = Number(21),
            Name = Text(22),
            NameLowercase = Text(23),
            Rarity = Text(24),
            LevelRequirement = reader.IsDBNull(25) ? 0 : reader.GetDouble(25),
            Mod = Text(26),
            IsHardcore = Number(27) != 0,
        };
    }

    public void Dispose() => _connection.Dispose();
}
