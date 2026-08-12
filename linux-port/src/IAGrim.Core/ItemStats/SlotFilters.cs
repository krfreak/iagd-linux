namespace IAGrim.Core.ItemStats;

/// <summary>One entry of the slot dropdown: a label and the item classes it selects.</summary>
/// <param name="Inverse">
/// True only for "Other", which is defined as everything the named slots do not cover. Upstream
/// builds it by concatenating every other entry's filter and inverting the match, so a new item
/// class introduced by a game patch lands in "Other" without anyone editing this table.
/// </param>
public sealed record SlotFilter(string Tag, string Label, IReadOnlyList<string> ItemClasses, bool Inverse = false);

/// <summary>One entry of the rarity dropdown.</summary>
/// <param name="PrefixRarity">
/// Not a rarity: the number of Rare affixes an item carries. Upstream's dropdown offers Green,
/// "Green (1 rare affix)" and "Green (2 rare affixes)" as three entries that share a rarity and
/// differ only in this. See <see cref="ItemRarity"/>.
/// </param>
public sealed record QualityFilter(string Tag, string Label, string? Rarity, int PrefixRarity = 0);

/// <summary>
/// The two dropdowns above the item list, ported from upstream's <c>UIHelper</c>.
///
/// These are data rather than logic, and they are the kind of data that goes stale invisibly: a
/// wrong item class here does not fail, it just quietly stops matching a slot. <c>make verify</c>
/// re-extracts both tables from the pinned upstream checkout and fails on any difference.
///
/// The labels are upstream's English strings, keyed by the same translation tag so a language
/// pack can replace them.
/// </summary>
public static class SlotFilters {
    /// <summary>Slot options, in upstream's order. "Any" is first and selects everything.</summary>
    public static readonly IReadOnlyList<SlotFilter> Slots = BuildSlots();

    /// <summary>Rarity options, in upstream's order.</summary>
    public static readonly IReadOnlyList<QualityFilter> Qualities = [
        // Upstream's labels are the colours, not the game's rarity names — "Epic" here is what
        // the game calls Legendary. Kept as they are: a user reading our dropdown next to theirs
        // must see the same words.
        new("iatag_rarity_any",      "Any",            null),
        new("iatag_rarity_yellow",   "Yellow",         "Yellow"),
        new("iatag_rarity_green",    "Green",          "Green"),
        new("iatag_rarity_green_p1", "Green (Rare)",   "Green", 1),
        new("iatag_rarity_green_p2", "Green (Double)", "Green", 2),
        new("iatag_rarity_blue",     "Blue",           "Blue"),
        new("iatag_rarity_epic",     "Epic",           "Epic"),
    ];

    private static IReadOnlyList<SlotFilter> BuildSlots() {
        List<SlotFilter> slots = [
            new("iatag_slot_any",   "Any",   []),
            new("iatag_slot_armor", "Armor", [
                "ArmorProtective_Head", "ArmorProtective_Hands", "ArmorProtective_Feet",
                "ArmorProtective_Legs", "ArmorProtective_Chest", "ArmorProtective_Waist",
                "ArmorJewelry_Medal", "ArmorJewelry_Ring", "ArmorProtective_Shoulders",
                "ArmorJewelry_Amulet",
            ]),
            new("iatag_slot_head",     "Head",     ["ArmorProtective_Head"]),
            new("iatag_slot_hands",    "Hands",    ["ArmorProtective_Hands"]),
            new("iatag_slot_feet",     "Feet",     ["ArmorProtective_Feet"]),
            new("iatag_slot_legs",     "Legs",     ["ArmorProtective_Legs"]),
            new("iatag_slot_chest",    "Chest",    ["ArmorProtective_Chest"]),
            new("iatag_slot_belt",     "Belt",     ["ArmorProtective_Waist"]),
            new("iatag_slot_medal",    "Medal",    ["ArmorJewelry_Medal"]),
            new("iatag_slot_ring",     "Ring",     ["ArmorJewelry_Ring"]),
            new("iatag_slot_shoulder", "Shoulder", ["ArmorProtective_Shoulders"]),
            new("iatag_slot_neck",     "Amulet/Neck", ["ArmorJewelry_Amulet"]),
            new("iatag_slot_weapon1h", "Weapon (1h)", [
                "WeaponMelee_Dagger", "WeaponMelee_Mace", "WeaponMelee_Axe",
                "WeaponMelee_Scepter", "WeaponMelee_Sword",
            ]),
            new("iatag_slot_weapon2h", "Weapon (2h)", [
                "WeaponMelee_Sword2h", "WeaponMelee_Mace2h", "WeaponMelee_Axe2h",
                "WeaponMelee_Spear2h",
            ]),
            new("iatag_slot_weaponranged1h", "Weapon (Ranged 1h)", ["WeaponHunting_Ranged1h"]),
            new("iatag_slot_weaponranged2h", "Weapon (Ranged 2h)", ["WeaponHunting_Ranged2h"]),
            new("iatag_slot_offhand", "Offhand", ["WeaponArmor_Offhand"]),
            new("iatag_slot_shield",  "Shield",  ["WeaponArmor_Shield"]),
            new("iatag_slot_relic",   "Relic",   ["ItemArtifact"]),
        ];

        // "Other" is the inverse of every class named above, built the same way upstream builds
        // it so the two cannot disagree — including the duplicate entries, since "Armor" repeats
        // the classes its individual slots also list.
        var everythingNamed = slots.SelectMany(s => s.ItemClasses).ToList();
        slots.Add(new SlotFilter("iatag_slot_other", "Other", everythingNamed, Inverse: true));

        return slots;
    }
}
