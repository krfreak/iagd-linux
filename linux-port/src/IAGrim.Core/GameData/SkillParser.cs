using DataAccess;
using IAGrim.Parser.Arz;

namespace IAGrim.Core.GameData;

/// <summary>A skill an item grants, as upstream's <c>ItemGrantedSkill</c> models it.</summary>
public sealed record ItemGrantedSkill {
    /// <summary>The skill's own record — the identity upstream stores in <c>itemskill_v2</c>.</summary>
    public required string Record { get; init; }

    public string? Name { get; init; }
    public string? Description { get; init; }
    public long Level { get; init; }

    /// <summary>
    /// <c>itemSkillAutoController</c>. Present means the skill fires by itself; absent means the
    /// player triggers it, which is what "grants a skill" means to someone building a bar.
    /// </summary>
    public string? Trigger { get; init; }

    /// <summary>
    /// Whether the skill record carries <c>spawnObjects</c>, i.e. it summons a pet.
    ///
    /// PORT: upstream resolves this at query time by joining the skill back to
    /// <c>DatabaseItemStat_v2</c> and testing <c>stat = 'spawnObjects'</c>. This port does not
    /// keep the game's stat rows (see PORTING.md), so the same test is made here, while the
    /// record's stats are in hand, and the answer stored. Same predicate, evaluated earlier.
    /// </summary>
    public bool SpawnsPets { get; init; }
}

/// <summary>
/// Resolves which items grant which skills.
///
/// Ported from upstream's <c>ComplexItemParser</c>. An item names a skill through
/// <c>itemSkillName</c>; that skill record may in turn be a shell that only points at a
/// <c>buffSkillName</c> sub-skill (upstream's example is the Apothecary's Touch), in which case
/// the sub-skill carries the real name and description. Missing that indirection would leave a
/// slice of items looking as though they grant nothing.
/// </summary>
public static class SkillParser {
    public sealed record Result(
        IReadOnlyList<ItemGrantedSkill> Skills,
        IReadOnlyList<(string ItemRecord, string SkillRecord)> Mappings);

    /// <summary>
    /// Walks every record in the game's databases and builds the item→skill mapping.
    ///
    /// Note this reads records unfiltered: <c>spawnObjects</c> and <c>itemSkillAutoController</c>
    /// are both dropped by the seed-engine stat filter. See
    /// <see cref="ItemDatabase.LoadAllStats"/>.
    /// </summary>
    public static Result Parse(
        IEnumerable<string> databases,
        IReadOnlyDictionary<string, string> tags,
        Action<string>? progress = null) {

        // Every record, keyed by path — the lookup has to reach skill records, not just items,
        // because that is where the display name and the pet flag live.
        var records = new Dictionary<string, Dictionary<string, IItemStat>>(StringComparer.OrdinalIgnoreCase);

        foreach (var database in databases) {
            progress?.Invoke($"scanning {Path.GetFileName(database)}");
            foreach (var item in ArzParser.LoadItemRecords(database, skipLots: true)) {
                if (item.Record is null || item.Stats is null) continue;

                var lookup = new Dictionary<string, IItemStat>(StringComparer.OrdinalIgnoreCase);
                foreach (var stat in item.Stats) {
                    if (stat.Stat is not null) lookup.TryAdd(stat.Stat, stat);
                }
                // Later databases override earlier ones, as expansions rebalance base records.
                records[item.Record] = lookup;
            }
        }

        var skills = new Dictionary<string, ItemGrantedSkill>(StringComparer.OrdinalIgnoreCase);
        var mappings = new List<(string, string)>();

        foreach (var (record, stats) in records) {
            var skill = SkillFor(record, stats, records, tags);
            if (skill is null) continue;

            // First occurrence wins, as upstream's HashSet<ItemGrantedSkill> does — it compares
            // on Record alone, so a second item granting the same skill is discarded. Level and
            // Trigger come from the *item* rather than the skill, so which item registers the
            // skill decides those two; upstream has the same property and the mapping table,
            // not this one, is what the filters actually join against.
            skills.TryAdd(skill.Record, skill);
            mappings.Add((record, skill.Record));
        }

        progress?.Invoke($"{skills.Count:N0} skills granted by {mappings.Count:N0} items");
        return new Result(skills.Values.ToList(), mappings);
    }

    /// <summary>
    /// Upstream's <c>ComplexItemParser.GetSkill</c>, field for field.
    /// </summary>
    private static ItemGrantedSkill? SkillFor(
        string itemRecord,
        Dictionary<string, IItemStat> itemStats,
        Dictionary<string, Dictionary<string, IItemStat>> records,
        IReadOnlyDictionary<string, string> tags) {

        if (!itemStats.TryGetValue("itemSkillName", out var skillName)) return null;

        var record = skillName.TextValue;
        if (string.IsNullOrWhiteSpace(record)) return null;

        // Doesn't exist??
        if (!records.TryGetValue(record, out var stats)) return null;

        // Some items (like the Apothecary's touch) just references a subskill
        if (stats.TryGetValue("buffSkillName", out var subSkill)
            && subSkill.TextValue is { Length: > 0 } subRecord
            && records.TryGetValue(subRecord, out var sub)) {
            stats = sub;
            record = subRecord;
        }

        string? Text(Dictionary<string, IItemStat> from, string key) =>
            from.TryGetValue(key, out var stat) ? stat.TextValue : null;

        var nameTag = Text(stats, "skillDisplayName") ?? string.Empty;
        var descTag = Text(stats, "skillBaseDescription") ?? string.Empty;

        var level = Text(itemStats, "itemSkillLevelEq");
        long parsedLevel = 0;
        if (!string.IsNullOrEmpty(level)) long.TryParse(level, out parsedLevel);

        return new ItemGrantedSkill {
            Record      = record,
            Name        = tags.TryGetValue(nameTag, out var name) ? name : null,
            Description = tags.TryGetValue(descTag, out var desc) ? desc : null,
            Level       = parsedLevel,
            Trigger     = Text(itemStats, "itemSkillAutoController"),
            SpawnsPets  = stats.ContainsKey("spawnObjects"),
        };
    }
}
