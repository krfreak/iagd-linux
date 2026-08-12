namespace IAGrim.Core.ItemStats;

/// <summary>
/// The exact stat filter upstream applies before any stat row reaches the seed engine
/// (<c>DatabaseItemStatDaoImpl.GetStats</c>):
///
/// <code>
///   AND (val1 &gt; 0 or stat in ( :whitelist ))
///   AND NOT stat IN ( :blacklist )
/// </code>
///
/// This is not cosmetic. The engine draws from a shared random stream in a fixed field
/// order, so feeding it a rollable field the game did not roll inserts an extra draw and
/// desyncs every value after it. Matching upstream's filter exactly is what makes the
/// computed numbers identical to the ones Grim Dawn shows.
///
/// Both lists are copied verbatim from upstream and are tracked in upstream-sync.tsv.
/// </summary>
public static class StatFilter {
    /// <summary>
    /// Presentation and physics fields upstream drops outright, whatever their value.
    /// </summary>
    private static readonly HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase) {
        "physicsMass", "actorHeight", "actorRadius", "chest", "itemCost", "medalVisible",
        "medal", "marketAdjustmentPercent", "physicsFriction", "waist", "scale", "sword",
        "sword2h", "castsShadows", "feet"
    };

    /// <summary>
    /// Text stats upstream keeps even when their numeric value is zero -- names, tags and
    /// conversion types carry meaning in the text column rather than the value.
    /// </summary>
    private static readonly HashSet<string> Whitelist = new(StringComparer.OrdinalIgnoreCase) {
        "setName", "itemSetName", "petBonusName", "Class", "itemClassification",
        "augmentMasteryLevel1", "augmentMasteryLevel2", "augmentMasteryLevel4",
        "augmentMasteryLevel3", "augmentMasteryName1", "augmentMasteryName2",
        "augmentMasteryName3", "augmentMasteryName4", "augmentSkillLevel1",
        "augmentSkillLevel2", "augmentSkillLevel3", "augmentSkillLevel4", "augmentSkillName1",
        "augmentSkillName2", "augmentSkillName3", "augmentSkillName4", "augmentAllLevel",
        "factionSource", "skillDownBitmapName", "skillUpBitmapName", "bitmap", "noteBitmap",
        "artifactFormulaBitmapName", "artifactBitmap", "bitmapButtonDown", "bitmapButtonUp",
        "relicBitmap", "shardBitmap", "emptyBitmap", "fullBitmap", "lootRandomizerName",
        "itemNameTag", "itemQualityTag", "itemStyleTag", "description", "levelRequirement",
        "itemSkillName", "skillDisplayName", "petSkillName", "buffSkillName",
        "characterBaseAttackSpeedTag", "conversionInType", "conversionOutType",
        "racialBonusRace", "itemText", "MasteryEnumeration", "modifiedSkillName1",
        "modifiedSkillName2", "modifiedSkillName3", "modifiedSkillName4", "modifierSkillName1",
        "modifierSkillName2", "modifierSkillName3", "modifierSkillName4",
        "petconversionOutType", "petconversionInType", "petBurstSpawn"
    };

    /// <summary>Mirrors upstream's WHERE clause for a single row.</summary>
    public static bool Keep(string stat, double value) =>
        !Blacklist.Contains(stat) && (value > 0 || Whitelist.Contains(stat));

    public static int BlacklistCount => Blacklist.Count;
    public static int WhitelistCount => Whitelist.Count;

    /// <summary>
    /// Exposed so the lists can be checked against upstream's source automatically
    /// (scripts/verify-stat-filter.sh). These lists must not drift: a field added to
    /// upstream's blacklist that we still feed the engine would desync the draw stream and
    /// silently change every computed value.
    /// </summary>
    public static IReadOnlyCollection<string> BlacklistEntries => Blacklist;
    public static IReadOnlyCollection<string> WhitelistEntries => Whitelist;
}
