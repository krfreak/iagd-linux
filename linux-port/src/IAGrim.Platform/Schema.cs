using Microsoft.Data.Sqlite;

namespace IAGrim.Platform;

/// <summary>
/// The database schema — upstream's, verbatim.
///
/// This port reads and writes the *same file layout* as the Windows tool, so a `userdata.db`
/// copied from an existing IAGD install opens here with its collection intact, and nothing has
/// to be exported or converted. That is the reason the DDL below is upstream's wording rather
/// than a tidier equivalent: `Id` not `Id_PlayerItem`, `created_at` not `CreationDate`,
/// `ReplicaItemRow` rather than a flat stat table. Where the two disagree, upstream wins.
///
/// Upstream builds this in three places — <c>AddBaseTables</c> for the tables and indices,
/// <c>AddAsterkarnFieldsToPlayerItem</c> for the expansion columns, and
/// <c>HbmSchemaMigration</c>, which walks the .hbm.xml mappings and ALTERs in any column a
/// mapping declares but the CREATE lacks (that is where <c>AffixRerollsUsed</c> comes from).
/// All three are collapsed here, and `scripts/verify-schema.sh` compares the result column for
/// column against upstream's sources.
///
/// Tables upstream owns but this port does not use yet (buddy sharing, cloud sync, deleted-item
/// tombstones) are still created, so a database this port writes stays loadable by the Windows
/// tool rather than being a one-way export.
/// </summary>
public static class Schema {
    /// <summary>
    /// Upstream's <c>AddBaseTables._tables</c>, copied. Do not reformat: this is compared
    /// against upstream's source mechanically.
    /// </summary>
    private static readonly (string Table, string Ddl)[] Tables = [
        ("deletedplayeritem_v3", "CREATE TABLE deletedplayeritem_v3 (id TEXT not null, primary key (id))"),
        ("PlayerItemRecord", "CREATE TABLE PlayerItemRecord (PlayerItemId INTEGER not null, Record TEXT not null, primary key (PlayerItemId, Record))"),
        ("itemskill_v2", "CREATE TABLE itemskill_v2 (id_skill  integer primary key autoincrement, Description TEXT, Name TEXT, Record TEXT, Trigger TEXT, Level INTEGER, id_databaseitem INTEGER)"),
        ("itemskill_mapping", "CREATE TABLE itemskill_mapping (id_skill INTEGER not null, id_databaseitem INTEGER not null, primary key (id_skill, id_databaseitem))"),
        ("buddyitems_v6", "CREATE TABLE buddyitems_v6 (id_item_remote TEXT not null, id_buddy INTEGER not null, baserecord TEXT, prefixrecord TEXT, suffixrecord TEXT, modifierrecord TEXT, transmuterecord TEXT, materiarecord TEXT, stackcount INTEGER, ishardcore INTEGER, mod TEXT, name TEXT, namelowercase TEXT, levelrequirement REAL, created_at INTEGER, rarity TEXT, prefixrarity INTEGER, seed INTEGER, relicseed INTEGER, enchantmentseed INTEGER, AscendantAffixNameRecord TEXT, AscendantAffix2hNameRecord TEXT, RerollsUsed INTEGER, primary key (id_item_remote, id_buddy))"),
        ("BuddyItemRecord_v2", "CREATE TABLE BuddyItemRecord_v2 (id_item TEXT not null, record TEXT not null, primary key (id_item, record))"),
        ("ReplicaItem2", "CREATE TABLE ReplicaItem2 (Id INTEGER not null, playeritemid INTEGER unique, buddyitemid TEXT unique, primary key (Id))"),
        ("ReplicaItemRow", "CREATE TABLE ReplicaItemRow (Id INTEGER not null, replicaitemid INTEGER, Type INTEGER, Text TEXT, TextLowercase TEXT, primary key (Id))"),
        ("ComputedItemStat", "CREATE TABLE ComputedItemStat (Id INTEGER not null, playeritemid INTEGER, stat TEXT, value REAL, primary key (Id))"),
        ("settings", "CREATE TABLE settings (setting TEXT not null, val1 INTEGER, V2 TEXT, primary key (setting))"),
        ("BuddySubscription", "CREATE TABLE BuddySubscription (Id INTEGER not null, Nickname TEXT, LastSyncTimestamp INTEGER, IsHidden INTEGER, primary key (Id))"),
        ("DatabaseItemStat_v2", "CREATE TABLE DatabaseItemStat_v2 (id_databaseitemstat  integer primary key autoincrement, id_databaseitem INTEGER, Stat TEXT, TextValue TEXT, val1 REAL, constraint FK_95F02CAE foreign key (id_databaseitem) references DatabaseItem_v2)"),
        ("DatabaseItem_v2", "CREATE TABLE DatabaseItem_v2 (id_databaseitem INTEGER not null, baserecord TEXT unique, name TEXT, hash INTEGER, namelowercase TEXT, primary key (id_databaseitem))"),
        ("ItemTag", "CREATE TABLE ItemTag (Tag TEXT not null, Name TEXT, primary key (Tag))"),
        ("PlayerItem", """
            CREATE TABLE "PlayerItem" (
            	"Id"	INTEGER NOT NULL,
            	"baserecord"	TEXT,
            	"PrefixRecord"	TEXT,
            	"SuffixRecord"	TEXT,
            	"ModifierRecord"	TEXT,
            	"TransmuteRecord"	TEXT,
            	"Seed"	INTEGER,
            	"MateriaRecord"	TEXT,
            	"RelicCompletionBonusRecord"	NUMERIC,
            	"RelicSeed"	INTEGER,
            	"EnchantmentRecord"	TEXT,
            	"PrefixRarity"	INTEGER,
            	"UNKNOWN"	INTEGER,
            	"EnchantmentSeed"	INTEGER,
            	"MateriaCombines"	INTEGER,
            	"StackCount"	INTEGER,
            	"Name"	TEXT,
            	"namelowercase"	TEXT,
            	"Rarity"	TEXT,
            	"LevelRequirement"	REAL,
            	"Mod"	TEXT,
            	"IsHardcore"	INTEGER,
            	"cloudid"	TEXT,
            	"cloud_hassync"	INTEGER,
            	"created_at"	INTEGER, AscendantAffixNameRecord TEXT, AscendantAffix2hNameRecord TEXT, RerollsUsed INT,
            	PRIMARY KEY("Id")
            )
            """),
    ];

    /// <summary>Upstream's <c>AddBaseTables._indices</c>, copied.</summary>
    private static readonly string[] Indices = [
        "CREATE INDEX idx_databaseitemstatv2_parent_stat on DatabaseItemStat_v2 (id_databaseitem)",
        "CREATE INDEX idx_databaseitemstatv2_stat on DatabaseItemStat_v2 (Stat)",
        "CREATE INDEX idx_databaseitemv2_record on DatabaseItem_v2 (baserecord)",
        "CREATE INDEX idx_playeritem_baserecord on PlayerItem (baserecord)",
        "CREATE INDEX idx_playeritem_levelreq on PlayerItem (LevelRequirement)",
        "CREATE INDEX idx_playeritem_lowercasename on PlayerItem (namelowercase)",
        "CREATE INDEX idx_playeritem_name on PlayerItem (name, Id)",
        "CREATE INDEX idx_playeritem_prefix on PlayerItem (PrefixRecord)",
        "CREATE INDEX idx_playeritem_rarity on PlayerItem (Rarity)",
        "CREATE INDEX idx_playeritem_suffix on PlayerItem (SuffixRecord)",
        "CREATE INDEX idx_replicaitem_buddyitemid on ReplicaItem2 (buddyitemid)",
        "CREATE INDEX idx_replicaitem_playeritemid on ReplicaItem2 (playeritemid)",
        "CREATE INDEX idx_replicaitemstat_replicaitemid on ReplicaItemRow (replicaitemid)",
        "CREATE INDEX idx_databaseitemv2_baserecord on DatabaseItem_v2 (baserecord)",
        "CREATE INDEX idx_playeritemrecord_record on PlayerItemRecord (record)",
        "CREATE INDEX idx_computeditemstat_playeritemid on ComputedItemStat (playeritemid)",
        "CREATE INDEX idx_computeditemstat_stat_value on ComputedItemStat (stat, value)",
    ];

    /// <summary>
    /// Columns upstream's .hbm.xml mappings declare but the CREATE statements omit — upstream
    /// adds these at startup via <c>HbmSchemaMigration</c>, so a live upstream database has
    /// them and ours must too.
    /// </summary>
    private static readonly (string Table, string Column, string Type)[] MappedColumns = [
        ("PlayerItem", "AffixRerollsUsed", "INTEGER"),
        // BuddyItem.hbm.xml declares this too, and AddBaseTables does not create it -- upstream's
        // buddyitems_v6 gets it the same way PlayerItem does. Missing here it was not merely a
        // dormant column: buddy sync inserts it by name, so every insert failed and a subscribed
        // buddy's collection silently stayed empty.
        ("buddyitems_v6", "AffixRerollsUsed", "INTEGER"),
    ];

    /// <summary>
    /// Tables this port adds. They are additive and upstream ignores them, which is what keeps
    /// the file readable by both.
    ///
    /// <c>ItemTemplate</c> is a denormalised view of what upstream pivots out of
    /// <c>DatabaseItemStat_v2</c> at query time (name, class, quality, icon, set, tier). It
    /// exists because this port serves a web UI that wants those fields per row, and because
    /// the icon filename has no upstream equivalent — upstream reads icons from its own
    /// extracted storage by a different convention.
    /// </summary>
    private static readonly (string Table, string Ddl)[] PortTables = [
        ("ItemTemplate", """
            CREATE TABLE ItemTemplate (
                Mod              TEXT NOT NULL DEFAULT '',
                Record           TEXT NOT NULL,
                Name             TEXT,
                NameTag          TEXT,
                Quality          TEXT,
                ItemClass        TEXT,
                Classification   TEXT,
                SetRecord        TEXT,
                SetName          TEXT,
                Bitmap           TEXT,
                IconFile         TEXT,
                LevelRequirement INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (Mod, Record)
            )
            """),
        ("GameDataMeta", """
            CREATE TABLE GameDataMeta (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            )
            """),
    ];

    private static readonly string[] PortIndices = [
        "CREATE INDEX IF NOT EXISTS IX_ItemTemplate_Name ON ItemTemplate(Name)",
        "CREATE INDEX IF NOT EXISTS IX_ItemTemplate_Record ON ItemTemplate(Record, Mod)",
        "CREATE INDEX IF NOT EXISTS IX_ItemTemplate_Class ON ItemTemplate(Classification)",
        "CREATE INDEX IF NOT EXISTS IX_ItemTemplate_SetRecord ON ItemTemplate(SetRecord)",
        "CREATE INDEX IF NOT EXISTS IX_ItemSkill_Record ON itemskill_v2(Record)",
    ];

    /// <summary>
    /// Brings a database up to date, creating what is missing and leaving what exists alone.
    /// Safe on an empty file, on a database this port wrote, and on one copied from an existing
    /// Windows install — in the last case every table already exists and this is a no-op.
    /// </summary>
    public static void Apply(SqliteConnection connection) {
        Execute(connection, "PRAGMA journal_mode=WAL;");

        // Wait for a busy database instead of failing instantly, which is SQLite's default.
        // Several writers exist by design here — the loot importer on its timer, the CLI, and a
        // merge started from the UI — and WAL lets them coexist only if they are willing to
        // queue. Without this, a merge running while loot arrives fails with "database is
        // locked" rather than taking its turn.
        Execute(connection, "PRAGMA busy_timeout=10000;");

        foreach (var (table, ddl) in Tables.Concat(PortTables)) {
            if (TableExists(connection, table)) continue;
            Execute(connection, ddl);
        }

        // Before the indices: one of them is over a column an older ItemTemplate does not have,
        // so creating indices first fails on exactly the databases the migration exists for.
        MigrateItemTemplateForMods(connection);

        foreach (var index in Indices) {
            // Upstream's index DDL has no IF NOT EXISTS; adding one would change the text this
            // is verified against, so the duplicate is swallowed instead.
            TryExecute(connection, index);
        }
        foreach (var index in PortIndices) {
            Execute(connection, index);
        }

        foreach (var (table, column, type) in MappedColumns) {
            AddColumn(connection, table, column, type);
        }

        MigrateFromPortSchema(connection);
        NormaliseToUpstreamValues(connection);
        RemoveOrphanedRows(connection);

        // Captured tooltips stored before this port normalised them the way upstream does.
        ReplicaService.NormaliseStoredRows(connection);
    }

    /// <summary>
    /// Rebuilds ItemTemplate when it predates mod support.
    ///
    /// The table was keyed by record alone, which two mods defining the same record would
    /// collide on. It is derived data — 'iagd parse' refills it in about 13 s — so it is
    /// recreated rather than migrated, and left empty until then.
    /// </summary>
    private static void MigrateItemTemplateForMods(SqliteConnection connection) {
        if (!TableExists(connection, "ItemTemplate") || ColumnExists(connection, "ItemTemplate", "Mod")) {
            return;
        }

        Execute(connection, "DROP TABLE ItemTemplate;");
        Execute(connection, PortTables.First(t => t.Table == "ItemTemplate").Ddl);
        foreach (var index in PortIndices) TryExecute(connection, index);
    }

    /// <summary>
    /// Rewrites values this port once stored in a shape upstream does not use.
    ///
    /// Having upstream's *columns* is not the same as having upstream's *values*, and its SQL
    /// depends on the values:
    ///
    ///   * optional records are the empty string there, never NULL — its stash parser starts
    ///     them at "" and copies them through. The Components filter ends in
    ///     <c>MateriaRecord = ''</c>, and NULL fails that test, so a collection written with
    ///     NULLs returns no components at all.
    ///   * <c>created_at</c> is milliseconds there — every write goes through
    ///     <c>DateTime.ToTimestamp()</c>, which returns TotalMilliseconds. Seconds read as
    ///     January 1970 in the Windows tool and sit inside every "recent" window.
    ///   * <c>LevelRequirement</c> is never NULL there — the property is a non-nullable double,
    ///     so an item that has not been analysed yet is level 0. This port left it unset, and
    ///     the search's default upper bound (<c>&lt;= 110</c>, upstream's own) drops a NULL:
    ///     items looted before Grim Dawn's data had been read were counted in the collection
    ///     and absent from the list, with no filter switched on to explain it.
    ///
    /// Each conversion is decided per row and is its own no-op the second time, so this can
    /// run on every start: a collection merged in from elsewhere is fixed the same way.
    /// </summary>
    private static void NormaliseToUpstreamValues(SqliteConnection connection) {
        if (!TableExists(connection, "PlayerItem")) return;

        string[] recordColumns = [
            "PrefixRecord", "SuffixRecord", "ModifierRecord", "MateriaRecord",
            "RelicCompletionBonusRecord", "EnchantmentRecord", "TransmuteRecord",
            "AscendantAffixNameRecord", "AscendantAffix2hNameRecord",
        ];

        foreach (var column in recordColumns) {
            if (!ColumnExists(connection, "PlayerItem", column)) continue;
            TryExecute(connection, $"UPDATE PlayerItem SET {column} = '' WHERE {column} IS NULL;");
        }

        // 1e11 ms is March 1973 and 1e11 s is the year 5138: no real timestamp is near the
        // boundary, so which unit a row is in can be read off its magnitude.
        TryExecute(connection,
            "UPDATE PlayerItem SET created_at = created_at * 1000 "
            + "WHERE created_at > 0 AND created_at < 100000000000;");

        // Level 0 is what an undescribed item is worth to every query that reads this column,
        // and unlike NULL it survives the level filter. The analysis pass overwrites it with the
        // real number as soon as there is game data to compute one from; until then the item is
        // at least visible, which is the difference this repairs.
        TryExecute(connection,
            "UPDATE PlayerItem SET LevelRequirement = 0 WHERE LevelRequirement IS NULL;");
    }

    /// <summary>
    /// Deletes rows left behind by items that no longer exist.
    ///
    /// An earlier version of this port deleted a transferred item without clearing the tables
    /// keyed to it. Because PlayerItem.Id is a rowid alias, SQLite hands the freed id to the
    /// next looted item — which then collided with the leftovers. ReplicaItem2.playeritemid is
    /// UNIQUE, so that collision failed the import outright; ComputedItemStat had no such guard
    /// and would simply have shown one item's rolled values on another.
    ///
    /// The delete path is fixed, so this only ever finds anything once. It runs on every start
    /// regardless, because the alternative is a database that stays broken until someone works
    /// out why looting stopped.
    /// </summary>
    private static void RemoveOrphanedRows(SqliteConnection connection) {
        if (!TableExists(connection, "PlayerItem")) return;

        foreach (var sql in new[] {
                     "DELETE FROM ReplicaItemRow WHERE replicaitemid NOT IN (SELECT Id FROM ReplicaItem2)",
                     "DELETE FROM ReplicaItem2 WHERE playeritemid IS NOT NULL " +
                     "AND playeritemid NOT IN (SELECT Id FROM PlayerItem)",
                     "DELETE FROM PlayerItemRecord WHERE PlayerItemId NOT IN (SELECT Id FROM PlayerItem)",
                     "DELETE FROM ComputedItemStat WHERE playeritemid NOT IN (SELECT Id FROM PlayerItem)",
                 }) {
            try { Execute(connection, sql); }
            catch (SqliteException) { /* table absent on a partially built database */ }
        }
    }

    /// <summary>
    /// Moves a database written by this port *before* it adopted upstream's layout.
    ///
    /// Early versions used their own names — <c>Id_PlayerItem</c>, <c>CreationDate</c>, and a
    /// flat <c>PlayerItemStat</c> table instead of ReplicaItem2/ReplicaItemRow. A collection is
    /// not reproducible (the loot files are consumed on import), so this converts rather than
    /// asking the user to start over.
    /// </summary>
    private static void MigrateFromPortSchema(SqliteConnection connection) {
        if (!ColumnExists(connection, "PlayerItem", "Id_PlayerItem")) return;

        using var transaction = connection.BeginTransaction();

        // The old table is renamed rather than copied out, so a failure leaves the original in
        // place under a known name instead of a half-populated new one.
        Execute(connection, "ALTER TABLE PlayerItem RENAME TO PlayerItem_pre_upstream;", transaction);
        Execute(connection, Tables.First(t => t.Table == "PlayerItem").Ddl, transaction);
        foreach (var (table, column, type) in MappedColumns) {
            if (table == "PlayerItem") {
                Execute(connection, $"ALTER TABLE PlayerItem ADD COLUMN {column} {type};", transaction);
            }
        }

        Execute(connection, """
            INSERT INTO PlayerItem (
                Id, baserecord, PrefixRecord, SuffixRecord, ModifierRecord, TransmuteRecord,
                Seed, MateriaRecord, RelicCompletionBonusRecord, RelicSeed, EnchantmentRecord,
                PrefixRarity, EnchantmentSeed, Name, namelowercase, Rarity, LevelRequirement,
                Mod, IsHardcore, created_at,
                AscendantAffixNameRecord, AscendantAffix2hNameRecord, RerollsUsed, AffixRerollsUsed
            )
            SELECT Id_PlayerItem, BaseRecord, PrefixRecord, SuffixRecord, ModifierRecord,
                   TransmuteRecord, Seed, MateriaRecord, RelicCompletionBonusRecord, RelicSeed,
                   EnchantmentRecord, PrefixRarity, EnchantmentSeed, Name, LOWER(Name), Rarity,
                   LevelRequirement, Mod, IsHardcore, CreationDate,
                   AscendantAffixNameRecord, AscendantAffix2hNameRecord, RerollsUsed, AffixRerollsUsed
            FROM PlayerItem_pre_upstream;
            """, transaction);

        // The captured tooltip lines move into upstream's two-table shape: one ReplicaItem2 per
        // item, its rows in ReplicaItemRow. Ordinal is dropped because upstream has no such
        // column and relies on insertion order, which the ORDER BY preserves.
        if (TableExists(connection, "PlayerItemStat")) {
            Execute(connection, """
                INSERT INTO ReplicaItem2 (Id, playeritemid)
                SELECT DISTINCT Id_PlayerItem, Id_PlayerItem FROM PlayerItemStat;
                """, transaction);
            Execute(connection, """
                INSERT INTO ReplicaItemRow (Id, replicaitemid, Type, Text, TextLowercase)
                SELECT Id_PlayerItemStat, Id_PlayerItem, TextClass, Text, LOWER(Text)
                FROM PlayerItemStat ORDER BY Id_PlayerItem, Ordinal;
                """, transaction);
            Execute(connection, "DROP TABLE PlayerItemStat;", transaction);
        }

        Execute(connection, "DROP TABLE PlayerItem_pre_upstream;", transaction);

        // Superseded by upstream's itemskill_v2 / itemskill_mapping, which are keyed by
        // DatabaseItem_v2 id rather than by record path.
        Execute(connection, "DROP TABLE IF EXISTS ItemSkillMapping;", transaction);
        Execute(connection, "DROP TABLE IF EXISTS ItemSkill;", transaction);

        // ComputedItemStat changes shape too (surrogate Id, lowercase columns), and unlike the
        // collection it is derived — 'iagd stats' rebuilds it in seconds.
        Execute(connection, "DROP TABLE IF EXISTS ComputedItemStat;", transaction);
        Execute(connection, Tables.First(t => t.Table == "ComputedItemStat").Ddl, transaction);

        transaction.Commit();

        foreach (var index in Indices) TryExecute(connection, index);
    }

    public static bool TableExists(SqliteConnection connection, string table) {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND lower(name) = lower($name);";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    public static bool ColumnExists(SqliteConnection connection, string table, string column) {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE lower(name) = lower($name);";
        command.Parameters.AddWithValue("$name", column);
        try { return Convert.ToInt32(command.ExecuteScalar()) > 0; }
        catch (SqliteException) { return false; }
    }

    private static void AddColumn(SqliteConnection connection, string table, string column, string type) {
        if (!TableExists(connection, table) || ColumnExists(connection, table, column)) return;
        Execute(connection, $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type};");
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null) {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryExecute(SqliteConnection connection, string sql) {
        try { Execute(connection, sql); }
        catch (SqliteException) { /* already present */ }
    }
}
