namespace IAGrim.Platform;

/// <summary>
/// What is allowed into a collection, and what the Windows tool has always refused.
///
/// The rules are the hook's, from <c>InventorySack_AddItem::IsRelevant</c> — components,
/// crafting materials, quest items, the miscellaneous drawer, the salt bag, anything that
/// stacks, and story elements apart from a short list of real equipment that happens to live
/// under that path. The hook applies them as an item is looted, so anything arriving that way
/// is already filtered; this port's file-based imports (the transfer stash, a GD Stash file,
/// another collection) bypass the hook entirely and had no equivalent.
///
/// The exceptions are upstream's, comments and all: Lokarr's four pieces and the two Gazer Man
/// torsos are genuine gear that the game happens to store under <c>storyelements</c>.
///
/// Kept as a separate class rather than folded into the importers because it decides what a
/// collection may contain, and that has to be one answer rather than one per entry point.
/// </summary>
public static class ItemAdmission {
    /// <summary>Story-element records upstream lets through, with its own names for them.</summary>
    private static readonly string[] AllowedStoryElements = [
        "records/storyelements/signs/signh.dbr",          // Lokarr's Gaze
        "records/storyelements/signs/signf.dbr",          // Lokarr's Boots
        "records/storyelements/signs/signs.dbr",          // Lokarr's Mantle
        "records/storyelements/signs/signt.dbr",          // Lokarr's Coat
        "records/storyelements/questassets/q000_torso.dbr", // Gazer Man
        "records/endlessdungeon/items/q001_torso.dbr",      // Miss Gazer Man
    ];

    /// <summary>Why an item is not collectable, or null when it is.</summary>
    public static string? Refuse(string? baseRecord, long stackCount = 1) {
        if (string.IsNullOrWhiteSpace(baseRecord)) return "no base record";

        var record = baseRecord.Replace('\\', '/');

        bool Has(string fragment) => record.Contains(fragment, StringComparison.OrdinalIgnoreCase);

        // Upstream's first test, and the one that catches most junk: a stack is a pile of
        // potions or components rather than an item worth keeping.
        if (stackCount > 1) return "stackable";

        if (Has("/storyelements/")
            && !AllowedStoryElements.Any(a => record.Contains(a, StringComparison.OrdinalIgnoreCase))) {
            return "quest item";
        }

        if (Has("/materia/")) return "component";
        if (record.StartsWith("records/items/misc/", StringComparison.OrdinalIgnoreCase)) return "misc item";
        if (Has("/questitems/")) return "quest item";
        if (Has("/crafting/")) return "crafting material";

        // The salt bag, which upstream singles out because people kept asking about it.
        if (Has("gearaccessories/necklaces/a00_necklace.dbr")) return "salt bag";

        return null;
    }

    public static bool IsCollectable(string? baseRecord, long stackCount = 1) =>
        Refuse(baseRecord, stackCount) is null;
}
