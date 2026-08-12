using IAGrim.Core.ItemStats.Dto;

namespace IAGrim.Core.ItemStats;

/// <summary>
/// Item rarity, "level of green" and level requirement, derived from an item's records.
///
/// Ported from upstream's <c>ItemOperationsUtility</c> and
/// <c>PlayerItemDaoImpl.GetGreenQualityLevelForRecords</c>. These three values are stored on
/// PlayerItem at ingestion because upstream's search filters on the stored columns, not on a
/// join — <c>PI.Rarity = :rarity</c> and <c>PI.PrefixRarity &gt;= :prefixRarity</c>.
///
/// The naming is upstream's and it is not intuitive:
///
/// - "Rarity" is a *display colour*, not the game's classification. Grim Dawn's Legendary is
///   IA's "Epic", Epic is "Blue". Diverging here would silently mislabel every item.
/// - "PrefixRarity" is not a rarity at all: it counts how many of an item's affixes are Rare,
///   so a double-rare ("green") item sorts above a single-rare one.
/// - <c>GetMinimumLevelForRecords</c> returns the <em>maximum</em> level requirement across
///   the records, since an item is gated by its most demanding part.
///
/// The stat rows fed in must already be filtered by <see cref="StatFilter"/>, matching the
/// rows upstream's <c>GetStats(records, StatFetch.PlayerItems)</c> returns.
/// </summary>
public static class ItemRarity {
    /// <summary>Grim Dawn's classification to IA's display colour. Verbatim from upstream.</summary>
    public static string TranslateClassification(IEnumerable<string?> classifications) {
        var enumerable = classifications as string[] ?? classifications.ToArray();
        if (enumerable.Contains("Legendary"))
            return "Epic";
        else if (enumerable.Contains("Epic"))
            return "Blue";
        else if (enumerable.Contains("Rare"))
            return "Green";
        else if (enumerable.Contains("Magical"))
            return "Yellow";
        else if (enumerable.Contains("Common"))
            return "White";
        else
            return "Unknown";
    }

    public static string ForRecords(
        IReadOnlyDictionary<string, List<DBStatRow>> stats, IEnumerable<string> records) {

        var classifications = records
            .Where(stats.ContainsKey)
            .SelectMany(record => stats[record].Where(v => v.Stat == "itemClassification"))
            .Select(m => m.TextValue);

        return TranslateClassification(classifications);
    }

    /// <summary>
    /// The "level of green": an item with a white suffix and a green prefix is worth less than
    /// one with two green affixes, and upstream's filter is a <c>&gt;=</c> over this count.
    ///
    /// Only affix records count, which is why the base record (and green components, which are
    /// materia) are excluded — otherwise a legendary base would make every item score.
    /// </summary>
    public static int GreenQualityLevelForRecords(
        IReadOnlyDictionary<string, List<DBStatRow>> stats, IEnumerable<string> records) {

        // Filter out green components
        var filteredRecords = records
            .Where(record => !record.StartsWith("records/items/materia/"))
            .Where(record => record.Contains("/lootaffixes/")) // Ignore the base record
            .ToList();

        var classifications = filteredRecords
            .Where(stats.ContainsKey)
            .SelectMany(record => stats[record].Where(v => v.Stat == "itemClassification"))
            .Select(m => m.TextValue)
            .ToList();

        if (classifications.All(m => m != "Legendary" && m != "Epic")) {
            return classifications.Count(m => m == "Rare");
        }

        return 0;
    }

    /// <summary>
    /// Highest level requirement across the item's records — an item is gated by its most
    /// demanding part. Upstream calls this "minimum" and then takes Max(); the name refers to
    /// the minimum character level needed, not to a minimum over the records.
    /// </summary>
    public static float MinimumLevelForRecords(
        IReadOnlyDictionary<string, List<DBStatRow>> stats, IEnumerable<string> records) {

        var levels = records
            .Where(stats.ContainsKey)
            .SelectMany(record => stats[record].Where(v => v.Stat == "levelRequirement"))
            .Select(m => m.Value)
            .ToList();

        if (levels.Count == 0)
            return 0;
        else
            return (float)levels.Max<double>();
    }
}
