using Microsoft.Data.Sqlite;

namespace IAGrim.Core.GameData;

/// <summary>
/// Stores Grim Dawn's item templates alongside the collection, so an item's name, class and
/// icon can be resolved from its record path alone.
///
/// Kept in the same database as PlayerItem deliberately: the interesting queries join the
/// two ("show my items, named, sorted by level"), and a cross-database join would mean
/// doing it in memory.
/// </summary>
public sealed class GameDataStore : IDisposable {
    private readonly SqliteConnection _connection;

    public GameDataStore(string databasePath) {
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        IAGrim.Platform.Schema.Apply(_connection);
    }

    /// <summary>
    /// Replaces the template set in one transaction. A partial import is worse than none:
    /// it would silently leave items unnamed with no indication why.
    /// </summary>
    /// <param name="mod">
    /// Empty for vanilla, otherwise the mod folder name — which is what Grim Dawn reports to the
    /// hook and therefore what lands in PlayerItem.Mod. Templates are keyed by (mod, record) so
    /// a mod redefining a base-game record does not overwrite it.
    /// </param>
    public int ReplaceTemplates(IEnumerable<ItemTemplate> templates, string mod = "") {
        // Enumerated twice below (ItemTemplate, then DatabaseItem_v2), and the caller may hand
        // over a lazy sequence — a second pass over a spent iterator would silently write an
        // empty item table.
        var all = templates as IReadOnlyCollection<ItemTemplate> ?? templates.ToList();
        templates = all;

        using var transaction = _connection.BeginTransaction();

        using (var clear = _connection.CreateCommand()) {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM ItemTemplate WHERE Mod = $mod;";
            clear.Parameters.AddWithValue("$mod", mod);
            clear.ExecuteNonQuery();
        }

        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR REPLACE INTO ItemTemplate
                (Mod, Record, Name, NameTag, Quality, ItemClass, Bitmap, IconFile, LevelRequirement,
                 Classification, SetRecord, SetName)
            VALUES ($mod, $record, $name, $tag, $quality, $class, $bitmap, $icon, $level,
                    $classification, $setRecord, $setName);
            """;
        insert.Parameters.AddWithValue("$mod", mod);

        var record   = insert.Parameters.Add("$record", SqliteType.Text);
        var name     = insert.Parameters.Add("$name", SqliteType.Text);
        var tag      = insert.Parameters.Add("$tag", SqliteType.Text);
        var quality  = insert.Parameters.Add("$quality", SqliteType.Text);
        var itemClass= insert.Parameters.Add("$class", SqliteType.Text);
        var bitmap   = insert.Parameters.Add("$bitmap", SqliteType.Text);
        var icon     = insert.Parameters.Add("$icon", SqliteType.Text);
        var level    = insert.Parameters.Add("$level", SqliteType.Integer);
        var classification = insert.Parameters.Add("$classification", SqliteType.Text);
        var setRecord      = insert.Parameters.Add("$setRecord", SqliteType.Text);
        var setName        = insert.Parameters.Add("$setName", SqliteType.Text);

        var count = 0;
        foreach (var template in templates) {
            record.Value    = template.Record;
            name.Value      = (object?)template.Name ?? DBNull.Value;
            tag.Value       = (object?)template.NameTag ?? DBNull.Value;
            quality.Value   = (object?)template.Quality ?? DBNull.Value;
            itemClass.Value = (object?)template.ItemClass ?? DBNull.Value;
            bitmap.Value    = (object?)template.Bitmap ?? DBNull.Value;
            icon.Value      = (object?)IconFileFor(template.Bitmap) ?? DBNull.Value;
            level.Value     = template.LevelRequirement;
            classification.Value = (object?)template.Classification ?? DBNull.Value;
            setRecord.Value      = (object?)template.SetRecord ?? DBNull.Value;
            setName.Value        = (object?)template.SetName ?? DBNull.Value;
            insert.ExecuteNonQuery();
            count++;
        }

        // Upstream's own item table, populated alongside ours. It carries only record, name and
        // a change hash — everything else upstream keeps as stat rows — but it is the table the
        // skill mapping and every record-driven filter join against, so it has to exist and it
        // has to hold the same ids.
        //
        // It has no mod dimension (keyed by record alone), so it is cleared only on the vanilla
        // pass; a mod's records are then layered in on top, which is how the game reads them.
        //
        // The stat rows go first, and not only because upstream's FK_95F02CAE forbids the other
        // order. Those rows are keyed by id_databaseitem, and this rebuild reassigns every one
        // of those ids — keeping them would silently attach one record's stats to another. They
        // are regenerated by 'iagd stats', which is why Parse tells the user to run it.
        if (mod.Length == 0) {
            using var clear = _connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM DatabaseItemStat_v2; DELETE FROM DatabaseItem_v2;";
            clear.ExecuteNonQuery();
        }

        using (var insert2 = _connection.CreateCommand()) {
            insert2.Transaction = transaction;
            // hash is upstream's "has this record changed since the last parse" marker, computed
            // with String.GetHashCode(). That is randomised per process on .NET Core, so it is
            // not portable and not reproducible; this port always rewrites the whole table and
            // never reads it. Zero is written so the column exists with a defined value — the
            // Windows tool reading this database will simply decide every record needs
            // reparsing, which costs it one slow parse and nothing else.
            insert2.CommandText = """
                INSERT OR REPLACE INTO DatabaseItem_v2 (baserecord, name, hash, namelowercase)
                VALUES ($record, $name, 0, $lower);
                """;
            var record2 = insert2.Parameters.Add("$record", SqliteType.Text);
            var name2   = insert2.Parameters.Add("$name", SqliteType.Text);
            var lower2  = insert2.Parameters.Add("$lower", SqliteType.Text);

            foreach (var template in templates) {
                record2.Value = template.Record;
                name2.Value   = (object?)template.Name ?? DBNull.Value;
                lower2.Value  = (object?)template.Name?.ToLowerInvariant() ?? DBNull.Value;
                insert2.ExecuteNonQuery();
            }
        }

        SetMeta(transaction, mod.Length == 0 ? "templates.count" : $"templates.count.{mod}",
                count.ToString());
        SetMeta(transaction, "templates.builtUtc", DateTimeOffset.UtcNow.ToString("O"));

        transaction.Commit();
        return count;
    }

    /// <summary>
    /// Drops template sets for mods that are no longer installed.
    ///
    /// Without this, uninstalling a mod leaves its templates behind forever, and they keep
    /// winning the mod-first lookup for any item still tagged with that mod — so the item shows
    /// a name from a mod the player no longer has. Vanilla is never dropped.
    /// </summary>
    public int RemoveTemplatesForMissingMods(IEnumerable<string> installedMods) {
        var keep = new HashSet<string>(installedMods, StringComparer.OrdinalIgnoreCase) { "" };

        var stale = new List<string>();
        using (var command = _connection.CreateCommand()) {
            command.CommandText = "SELECT DISTINCT Mod FROM ItemTemplate;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                var mod = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (!keep.Contains(mod)) stale.Add(mod);
            }
        }

        foreach (var mod in stale) {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM ItemTemplate WHERE Mod = $mod;";
            command.Parameters.AddWithValue("$mod", mod);
            command.ExecuteNonQuery();
        }
        return stale.Count;
    }

    /// <summary>
    /// Grim Dawn's localised tag table. Upstream keeps it so item names can be recomposed
    /// without re-reading the .arc archives; the same rows are written here for compatibility.
    /// </summary>
    public int ReplaceTags(IReadOnlyDictionary<string, string> tags) {
        using var transaction = _connection.BeginTransaction();

        using (var clear = _connection.CreateCommand()) {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM ItemTag;";
            clear.ExecuteNonQuery();
        }

        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR REPLACE INTO ItemTag (Tag, Name) VALUES ($tag, $name);";
        var tag  = insert.Parameters.Add("$tag", SqliteType.Text);
        var name = insert.Parameters.Add("$name", SqliteType.Text);

        var count = 0;
        foreach (var (key, value) in tags) {
            tag.Value = key;
            name.Value = value;
            insert.ExecuteNonQuery();
            count++;
        }

        transaction.Commit();
        return count;
    }

    /// <summary>
    /// Replaces the item→skill mapping, in one transaction for the same reason the templates
    /// are: half a mapping would make "grants a skill" quietly under-report.
    ///
    /// Mirrors upstream's <c>ItemSkillDaoImpl.Save(..., additive: false)</c>, which deletes both
    /// tables before writing.
    /// </summary>
    public (int Skills, int Mappings) ReplaceSkills(SkillParser.Result parsed) {
        using var transaction = _connection.BeginTransaction();

        foreach (var table in new[] { "itemskill_mapping", "itemskill_v2" }) {
            using var clear = _connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = $"DELETE FROM {table};";
            clear.ExecuteNonQuery();
        }

        // Upstream keys both tables by DatabaseItem_v2.id_databaseitem, resolved with a subselect
        // on the record path — exactly as ItemSkillDaoImpl.Save does. Keeping the surrogate ids
        // rather than substituting record paths is what lets upstream's queries run unchanged,
        // here and against a database written by the Windows tool.
        var skills = 0;
        using (var insert = _connection.CreateCommand()) {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO itemskill_v2 (Description, Level, Name, Record, id_databaseitem, Trigger)
                VALUES ($description, $level, $name, $record,
                        (SELECT id_databaseitem FROM DatabaseItem_v2 WHERE baserecord = $record LIMIT 1),
                        $trigger);
                """;
            var record      = insert.Parameters.Add("$record", SqliteType.Text);
            var name        = insert.Parameters.Add("$name", SqliteType.Text);
            var description = insert.Parameters.Add("$description", SqliteType.Text);
            var level       = insert.Parameters.Add("$level", SqliteType.Integer);
            var trigger     = insert.Parameters.Add("$trigger", SqliteType.Text);

            foreach (var skill in parsed.Skills) {
                record.Value      = skill.Record;
                name.Value        = (object?)skill.Name ?? DBNull.Value;
                description.Value = (object?)skill.Description ?? DBNull.Value;
                level.Value       = skill.Level;
                trigger.Value     = (object?)skill.Trigger ?? DBNull.Value;
                insert.ExecuteNonQuery();
                skills++;
            }
        }

        var mappings = 0;
        using (var insert = _connection.CreateCommand()) {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO itemskill_mapping (id_skill, id_databaseitem)
                VALUES ((SELECT id_skill FROM itemskill_v2 WHERE Record = $skill LIMIT 1),
                        (SELECT id_databaseitem FROM DatabaseItem_v2 WHERE baserecord = $item LIMIT 1));
                """;
            var item  = insert.Parameters.Add("$item", SqliteType.Text);
            var skill = insert.Parameters.Add("$skill", SqliteType.Text);

            foreach (var (itemRecord, skillRecord) in parsed.Mappings) {
                item.Value  = itemRecord;
                skill.Value = skillRecord;
                mappings += insert.ExecuteNonQuery();
            }
        }

        // A mapping whose item or skill has no DatabaseItem_v2 row inserts NULLs and matches
        // nothing later. Dropping them here keeps the failure visible in the count rather than
        // as filters that silently under-report.
        using (var prune = _connection.CreateCommand()) {
            prune.Transaction = transaction;
            prune.CommandText =
                "DELETE FROM itemskill_mapping WHERE id_skill IS NULL OR id_databaseitem IS NULL;";
            mappings -= prune.ExecuteNonQuery();
        }

        SetMeta(transaction, "skills.count", skills.ToString());
        transaction.Commit();
        return (skills, mappings);
    }

    /// <summary>
    /// Maps a record's bitmap reference to the file DdsIconExtractor wrote.
    ///
    /// The record says <c>items/gearweapons/guns1h/bitmaps/c012_gun1h.tex</c>; extraction
    /// flattens to a filename and appends .png, giving <c>c012_gun1h.tex.png</c>.
    /// </summary>
    public static string? IconFileFor(string? bitmap) =>
        string.IsNullOrWhiteSpace(bitmap)
            ? null
            : Path.GetFileName(bitmap.Replace('\\', '/')) + ".png";

    public int TemplateCount() {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ItemTemplate;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int DatabaseItemCount() {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DatabaseItem_v2;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Whether Grim Dawn's data has been read at all — upstream's <c>local.isGrimDawnParsed</c>,
    /// derived the same way it does (<c>databaseItemDao.GetRowCount() &gt; 0</c>, StartupService).
    ///
    /// The hook refuses to loot anything while that key is false, so this answer ends up in the
    /// bridge file; see <c>BridgeSettings</c>. Failing to open the database means nothing has
    /// been parsed into it, which is the same answer.
    /// </summary>
    public static bool HasParsedItems(string databasePath) {
        try {
            using var store = new GameDataStore(databasePath);
            return store.DatabaseItemCount() > 0;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>
    /// Records what a parse was made from, so staleness can be detected rather than guessed at.
    /// </summary>
    public void RecordParseSource(long sourceTimestamp, string language) {
        using var transaction = _connection.BeginTransaction();
        SetMeta(transaction, ItemDatabase.VersionKey, ItemDatabase.Version.ToString());
        SetMeta(transaction, "gamedata.sourceTimestamp", sourceTimestamp.ToString());
        SetMeta(transaction, "gamedata.language", language);
        transaction.Commit();
    }

    public string? Meta(string key) {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Value FROM GameDataMeta WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    /// <summary>The collection, enriched with everything known about each item.</summary>
    public IEnumerable<CollectionRow> Collection(string? nameFilter = null) {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id, p.Name, p.baserecord, p.Seed,
                   COALESCE(tm.Name, tv.Name), COALESCE(tm.ItemClass, tv.ItemClass),
                   COALESCE(tm.Quality, tv.Quality), COALESCE(tm.LevelRequirement, tv.LevelRequirement),
                   COALESCE(tm.IconFile, tv.IconFile)
            FROM PlayerItem p
            LEFT JOIN ItemTemplate tm ON tm.Record = p.baserecord AND tm.Mod = IFNULL(p.Mod, '')
            LEFT JOIN ItemTemplate tv ON tv.Record = p.baserecord AND tv.Mod = ''
            WHERE ($filter IS NULL
                   OR p.Name LIKE '%' || $filter || '%'
                   OR COALESCE(tm.Name, tv.Name) LIKE '%' || $filter || '%')
            ORDER BY COALESCE(tm.LevelRequirement, tv.LevelRequirement) DESC, p.Id;
            """;
        command.Parameters.AddWithValue("$filter", (object?)nameFilter ?? DBNull.Value);

        using var reader = command.ExecuteReader();
        var rows = new List<CollectionRow>();
        while (reader.Read()) {
            rows.Add(new CollectionRow(
                Id:           reader.GetInt64(0),
                LootedName:   reader.IsDBNull(1) ? null : reader.GetString(1),
                BaseRecord:   reader.GetString(2),
                Seed:         reader.GetInt64(3),
                TemplateName: reader.IsDBNull(4) ? null : reader.GetString(4),
                ItemClass:    reader.IsDBNull(5) ? null : reader.GetString(5),
                Quality:      reader.IsDBNull(6) ? null : reader.GetString(6),
                Level:        reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                IconFile:     reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return rows;
    }

    private void SetMeta(SqliteTransaction transaction, string key, string value) {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO GameDataMeta (Key, Value) VALUES ($key, $value) " +
            "ON CONFLICT(Key) DO UPDATE SET Value = $value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void Execute(string sql) {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>A collection entry joined with what the game database knows about it.</summary>
public sealed record CollectionRow(
    long Id,
    string? LootedName,
    string BaseRecord,
    long Seed,
    string? TemplateName,
    string? ItemClass,
    string? Quality,
    int Level,
    string? IconFile);
