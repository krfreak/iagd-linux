using IAGrim.Cloud.Dto;
using IAGrim.Platform;
using Microsoft.Data.Sqlite;

namespace IAGrim.Cloud.Data;

/// <summary>
/// A friend whose collection is being followed. Upstream's <c>BuddySubscription</c>, stored in
/// the <c>BuddySubscription</c> table.
/// </summary>
public sealed class BuddySubscription {
    /// <summary>Their six-digit buddy id, which is also the primary key. Not a local row id.</summary>
    public long Id { get; set; }

    public string? Nickname { get; set; }

    /// <summary>Hidden buddies keep their items but drop out of search results.</summary>
    public bool IsHidden { get; set; }

    /// <summary>The high-water mark for this buddy alone. Each subscription syncs on its own clock.</summary>
    public long LastSyncTimestamp { get; set; }
}

/// <summary>
/// One item in a friend's collection — upstream's <c>BuddyItem</c>, in <c>buddyitems_v6</c>.
///
/// Keyed by (remote item id, buddy id), so the same item shared by two people is two rows. It is
/// deliberately a *different* type from <see cref="CloudItem"/>: a buddy item is read-only, has
/// no cloud sync state of its own, and carries fewer columns — notably no enchantment record and
/// no ascendant affixes, because upstream's table has no place to put them.
/// </summary>
public sealed class BuddyItem {
    public string? RemoteItemId { get; set; }
    public long BuddyId { get; set; }

    public string? BaseRecord { get; set; }
    public string? PrefixRecord { get; set; }
    public string? SuffixRecord { get; set; }
    public string? ModifierRecord { get; set; }
    public string? TransmuteRecord { get; set; }
    public string? MateriaRecord { get; set; }

    public long StackCount { get; set; }
    public bool IsHardcore { get; set; }
    public string? Mod { get; set; }
    public string? Name { get; set; }
    public string? NameLowercase { get; set; }
    public double MinimumLevel { get; set; }
    public long CreationDate { get; set; }
    public string? Rarity { get; set; }
    public long PrefixRarity { get; set; }
    public long Seed { get; set; }
    public long RelicSeed { get; set; }
    public long EnchantmentSeed { get; set; }
    public long RerollsUsed { get; set; }
    public long AffixRerollsUsed { get; set; }
}

/// <summary>
/// Buddy subscriptions and their items — upstream's <c>BuddySubscriptionDaoImpl</c> and the
/// sync half of <c>BuddyItemDaoImpl</c>.
///
/// Three tables, all upstream's: <c>BuddySubscription</c> (who is followed),
/// <c>buddyitems_v6</c> (their items) and <c>BuddyItemRecord_v2</c> (the record lookup the
/// filters drive off, the buddy-side twin of <c>PlayerItemRecord</c>).
///
/// Nothing here writes to <c>PlayerItem</c>. A buddy's item is never the player's item, and the
/// only way one crosses over is the player asking for it — which is a transfer, not a sync.
/// </summary>
public sealed class BuddyStore : IDisposable {
    private readonly SqliteConnection _connection;

    public BuddyStore(string databasePath) {
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        Schema.Apply(_connection);
    }

    // ------------------------------------------------------------------ subscriptions

    public IList<BuddySubscription> ListSubscriptions() {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Nickname, IFNULL(IsHidden, 0), IFNULL(LastSyncTimestamp, 0) FROM BuddySubscription ORDER BY Id;";

        var subscriptions = new List<BuddySubscription>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            subscriptions.Add(new BuddySubscription {
                Id = reader.GetInt64(0),
                Nickname = reader.IsDBNull(1) ? null : reader.GetString(1),
                IsHidden = reader.GetInt64(2) != 0,
                LastSyncTimestamp = reader.GetInt64(3),
            });
        }
        return subscriptions;
    }

    public BuddySubscription? GetSubscription(long id) =>
        ListSubscriptions().FirstOrDefault(subscription => subscription.Id == id);

    /// <summary>
    /// Adds or updates a subscription. <see cref="BuddySubscription.LastSyncTimestamp"/> is
    /// written as given, so an existing buddy keeps its position rather than re-downloading
    /// their whole collection every time their nickname is edited.
    /// </summary>
    public void SaveOrUpdate(BuddySubscription subscription) {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO BuddySubscription (Id, Nickname, IsHidden, LastSyncTimestamp)
            VALUES ($id, $nickname, $hidden, $timestamp)
            ON CONFLICT(Id) DO UPDATE SET
                Nickname = excluded.Nickname,
                IsHidden = excluded.IsHidden,
                LastSyncTimestamp = excluded.LastSyncTimestamp;
            """;
        command.Parameters.AddWithValue("$id", subscription.Id);
        command.Parameters.AddWithValue("$nickname", (object?)subscription.Nickname ?? DBNull.Value);
        command.Parameters.AddWithValue("$hidden", subscription.IsHidden ? 1 : 0);
        command.Parameters.AddWithValue("$timestamp", subscription.LastSyncTimestamp);
        command.ExecuteNonQuery();
    }

    public long GetNumItems(long subscriptionId) {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM buddyitems_v6 WHERE id_buddy = $id;";
        command.Parameters.AddWithValue("$id", subscriptionId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// Unsubscribes: the buddy, their items, and everything keyed to those items.
    ///
    /// The orphan sweeps are upstream's own — it clears <c>BuddyItemRecord_v2</c> and the
    /// replica rows for items no longer present, because both are keyed by the remote item id
    /// rather than by the buddy.
    /// </summary>
    public void RemoveBuddy(long buddyId) {
        using var transaction = _connection.BeginTransaction();

        Execute(transaction, "DELETE FROM buddyitems_v6 WHERE id_buddy = $id", ("$id", buddyId));
        Execute(transaction, "DELETE FROM BuddySubscription WHERE Id = $id", ("$id", buddyId));
        SweepOrphans(transaction);

        transaction.Commit();
    }

    /// <summary>Forgets every buddy. Upstream does this on logout — the items are not the user's.</summary>
    public void DeleteAll() {
        using var transaction = _connection.BeginTransaction();

        Execute(transaction, "DELETE FROM buddyitems_v6");
        Execute(transaction, "DELETE FROM BuddySubscription");
        Execute(transaction, "DELETE FROM BuddyItemRecord_v2");
        Execute(transaction, "DELETE FROM ReplicaItemRow WHERE replicaitemid IN (SELECT Id FROM ReplicaItem2 WHERE playeritemid IS NULL AND buddyitemid IS NOT NULL)");
        Execute(transaction, "DELETE FROM ReplicaItem2 WHERE playeritemid IS NULL AND buddyitemid IS NOT NULL");

        transaction.Commit();
    }

    // ------------------------------------------------------------------------- items

    /// <summary>Which of this buddy's items are already held, so a sync can skip them.</summary>
    public IList<string> GetOnlineIds(BuddySubscription subscription) {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id_item_remote FROM buddyitems_v6 WHERE id_buddy = $id;";
        command.Parameters.AddWithValue("$id", subscription.Id);

        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            if (!reader.IsDBNull(0)) ids.Add(reader.GetString(0));
        }
        return ids;
    }

    /// <summary>
    /// Stores a batch of a buddy's items, and the record lookup rows the filters need.
    ///
    /// <c>INSERT OR IGNORE</c> on the item as well as the records: the primary key is (remote
    /// item id, buddy id), so a re-sync of a window already seen is a no-op rather than a
    /// constraint violation that aborts the whole batch.
    /// </summary>
    public void Save(BuddySubscription subscription, IList<BuddyItem> items) {
        using var transaction = _connection.BeginTransaction();

        foreach (var item in items) {
            item.BuddyId = subscription.Id;
            Insert(item, transaction);

            // Upstream indexes four of the records here, not the full six it uses for player
            // items: base, prefix, suffix and materia. The two ascendant affixes are absent
            // because ToBuddyItem never fills them in.
            foreach (var record in new[] { item.BaseRecord, item.PrefixRecord, item.SuffixRecord, item.MateriaRecord }) {
                if (string.IsNullOrEmpty(record)) continue;

                Execute(transaction,
                    "INSERT OR IGNORE INTO BuddyItemRecord_v2 (id_item, record) VALUES ($id, $record)",
                    ("$id", item.RemoteItemId!), ("$record", record));
            }
        }

        transaction.Commit();
    }

    private void Insert(BuddyItem item, SqliteTransaction transaction) {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO buddyitems_v6 (
                id_item_remote, id_buddy, baserecord, prefixrecord, suffixrecord, modifierrecord,
                transmuterecord, materiarecord, stackcount, ishardcore, mod, name, namelowercase,
                levelrequirement, created_at, rarity, prefixrarity, seed, relicseed,
                enchantmentseed, RerollsUsed, AffixRerollsUsed
            ) VALUES (
                $remoteId, $buddyId, $base, $prefix, $suffix, $modifier,
                $transmute, $materia, $stack, $hardcore, $mod, $name, $nameLower,
                $level, $created, $rarity, $prefixRarity, $seed, $relicSeed,
                $enchantmentSeed, $rerolls, $affixRerolls
            );
            """;

        command.Parameters.AddWithValue("$remoteId", (object?)item.RemoteItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("$buddyId", item.BuddyId);
        command.Parameters.AddWithValue("$base", (object?)item.BaseRecord ?? DBNull.Value);
        command.Parameters.AddWithValue("$prefix", (object?)item.PrefixRecord ?? DBNull.Value);
        command.Parameters.AddWithValue("$suffix", (object?)item.SuffixRecord ?? DBNull.Value);
        command.Parameters.AddWithValue("$modifier", (object?)item.ModifierRecord ?? DBNull.Value);
        command.Parameters.AddWithValue("$transmute", (object?)item.TransmuteRecord ?? DBNull.Value);
        command.Parameters.AddWithValue("$materia", (object?)item.MateriaRecord ?? DBNull.Value);
        command.Parameters.AddWithValue("$stack", item.StackCount);
        command.Parameters.AddWithValue("$hardcore", item.IsHardcore ? 1 : 0);
        command.Parameters.AddWithValue("$mod", (object?)item.Mod ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)item.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$nameLower", (object?)item.NameLowercase ?? DBNull.Value);
        command.Parameters.AddWithValue("$level", item.MinimumLevel);
        command.Parameters.AddWithValue("$created", item.CreationDate);
        command.Parameters.AddWithValue("$rarity", (object?)item.Rarity ?? DBNull.Value);
        command.Parameters.AddWithValue("$prefixRarity", item.PrefixRarity);
        command.Parameters.AddWithValue("$seed", item.Seed);
        command.Parameters.AddWithValue("$relicSeed", item.RelicSeed);
        command.Parameters.AddWithValue("$enchantmentSeed", item.EnchantmentSeed);
        command.Parameters.AddWithValue("$rerolls", item.RerollsUsed);
        command.Parameters.AddWithValue("$affixRerolls", item.AffixRerollsUsed);
        command.ExecuteNonQuery();
    }

    /// <summary>Removes items the buddy has transferred away, scoped to that buddy.</summary>
    public void Delete(BuddySubscription subscription, IList<DeleteItemDto> items) {
        using var transaction = _connection.BeginTransaction();

        foreach (var item in items) {
            if (string.IsNullOrEmpty(item.Id)) continue;

            Execute(transaction,
                "DELETE FROM buddyitems_v6 WHERE id_item_remote = $cloudId AND id_buddy = $buddyId",
                ("$cloudId", item.Id), ("$buddyId", subscription.Id));
        }

        SweepOrphans(transaction);
        transaction.Commit();
    }

    /// <summary>Buddy items whose name has not been resolved from the game data yet.</summary>
    public IList<BuddyItem> ListItemsWithMissingName() {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id_item_remote, id_buddy, baserecord, prefixrecord, suffixrecord, materiarecord,
                   IFNULL(mod, '')
            FROM buddyitems_v6
            WHERE name IS NULL OR name = '';
            """;

        var items = new List<BuddyItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            items.Add(new BuddyItem {
                RemoteItemId = reader.IsDBNull(0) ? null : reader.GetString(0),
                BuddyId = reader.GetInt64(1),
                BaseRecord = reader.IsDBNull(2) ? null : reader.GetString(2),
                PrefixRecord = reader.IsDBNull(3) ? null : reader.GetString(3),
                SuffixRecord = reader.IsDBNull(4) ? null : reader.GetString(4),
                MateriaRecord = reader.IsDBNull(5) ? null : reader.GetString(5),
                Mod = reader.GetString(6),
            });
        }
        return items;
    }

    /// <summary>
    /// Fills in the names of buddy items from the parsed game data.
    ///
    /// <b>The mechanism differs from upstream's, deliberately.</b> Upstream pivots
    /// <c>DatabaseItemStat_v2</c> for <c>itemNameTag</c>/<c>itemQualityTag</c>/
    /// <c>itemStyleTag</c>/<c>lootRandomizerName</c> and composes them through its language
    /// pack's <c>TranslateName</c>. This port names every item it shows out of
    /// <c>ItemTemplate</c>, which is the same game data already denormalised at parse time, and
    /// using anything else here would mean a buddy's copy of an item is captioned differently
    /// from the player's own copy of the same item in the same list.
    ///
    /// Returns how many names were resolved. Items whose record has not been parsed yet keep a
    /// null name and are retried on the next pass.
    /// </summary>
    public int UpdateNames(IList<BuddyItem> items) {
        if (items.Count == 0) return 0;

        var updated = 0;
        using var transaction = _connection.BeginTransaction();

        foreach (var item in items) {
            if (string.IsNullOrEmpty(item.BaseRecord) || string.IsNullOrEmpty(item.RemoteItemId)) continue;

            string? name;
            using (var lookup = _connection.CreateCommand()) {
                lookup.Transaction = transaction;
                // A mod's template wins over the vanilla one for the same record, matching how
                // CollectionService resolves a name for the player's own items.
                lookup.CommandText = """
                    SELECT Name FROM ItemTemplate
                    WHERE Record = $record AND Mod IN ($mod, '')
                    ORDER BY CASE WHEN Mod = $mod THEN 0 ELSE 1 END
                    LIMIT 1;
                    """;
                lookup.Parameters.AddWithValue("$record", item.BaseRecord);
                lookup.Parameters.AddWithValue("$mod", item.Mod ?? "");
                name = lookup.ExecuteScalar() as string;
            }

            if (string.IsNullOrEmpty(name)) continue;

            Execute(transaction,
                "UPDATE buddyitems_v6 SET name = $name, namelowercase = $lower WHERE id_item_remote = $id AND id_buddy = $buddy",
                ("$name", name), ("$lower", name.ToLowerInvariant()),
                ("$id", item.RemoteItemId!), ("$buddy", item.BuddyId));
            updated++;
        }

        transaction.Commit();
        return updated;
    }

    /// <summary>
    /// Clears rows keyed to buddy items that no longer exist. Upstream runs exactly these three
    /// after every buddy delete, because all of them key on the remote item id rather than on the
    /// buddy, and neither table has a foreign key.
    /// </summary>
    private void SweepOrphans(SqliteTransaction transaction) {
        Execute(transaction,
            "DELETE FROM BuddyItemRecord_v2 WHERE id_item NOT IN (SELECT id_item_remote FROM buddyitems_v6)");
        Execute(transaction,
            "DELETE FROM ReplicaItemRow WHERE replicaitemid IN (SELECT Id FROM ReplicaItem2 WHERE playeritemid IS NULL AND buddyitemid NOT IN (SELECT id_item_remote FROM buddyitems_v6))");
        Execute(transaction,
            "DELETE FROM ReplicaItem2 WHERE playeritemid IS NULL AND buddyitemid NOT IN (SELECT id_item_remote FROM buddyitems_v6)");
    }

    private void Execute(SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters) {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
