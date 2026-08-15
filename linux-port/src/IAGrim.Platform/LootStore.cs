using Microsoft.Data.Sqlite;

namespace IAGrim.Platform;

/// <summary>
/// Persists looted items to SQLite, in upstream's own schema.
///
/// Deliberately not NHibernate: see PORTING.md. The tables are upstream's though, down to the
/// column names, so a userdata.db from a Windows install opens here directly — that is what
/// <see cref="Schema"/> guarantees, and it is why this file talks about `Id` and `created_at`
/// rather than nicer names of its own.
/// </summary>
public sealed class LootStore : IDisposable {
    private readonly SqliteConnection _connection;

    public LootStore(string databasePath) {
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();

        // Loot arrives one file at a time from a directory poll; WAL keeps a reader (the UI)
        // from blocking the writer.
        Schema.Apply(_connection);
    }

    /// <summary>
    /// An item is identified by base record + seed, which is how Grim Dawn itself identifies
    /// a rolled item. Re-importing the same loot file must not duplicate it.
    /// </summary>
    public bool Exists(LootedItem item) {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM PlayerItem WHERE BaseRecord = $base AND Seed = $seed LIMIT 1;";
        command.Parameters.AddWithValue("$base", item.BaseRecord);
        command.Parameters.AddWithValue("$seed", item.Seed);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// An item's optional record, in upstream's shape: the empty string, never NULL.
    ///
    /// Upstream's stash parser initialises every record field to "" and copies it through
    /// unchanged (Parser/Stash/Item.cs, TransferStashService.Map), so its rows have no NULLs
    /// here — and its SQL relies on that. The Components filter ends in
    /// <c>MateriaRecord = ''</c>, which a NULL fails, so storing NULL made that filter match
    /// nothing at all. <see cref="Schema"/> converts collections written before this.
    /// </summary>
    private static object Record(string? value) => value ?? "";

    public long Insert(LootedItem item) {
        using var transaction = _connection.BeginTransaction();

        long id;
        using (var command = _connection.CreateCommand()) {
            command.Transaction = transaction;
            // Upstream's column names. `namelowercase` is stored rather than computed because
            // upstream's wildcard search compares against it directly.
            command.CommandText = """
                INSERT INTO PlayerItem (
                    Mod, IsHardcore, baserecord, PrefixRecord, SuffixRecord, Seed, RerollsUsed,
                    ModifierRecord, MateriaRecord, RelicCompletionBonusRecord, RelicSeed,
                    EnchantmentRecord, EnchantmentSeed, TransmuteRecord,
                    AscendantAffixNameRecord, AscendantAffix2hNameRecord, AffixRerollsUsed,
                    StackCount, Name, namelowercase, created_at, cloudid, cloud_hassync
                ) VALUES (
                    $mod, $hc, $base, $prefix, $suffix, $seed, $rerolls,
                    $modifier, $materia, $relicBonus, $relicSeed,
                    $enchantment, $enchantmentSeed, $transmute,
                    $asc1, $asc2, $affixRerolls,
                    $stack, $name, $nameLower, $created, $cloudId, 0
                );
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$mod", (object?)item.Mod ?? DBNull.Value);
            command.Parameters.AddWithValue("$hc", item.IsHardcore ? 1 : 0);
            command.Parameters.AddWithValue("$base", item.BaseRecord);
            command.Parameters.AddWithValue("$prefix", Record(item.PrefixRecord));
            command.Parameters.AddWithValue("$suffix", Record(item.SuffixRecord));
            command.Parameters.AddWithValue("$seed", item.Seed);
            command.Parameters.AddWithValue("$rerolls", item.RerollsUsed);
            command.Parameters.AddWithValue("$modifier", Record(item.ModifierRecord));
            command.Parameters.AddWithValue("$materia", Record(item.MateriaRecord));
            command.Parameters.AddWithValue("$relicBonus", Record(item.RelicCompletionBonusRecord));
            command.Parameters.AddWithValue("$relicSeed", item.RelicSeed);
            command.Parameters.AddWithValue("$enchantment", Record(item.EnchantmentRecord));
            command.Parameters.AddWithValue("$enchantmentSeed", item.EnchantmentSeed);
            command.Parameters.AddWithValue("$transmute", Record(item.TransmuteRecord));
            command.Parameters.AddWithValue("$asc1", Record(item.AscendantAffixNameRecord));
            command.Parameters.AddWithValue("$asc2", Record(item.AscendantAffix2hNameRecord));
            command.Parameters.AddWithValue("$affixRerolls", item.AffixRerollsUsed);
            command.Parameters.AddWithValue("$stack", Math.Max(1, item.StackCount));
            command.Parameters.AddWithValue("$name", (object?)item.PlainName ?? DBNull.Value);
            command.Parameters.AddWithValue("$nameLower",
                (object?)item.PlainName?.ToLowerInvariant() ?? DBNull.Value);
            // Milliseconds: upstream's created_at is written by DateTime.ToTimestamp(), which
            // returns TotalMilliseconds. Seconds here would read as 1970 in the Windows tool and
            // would put every item inside the "recent" window.
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            // The cloud identity is assigned here rather than at upload time, and regardless of
            // whether online sync is switched on. See CloudIdentity: an item that reaches another
            // machine before it has a stable id arrives twice. `cloud_hassync` starts at 0, which
            // is what marks it as still to upload.
            command.Parameters.AddWithValue("$cloudId", CloudIdentity.New());

            id = (long)command.ExecuteScalar()!;
        }

        // The records this item is made of. Upstream keeps these in their own table because the
        // pet-bonus and damage-type filters drive off it — they join owned records to the game
        // database rather than scanning every item. See PlayerItemDaoImpl.RecordStatSubquery.
        InsertRecords(id, item.Records(), transaction);

        // Tooltip lines, in upstream's two-table shape: one ReplicaItem2 per item, its lines in
        // ReplicaItemRow. Upstream has no ordinal column and relies on insertion order.
        if (item.Stats.Count > 0) {
            using (var command = _connection.CreateCommand()) {
                command.Transaction = transaction;
                command.CommandText =
                    "INSERT INTO ReplicaItem2 (playeritemid) VALUES ($item); SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("$item", id);
                var replicaId = (long)command.ExecuteScalar()!;

                foreach (var stat in item.Stats) {
                    using var row = _connection.CreateCommand();
                    row.Transaction = transaction;
                    row.CommandText =
                        "INSERT INTO ReplicaItemRow (replicaitemid, Type, Text, TextLowercase) " +
                        "VALUES ($replica, $type, $text, $lower);";
                    row.Parameters.AddWithValue("$replica", replicaId);
                    row.Parameters.AddWithValue("$type", stat.TextClass);
                    row.Parameters.AddWithValue("$text", stat.Text);
                    row.Parameters.AddWithValue("$lower", stat.Text.ToLowerInvariant());
                    row.ExecuteNonQuery();
                }
            }
        }

        transaction.Commit();
        return id;
    }

    /// <summary>
    /// Records an item is composed of. Pet-bonus targets are added later, by the precompute
    /// pass, because resolving them needs the game database.
    /// </summary>
    private void InsertRecords(long id, IEnumerable<string> records, SqliteTransaction? transaction = null) {
        foreach (var record in records) {
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
    /// Items with no tooltip yet — the ones that came from a file rather than from the hook.
    ///
    /// **Equippable gear and relics only**, which is upstream's restriction in
    /// <c>ListMissingReplica</c>: the class list is the same, and it exists because asking the
    /// game to render a potion or a crafting material spends a round trip on something that has
    /// no tooltip worth showing. A collection merged from elsewhere is mostly such items, so
    /// without this most requests are wasted.
    ///
    /// Ordered newest first so a freshly imported stash fills in while the player is looking at
    /// it, rather than starting from items imported months ago. (Upstream orders randomly; that
    /// spreads the work evenly but means the items on screen are the last to arrive.)
    /// </summary>
    public IReadOnlyList<ReplicaRequestItem> ItemsMissingReplica(int limit) {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT Id, IFNULL(Mod,''), baserecord, PrefixRecord, SuffixRecord, ModifierRecord,
                   MateriaRecord, EnchantmentRecord, TransmuteRecord,
                   AscendantAffixNameRecord, AscendantAffix2hNameRecord,
                   IFNULL(Seed,0), IFNULL(RelicSeed,0), IFNULL(EnchantmentSeed,0),
                   IFNULL(RerollsUsed,0)
            FROM PlayerItem p
            WHERE NOT EXISTS (SELECT 1 FROM ReplicaItem2 r WHERE r.playeritemid = p.Id)
              AND EXISTS (
                    SELECT 1 FROM DatabaseItem_v2 db
                     JOIN DatabaseItemStat_v2 dbs ON dbs.id_databaseitem = db.id_databaseitem
                    WHERE db.baserecord = p.baserecord
                      AND dbs.Stat = 'Class'
                      AND dbs.TextValue IN (
                        'ArmorProtective_Head', 'ArmorProtective_Hands', 'ArmorProtective_Feet',
                        'ArmorProtective_Legs', 'ArmorProtective_Chest', 'ArmorProtective_Waist',
                        'ArmorJewelry_Medal', 'ArmorJewelry_Ring', 'ArmorProtective_Shoulders',
                        'ArmorJewelry_Amulet',
                        'WeaponMelee_Dagger', 'WeaponMelee_Mace', 'WeaponMelee_Axe',
                        'WeaponMelee_Scepter', 'WeaponMelee_Sword', 'WeaponMelee_Spear2h',
                        'WeaponMelee_Sword2h', 'WeaponMelee_Mace2h', 'WeaponMelee_Axe2h',
                        'WeaponHunting_Ranged1h', 'WeaponHunting_Ranged2h',
                        'WeaponArmor_Offhand', 'WeaponArmor_Shield', 'ItemArtifact'))
            ORDER BY Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var items = new List<ReplicaRequestItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            items.Add(new ReplicaRequestItem(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                Text(3), Text(4), Text(5), Text(6), Text(7), Text(8), Text(9), Text(10),
                reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14)));
        }
        return items;
    }

    /// <summary>
    /// Attaches a tooltip the game rendered. Returns false when the item has since been
    /// transferred away, which is normal rather than an error.
    /// </summary>
    public bool AttachReplica(long id, IReadOnlyList<LootStat> stats) {
        using var transaction = _connection.BeginTransaction();

        using (var exists = _connection.CreateCommand()) {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM PlayerItem WHERE Id = $id;";
            exists.Parameters.AddWithValue("$id", id);
            if (exists.ExecuteScalar() is null) return false;
        }

        // Replace rather than append: a second answer for the same item is a re-request, not a
        // second tooltip, and ReplicaItem2.playeritemid is UNIQUE.
        using (var clear = _connection.CreateCommand()) {
            clear.Transaction = transaction;
            clear.CommandText = """
                DELETE FROM ReplicaItemRow WHERE replicaitemid IN
                    (SELECT Id FROM ReplicaItem2 WHERE playeritemid = $id);
                DELETE FROM ReplicaItem2 WHERE playeritemid = $id;
                """;
            clear.Parameters.AddWithValue("$id", id);
            clear.ExecuteNonQuery();
        }

        long replicaId;
        using (var insert = _connection.CreateCommand()) {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO ReplicaItem2 (playeritemid) VALUES ($id); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$id", id);
            replicaId = (long)insert.ExecuteScalar()!;
        }

        foreach (var stat in stats) {
            using var row = _connection.CreateCommand();
            row.Transaction = transaction;
            row.CommandText =
                "INSERT INTO ReplicaItemRow (replicaitemid, Type, Text, TextLowercase) " +
                "VALUES ($replica, $type, $text, $lower);";
            row.Parameters.AddWithValue("$replica", replicaId);
            row.Parameters.AddWithValue("$type", stat.TextClass);
            row.Parameters.AddWithValue("$text", stat.Text);
            row.Parameters.AddWithValue("$lower", stat.Text.ToLowerInvariant());
            row.ExecuteNonQuery();
        }

        // The name column is what the collection falls back to; now that the game has rendered
        // one, use it.
        var name = stats.FirstOrDefault(s => s.TextClass == 6)?.Text ?? stats.FirstOrDefault()?.Text;
        if (name is not null) {
            using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE PlayerItem SET Name = $name, namelowercase = $lower WHERE Id = $id;";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$name", LootedItem.StripColourCodes(name));
            update.Parameters.AddWithValue("$lower", LootedItem.StripColourCodes(name).ToLowerInvariant());
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    public int CountItems() {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PlayerItem;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Items the precompute pass has not seen. Rarity is the marker because it is written for
    /// every item, including ones whose stat roll was skipped.
    /// </summary>
    public int CountItemsNeedingStats() {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PlayerItem WHERE Rarity IS NULL;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IEnumerable<(long Id, string? Name, string BaseRecord, long Seed)> ListItems() {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Name, baserecord, Seed FROM PlayerItem ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var results = new List<(long, string?, string, long)>();
        while (reader.Read()) {
            results.Add((reader.GetInt64(0),
                         reader.IsDBNull(1) ? null : reader.GetString(1),
                         reader.GetString(2),
                         reader.GetInt64(3)));
        }
        return results;
    }

    /// <summary>
    /// Rebuilds a stored item so it can be sent back to the game. Stats are not included:
    /// they are display data the game regenerates from the record and seed.
    /// </summary>
    public LootedItem? GetById(long id) {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT Mod, IsHardcore, BaseRecord, PrefixRecord, SuffixRecord, Seed, RerollsUsed,
                   ModifierRecord, MateriaRecord, RelicCompletionBonusRecord, RelicSeed,
                   EnchantmentRecord, EnchantmentSeed, TransmuteRecord,
                   AscendantAffixNameRecord, AscendantAffix2hNameRecord, AffixRerollsUsed, Name,
                   StackCount
            FROM PlayerItem WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

        return new LootedItem {
            Mod                        = Text(0) ?? "",
            IsHardcore                 = reader.GetInt64(1) != 0,
            BaseRecord                 = reader.GetString(2),
            PrefixRecord               = Text(3),
            SuffixRecord               = Text(4),
            Seed                       = reader.GetInt64(5),
            RerollsUsed                = reader.GetInt64(6),
            ModifierRecord             = Text(7),
            MateriaRecord              = Text(8),
            RelicCompletionBonusRecord = Text(9),
            RelicSeed                  = reader.GetInt64(10),
            EnchantmentRecord          = Text(11),
            EnchantmentSeed            = reader.GetInt64(12),
            TransmuteRecord            = Text(13),
            AscendantAffixNameRecord   = Text(14),
            AscendantAffix2hNameRecord = Text(15),
            AffixRerollsUsed           = reader.GetInt64(16),
            StackCount                 = reader.IsDBNull(18) ? 1 : reader.GetInt64(18),
            // Carried so the caller can name the item in messages; the game rebuilds the rest.
            Stats                      = Text(17) is { } name ? [new LootStat(6, name)] : [],
        };
    }

    /// <summary>
    /// Removes an item once the game has taken it. Only call this after the hook has
    /// collected the transfer file — deleting earlier loses the item, deleting never
    /// duplicates it.
    /// </summary>
    public bool Delete(long id) {
        using var transaction = _connection.BeginTransaction();

        // Before anything else: if the backup service knows this item, record that it is gone.
        // The tombstone is what stops the user's other machine uploading it straight back after
        // the game has taken it -- without it, an item transferred into the stash here reappears
        // in the collection minutes later and can be transferred a second time.
        CloudTombstone.Mark(_connection, id, transaction);

        // Every table that keys off the item, deleted explicitly.
        //
        // Upstream's schema declares no cascades — the port's own earlier schema did, and
        // adopting upstream's silently removed them. The result is not merely dead rows:
        // PlayerItem.Id is a rowid alias, so SQLite reuses the highest id after a delete, and
        // the *next looted item* would then collide with the leftovers. ReplicaItem2.playeritemid
        // is UNIQUE, so the collision is a hard failure — transfer an item out, loot anything,
        // and the import throws. Verified before fixing.
        //
        // ComputedItemStat is the quieter half of the same bug: the reused id would inherit the
        // departed item's rolled values, so a new item would show another item's stats.
        foreach (var sql in new[] {
                     "DELETE FROM ReplicaItemRow WHERE replicaitemid IN " +
                     "(SELECT Id FROM ReplicaItem2 WHERE playeritemid = $id)",
                     "DELETE FROM ReplicaItem2 WHERE playeritemid = $id",
                     "DELETE FROM PlayerItemRecord WHERE PlayerItemId = $id",
                     "DELETE FROM ComputedItemStat WHERE playeritemid = $id",
                 }) {
            using var cleanup = _connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = sql;
            cleanup.Parameters.AddWithValue("$id", id);
            cleanup.ExecuteNonQuery();
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM PlayerItem WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var deleted = command.ExecuteNonQuery() > 0;

        transaction.Commit();
        return deleted;
    }

    private void Execute(string sql) {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
