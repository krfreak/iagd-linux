using DataAccess;
using IAGrim.Core.ItemStats.Dto;
using Microsoft.Data.Sqlite;
using StatTranslator;

namespace IAGrim.Core.ItemStats;

/// <summary>One rendered tooltip line, tagged with the replica row type it stands in for.</summary>
public sealed record StatText(int TextClass, string Text);

/// <summary>
/// The tooltip lines for an item, computed rather than captured.
///
/// **Why this exists.** The hook captures what Grim Dawn drew, which is perfect and only
/// available for items looted while playing. Anything that arrived another way — a merged
/// collection, a GD Stash import — had no lines at all, so its card showed a name and a level
/// and nothing else. Upstream does not have that gap: it computes every item's text itself and
/// treats the captured tooltip as an override.
///
/// **This is upstream's own code, not a reimplementation.** <c>StatManager</c> and
/// <c>EnglishLanguage</c> come from the pinned submodule, which this project already references
/// for <c>ItemNameCombinator</c>. A thousand lines of stat-to-text rules — damage ranges,
/// conversions, racial bonuses, pet scoping, skill modifiers — are exactly the kind of thing
/// that would be subtly wrong if retyped, and the point of the port is to agree with the
/// Windows tool line for line.
///
/// What this class supplies is the input: the stat rows for the item's records, with the
/// seed-rolled values laid over them, in the shape <c>StatManager</c> expects.
/// </summary>
public sealed class ItemStatText {
    private readonly string _databasePath;
    private readonly Lazy<StatManager?> _stats;

    public ItemStatText(string databasePath) {
        _databasePath = databasePath;
        _stats = new Lazy<StatManager?>(BuildStatManager);
    }

    /// <summary>True when the game database has been parsed far enough to render anything.</summary>
    public bool Available => _stats.Value is not null;

    /// <summary>
    /// The language upstream's rules format through.
    ///
    /// The game's own tags come from ItemTag, filled by <c>iagd parse</c>, so this needs no
    /// access to the game's archives at request time. EnglishLanguage layers upstream's own
    /// custom tags (damage conversion, the resistance templates) over them and supplies
    /// defaults for anything missing, which is what it is for.
    /// </summary>
    private StatManager? BuildStatManager() {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try {
            using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Tag, Name FROM ItemTag;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1)) tags[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch (SqliteException) { return null; }

        if (tags.Count == 0) return null;   // not parsed yet

        return new StatManager(new EnglishLanguage(tags));
    }

    /// <summary>
    /// Lines for one item, or an empty list when it cannot be described — no parsed game data,
    /// or an item whose records carry no stats.
    /// </summary>
    public IReadOnlyList<StatText> Describe(SqliteConnection connection, long playerItemId) {
        var manager = _stats.Value;
        if (manager is null) return [];

        var stats = LoadStats(connection, playerItemId);
        if (stats.Count == 0) return [];

        var lines = new List<StatText>();

        // The same three passes upstream renders: the header block (weapon damage, armour,
        // attack speed), the body (everything else), and the pet bonuses.
        //
        // Identical lines are dropped, which upstream does with a ToHashSet() over its rendered
        // stats. An item is made of several records — base, prefix, suffix — and where two of
        // them carry the same stat, the same sentence comes out twice. It was three copies of
        // "+198% Aether Damage" on a real item before this.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Append(lines, seen, manager.ProcessStats(stats, TranslatedStatType.HEADER), HeaderClass);
        Append(lines, seen, manager.ProcessStats(stats, TranslatedStatType.BODY), BodyClass);

        var pet = manager.ProcessStats(stats, TranslatedStatType.PET);
        if (pet.Count > 0) {
            var petLines = new List<StatText>();
            Append(petLines, seen, pet, PetClass);
            if (petLines.Count > 0) {
                lines.Add(new StatText(PetHeadingClass, "Bonus to All Pets"));
                lines.AddRange(petLines);
            }
        }

        return lines;
    }

    /// <summary>
    /// Replica row types to borrow, so a computed line is styled exactly like a captured one:
    /// 17 is the header block, 18 a regular stat, 68 the pet heading and 69 a pet line.
    /// </summary>
    private const int HeaderClass = 17;
    private const int BodyClass = 18;
    private const int PetHeadingClass = 68;
    private const int PetClass = 69;

    private static void Append(List<StatText> lines, HashSet<string> seen,
                               IEnumerable<TranslatedStat> stats, int textClass) {
        foreach (var stat in stats) {
            var text = stat.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!seen.Add(text)) continue;
            lines.Add(new StatText(textClass, text));
        }
    }

    /// <summary>
    /// The stat rows for one item, built the way upstream builds them in
    /// <c>ItemStatService.BuildTags</c>.
    ///
    /// Three things in that method matter and none of them are obvious:
    ///
    /// 1. **Per record, a stat appears once, at its highest value** (upstream's <c>Filter</c>).
    /// 2. **The roll replaces the numerics of base, prefix and suffix wholesale.** The seed
    ///    engine is given those three records and its output *is* the item's numbers; only the
    ///    text-valued rows — item class, set name, conversion types, skill references — are
    ///    carried over from the records themselves.
    /// 3. **Across records, numeric stats are summed** (upstream's <c>process</c>). A modifier
    ///    or a pet-bonus record adding to something the base record already has produces one
    ///    line with the total, not two lines.
    ///
    /// This port used to read the roll back from <c>ComputedItemStat</c> instead, which is keyed
    /// by item and stat name — so when two of an item's records carried the same stat, one value
    /// overwrote the other and a line went missing from the card. That table stays as it is: the
    /// filters need the rolled values in SQL, which is a different question from rendering.
    /// </summary>
    private static HashSet<IItemStat> LoadStats(SqliteConnection connection, long playerItemId) {
        var item = LoadItem(connection, playerItemId);
        if (item is null) return [];

        var byRecord = LoadRecordStats(connection, playerItemId);

        List<DBStatRow> Raw(string? record) =>
            record is not null && byRecord.TryGetValue(record, out var rows) ? rows : [];

        // Upstream's Filter: one row per stat, the highest value winning.
        static List<DBStatRow> Filter(IEnumerable<DBStatRow> rows) =>
            rows.GroupBy(r => r.Stat)
                .Select(g => g.OrderByDescending(r => r.Value).First())
                .ToList();

        List<DBStatRow> Filtered(string? record) => Filter(Raw(record));

        var rolled = item.Seed == 0
            ? null
            : SeedStatCalculator.Compute(Raw(item.BaseRecord), Raw(item.PrefixRecord),
                                         Raw(item.SuffixRecord), (uint)item.Seed);

        var stats = new List<DBStatRow>();

        if (rolled is not null) {
            foreach (var record in new[] { item.BaseRecord, item.PrefixRecord, item.SuffixRecord }) {
                foreach (var row in Filtered(record)) {
                    if (!string.IsNullOrEmpty(row.TextValue)) stats.Add(row);
                }
            }
            foreach (var (stat, value) in rolled) {
                stats.Add(new DBStatRow { Stat = stat, Value = value });
            }
        }
        else {
            // The engine could not model one of the records; the raw rows are upstream's
            // fallback, and are what a non-rolling item (a component, a potion) has anyway.
            stats.AddRange(Filtered(item.BaseRecord));
            stats.AddRange(Filtered(item.PrefixRecord));
            stats.AddRange(Filtered(item.SuffixRecord));
        }

        stats.AddRange(Filtered(item.ModifierRecord));
        foreach (var petRecord in item.PetRecords) stats.AddRange(Filtered(petRecord));

        // Upstream's process(): numerics summed across records, text rows kept as they are.
        var withText = stats.Where(r => !string.IsNullOrEmpty(r.TextValue));
        var summed = stats.Where(r => string.IsNullOrEmpty(r.TextValue))
            .GroupBy(r => r.Stat)
            .Select(g => new DBStatRow {
                Record    = g.First().Record,
                TextValue = g.First().TextValue,
                Stat      = g.First().Stat,
                Value     = g.Sum(r => r.Value),
            })
            .ToList();

        summed.AddRange(withText);
        return [.. summed.Cast<IItemStat>()];
    }

    private sealed record ItemRecords(string BaseRecord, string? PrefixRecord, string? SuffixRecord,
                                      string? ModifierRecord, long Seed,
                                      IReadOnlyList<string> PetRecords);

    private static ItemRecords? LoadItem(SqliteConnection connection, long playerItemId) {
        ItemRecords? item = null;

        using (var command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT baserecord, PrefixRecord, SuffixRecord, ModifierRecord, IFNULL(Seed, 0)
                FROM PlayerItem WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", playerItemId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            item = new ItemRecords(reader.IsDBNull(0) ? "" : reader.GetString(0),
                                   Text(1), Text(2), Text(3), reader.GetInt64(4), []);
        }

        // The pet-bonus records, which are the PlayerItemRecord rows that are not the item's own.
        // The precompute writes them there precisely so this lookup is possible.
        var pets = new List<string>();
        using (var command = connection.CreateCommand()) {
            command.CommandText = """
                SELECT r.Record FROM PlayerItemRecord r
                 JOIN PlayerItem p ON p.Id = r.PlayerItemId
                WHERE r.PlayerItemId = $id
                  AND r.Record NOT IN (IFNULL(p.baserecord,''), IFNULL(p.PrefixRecord,''),
                                       IFNULL(p.SuffixRecord,''), IFNULL(p.ModifierRecord,''),
                                       IFNULL(p.MateriaRecord,''), IFNULL(p.TransmuteRecord,''),
                                       IFNULL(p.RelicCompletionBonusRecord,''),
                                       IFNULL(p.EnchantmentRecord,''),
                                       IFNULL(p.AscendantAffixNameRecord,''),
                                       IFNULL(p.AscendantAffix2hNameRecord,''));
                """;
            command.Parameters.AddWithValue("$id", playerItemId);
            try {
                using var reader = command.ExecuteReader();
                while (reader.Read()) pets.Add(reader.GetString(0));
            }
            catch (SqliteException) { /* records not written yet */ }
        }

        return item with { PetRecords = pets };
    }

    /// <summary>Every stored stat row for every record this item references, keyed by record.</summary>
    private static Dictionary<string, List<DBStatRow>> LoadRecordStats(
        SqliteConnection connection, long playerItemId) {

        var result = new Dictionary<string, List<DBStatRow>>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT db.baserecord, dbs.Stat, dbs.TextValue, dbs.val1
            FROM PlayerItem p
            JOIN DatabaseItem_v2 db
              ON db.baserecord IN (p.baserecord, p.PrefixRecord, p.SuffixRecord,
                                   p.ModifierRecord, p.MateriaRecord, p.TransmuteRecord,
                                   p.RelicCompletionBonusRecord, p.EnchantmentRecord,
                                   p.AscendantAffixNameRecord, p.AscendantAffix2hNameRecord)
              OR db.baserecord IN (SELECT Record FROM PlayerItemRecord WHERE PlayerItemId = p.Id)
            JOIN DatabaseItemStat_v2 dbs ON dbs.id_databaseitem = db.id_databaseitem
            WHERE p.Id = $id;
            """;
        command.Parameters.AddWithValue("$id", playerItemId);

        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            var record = reader.GetString(0);
            if (!result.TryGetValue(record, out var rows)) result[record] = rows = [];
            rows.Add(new DBStatRow {
                Record    = record,
                Stat      = reader.IsDBNull(1) ? null : reader.GetString(1),
                TextValue = reader.IsDBNull(2) ? null : reader.GetString(2),
                Value     = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
            });
        }
        return result;
    }
}
