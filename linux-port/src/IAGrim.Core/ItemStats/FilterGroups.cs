namespace IAGrim.Core.ItemStats;

/// <summary>One filter checkbox: a label and the stat fields it matches (any of them).</summary>
public sealed record FilterGroup(string Label, string[] Fields);

/// <summary>
/// The stat fields behind each filter checkbox, ported from upstream's filter controls —
/// <c>UI/Filters/Damage.cs</c>, <c>DamageOverTime.cs</c>, <c>Resistances.cs</c>, <c>Misc.cs</c>.
///
/// These lists are the filters. A checkbox labelled "Fire" is nothing but the set
/// <c>{offensiveFire, offensiveFireModifier, offensiveElemental, offensiveElementalModifier}</c>,
/// and getting the set wrong gives an answer that looks plausible and is not upstream's.
///
/// An earlier version of this port invented these from the shape of the stat names —
/// <c>offensiveFireMin</c>, <c>offensiveBaseFireMax</c> and so on. Every one of those is a real
/// Grim Dawn field, which is what made the guess convincing; none of them is what upstream
/// searches. <c>scripts/verify-filter-groups.sh</c> now checks these against upstream's source
/// so the same mistake cannot be made twice.
/// </summary>
public static class FilterGroups {
    /// <summary>
    /// Damage types. Fire, Cold and Lightning also carry the Elemental fields, because Grim Dawn
    /// expresses "elemental damage" as its own stat rather than as a sum of the three — an item
    /// with +15% Elemental would otherwise not appear under Fire.
    ///
    /// The names are the game's, not the UI's: Acid is <c>Poison</c> internally and Vitality is
    /// <c>Life</c>.
    /// </summary>
    public static IReadOnlyList<FilterGroup> Damage { get; } = BuildDamage();

    private static FilterGroup[] BuildDamage() {
        var types = new (string Label, string Field)[] {
            ("Physical", "Physical"),
            ("Pierce", "Pierce"),
            ("Fire", "Fire"),
            ("Cold", "Cold"),
            ("Lightning", "Lightning"),
            ("Aether", "Aether"),
            ("Vitality", "Life"),
            ("Chaos", "Chaos"),
            ("Acid", "Poison"),
            ("Elemental", "Elemental"),
        };

        var groups = new List<FilterGroup> {
            new("Total damage", ["offensiveTotalDamageModifier"]),
        };

        foreach (var (label, field) in types) {
            var isElemental = field is "Fire" or "Cold" or "Lightning";
            groups.Add(new FilterGroup(label, isElemental
                ? [$"offensive{field}", $"offensive{field}Modifier",
                   "offensiveElemental", "offensiveElementalModifier"]
                : [$"offensive{field}", $"offensive{field}Modifier"]));
        }

        return groups.ToArray();
    }

    /// <summary>
    /// Damage over time. Each type spans eight fields — the offensive value, its modifier,
    /// chance and duration, and the four retaliation equivalents — because a DoT can arrive
    /// either way and a player looking for "burn damage" means both.
    /// </summary>
    public static IReadOnlyList<FilterGroup> DamageOverTime { get; } = BuildDot();

    private static FilterGroup[] BuildDot() {
        var types = new (string Label, string Field)[] {
            ("Bleeding", "Bleeding"),
            ("Trauma", "Physical"),
            ("Burn", "Fire"),
            ("Electrocute", "Lightning"),
            ("Vitality decay", "Life"),
            ("Frostburn", "Cold"),
            ("Poison", "Poison"),
        };

        var groups = types.Select(t => new FilterGroup(t.Label, [
            $"offensiveSlow{t.Field}",
            $"offensiveSlow{t.Field}Modifier",
            $"offensiveSlow{t.Field}ModifierChance",
            $"offensiveSlow{t.Field}DurationModifier",
            $"retaliationSlow{t.Field}Min",
            $"retaliationSlow{t.Field}Chance",
            $"retaliationSlow{t.Field}Duration",
            $"retaliationSlow{t.Field}DurationMin",
        ])).ToList();

        // Upstream's spelling, "Leach" and all — it is a field name in the game data, not a word.
        groups.Add(new FilterGroup("Life leech", ["offensiveLifeLeechMin", "offensiveSlowLifeLeachMin"]));
        return groups.ToArray();
    }

    /// <summary>Resistances.</summary>
    public static IReadOnlyList<FilterGroup> Resistances { get; } = BuildResistances();

    private static FilterGroup[] BuildResistances() {
        var types = new (string Label, string Field)[] {
            ("Physical", "Physical"),
            ("Pierce", "Pierce"),
            ("Fire", "Fire"),
            ("Cold", "Cold"),
            ("Lightning", "Lightning"),
            ("Aether", "Aether"),
            ("Vitality", "Life"),
            ("Chaos", "Chaos"),
            ("Poison", "Poison"),
            ("Bleeding", "Bleeding"),
            ("Stun", "Stun"),
        };

        var groups = new List<FilterGroup> {
            new("Elemental", ["defensiveElementalResistance"]),
        };

        groups.AddRange(types.Select(t => new FilterGroup(t.Label, [
            $"defensive{t.Field}",
            $"defensive{t.Field}Modifier",
            $"defensiveSlow{t.Field}",
            $"defensiveSlow{t.Field}Modifier",
        ])));

        groups.Add(new FilterGroup("Slow", ["defensiveTotalSpeedResistance"]));
        return groups.ToArray();
    }

    /// <summary>
    /// The fields that carry "+N to a class's skills".
    ///
    /// **This does not match upstream, and upstream is wrong.** Upstream's class filter looks for
    /// <c>augmentSkill1Extras…augmentSkill4Extras</c> and <c>augmentMastery1…augmentMastery4</c>,
    /// and compares their text to <c>class03</c>. Measured against this installation, scanning
    /// all 4.8 million stat rows: **those eight field names occur zero times.** The fields that
    /// exist are <c>augmentMasteryName1..3</c> and <c>augmentSkillName1..5</c>, and they hold a
    /// record path (<c>records/skills/playerclass03/_classtraining_class03.dbr</c>) rather than a
    /// class id. So upstream's mastery filter cannot match anything, whatever is ticked.
    ///
    /// Reproducing that would mean shipping a filter that silently returns nothing. This one
    /// searches the real fields and matches the class through the record path — see
    /// <c>ItemQueryBuilder</c>, where the comparison is a path match rather than equality.
    /// </summary>
    /// <remarks>
    /// Declared before <see cref="Misc"/> deliberately: static field initialisers run in
    /// declaration order, so a Misc entry referring to this while it sits further down the file
    /// captures null rather than the array — silently, at runtime.
    /// </remarks>
    public static readonly string[] ClassFields = [
        "augmentMasteryName1", "augmentMasteryName2", "augmentMasteryName3",
        "augmentSkillName1", "augmentSkillName2", "augmentSkillName3",
        "augmentSkillName4", "augmentSkillName5",
    ];

    /// <summary>Everything else upstream's Misc panel offers as a stat filter.</summary>
    public static IReadOnlyList<FilterGroup> Misc { get; } = [
        new("Set bonus", ["setName", "itemSetName"]),
        new("Shield stats", ["blockAbsorption", "defensiveBlock", "defensiveBlockChance",
                             "defensiveBlockModifier", "defensiveBlockAmountModifier"]),
        new("Attack speed", ["characterAttackSpeedModifier", "characterAttackSpeed",
                             "characterTotalSpeedModifier"]),
        new("Cast speed", ["characterSpellCastSpeedModifier", "characterTotalSpeedModifier"]),
        new("Armor", ["defensiveProtectionModifier"]),
        new("Run speed", ["characterRunSpeedModifier", "characterTotalSpeedModifier"]),
        new("Experience", ["characterIncreasedExperience"]),
        new("Reflect", ["defensiveReflect"]),
        new("Health", ["characterLifeModifier", "characterLife"]),
        new("Defensive ability", ["characterDefensiveAbilityModifier", "characterDefensiveAbility"]),
        new("Offensive ability", ["characterOffensiveAbility", "characterOffensiveAbilityModifier"]),
        new("Energy regen", ["characterManaRegen", "characterManaRegenModifier"]),
        new("Weapon life leech", ["offensiveLifeLeechMin"]),
        new("Damage conversion", ["conversionPercentage"]),
        new("Cooldown reduction", ["skillCooldownReduction"]),
        // PORT: upstream's "mastery skills" checkbox searches augmentMastery1/2, which do not
        // exist in the game data — see ClassFields below. Same fix, same reason.
        new("Mastery skills", ClassFields),
        new("Physique", ["characterStrength", "characterStrengthModifier"]),
        new("Spirit", ["characterIntelligence", "characterIntelligenceModifier"]),
        new("Cunning", ["characterDexterity", "characterDexterityModifier"]),
    ];

}
