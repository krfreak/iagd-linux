namespace IAGrim.Host;

/// <summary>
/// Search criteria — upstream's <c>ItemSearchRequest</c>, field for field.
///
/// The names are upstream's even where they read oddly (<c>PrefixRarity</c> is an affix count,
/// <c>Slot</c> is a list of item classes), so that the two stay diffable when upstream adds a
/// filter. A new field appearing on their side is a new filter to port; check-upstream.sh
/// reports the file, and PORTING.md records what is deliberately absent.
/// </summary>
public sealed record ItemQuery {
    /// <summary>Free text, matched against the item name AND its stat lines.</summary>
    public string? Wildcard { get; init; }

    public bool? IsHardcore { get; init; }

    /// <summary>Null means "any"; empty string means vanilla specifically.</summary>
    public string? Mod { get; init; }

    public int MinimumLevel { get; init; }
    public int MaximumLevel { get; init; }

    /// <summary>Items with a component socketed into them.</summary>
    public bool SocketedOnly { get; init; }

    /// <summary>Items whose base+prefix+suffix combination occurs more than once.</summary>
    public bool DuplicatesOnly { get; init; }

    /// <summary>
    /// Upstream's "Order By Level" checkbox: level first, then name. Off, the order is name then
    /// id. Both ascending — see PlayerItemDaoImpl.SearchForItems.
    /// </summary>
    public bool OrderByLevel { get; init; }

    /// <summary>Looted in the last 12 hours, matching upstream's window.</summary>
    public bool RecentOnly { get; init; }

    /// <summary>
    /// Item classes, e.g. WeaponHunting_Ranged1h. A list because one UI slot can mean several
    /// classes — "two-handed" covers every 2h weapon class.
    /// </summary>
    public IReadOnlyList<string> Slot { get; init; } = [];

    /// <summary>Exclude the listed slots instead of restricting to them.</summary>
    public bool SlotInverse { get; init; }

    /// <summary>
    /// Stat-name groups the item must carry, e.g. the "Fire damage" checkbox expands to every
    /// fire field. Each group is an OR within itself and an AND against the other groups, which
    /// is why this is a list of lists rather than a flat set.
    /// </summary>
    public IReadOnlyList<string[]> Filters { get; init; } = [];

    /// <summary>Items carrying any <c>retaliation*</c> stat.</summary>
    public bool IsRetaliation { get; init; }

    /// <summary>
    /// Class ids an item must grant skill bonuses to, e.g. "class03" for Occultist. Ids rather
    /// than names because that is what upstream's checkboxes carry and what the game data keys
    /// on (records/skills/playerclass03/…).
    /// </summary>
    public IReadOnlyList<string> Classes { get; init; } = [];

    /// <summary>
    /// Scope every other stat filter to the item's *pet* records rather than its own, so
    /// "attack speed" means the pet's. Upstream expresses this by prefixing each stat name
    /// with "pet", which is how those rows are stored.
    /// </summary>
    public bool PetBonuses { get; init; }

    /// <summary>
    /// Plain "grants any pet bonus". Unlike <see cref="PetBonuses"/> this does not rescope the
    /// other filters, so "has a pet bonus AND cold damage on me" is expressible.
    /// </summary>
    public bool HasPetBonus { get; init; }

    /// <summary>
    /// IA's display colour, not the game's classification: Epic, Blue, Green, Yellow, White,
    /// Unknown. See <see cref="IAGrim.Core.ItemStats.ItemRarity"/> — the game's Legendary is
    /// IA's "Epic".
    /// </summary>
    public string? Rarity { get; init; }

    /// <summary>
    /// Minimum number of Rare affixes ("level of green"). Upstream compares with &gt;=, so 1
    /// means "at least one rare affix" and 2 means a double-rare.
    /// </summary>
    public int PrefixRarity { get; init; }

    /// <summary>Items granting a skill that can be placed on the hotbar and triggered.</summary>
    public bool WithGrantSkillsOnly { get; init; }

    /// <summary>Items whose granted skill summons a pet.</summary>
    public bool WithSummonerSkillOnly { get; init; }

    /// <summary>
    /// Numeric filters over seed-applied values, e.g. offensiveBaseFireMin >= 50.
    ///
    /// Upstream sums a *set* of fields per checkbox ("Fire damage" covers flat and modifier
    /// fields together), which is why the filter carries a list rather than one name.
    /// </summary>
    public IReadOnlyList<StatValueFilter> StatFilters { get; init; } = [];
}

/// <param name="Fields">Fields whose values are summed before comparing.</param>
/// <param name="Minimum">Threshold the sum must reach.</param>
public sealed record StatValueFilter(IReadOnlyList<string> Fields, double Minimum) {
    /// <summary>
    /// Parses "field>=value", or "fieldA+fieldB>=value" to sum several fields, which is how
    /// upstream's damage checkboxes behave.
    /// </summary>
    public static StatValueFilter? Parse(string raw) {
        var separator = raw.IndexOf(">=", StringComparison.Ordinal);
        if (separator <= 0) return null;

        var fields = raw[..separator]
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => f.All(c => char.IsLetterOrDigit(c) || c == '_'))   // column-safe
            .ToArray();

        if (fields.Length == 0) return null;
        if (!double.TryParse(raw[(separator + 2)..].Trim(),
                             System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var minimum)) {
            return null;
        }
        return new StatValueFilter(fields, minimum);
    }
}

/// <summary>
/// Builds the WHERE clause.
///
/// Ported from <c>PlayerItemDaoImpl.SearchForItems</c> and its helpers. The SQL fragments keep
/// upstream's wording and order so that diffing the two stays practical when upstream changes
/// the search semantics — which it does whenever Grim Dawn adds item fields.
/// `scripts/check-upstream.sh` reports when that file moves.
///
/// Because this port adopted upstream's schema (see <see cref="IAGrim.Platform.Schema"/>), the
/// fragments are now literally upstream's rather than translations of them — including the
/// record-driven subqueries, which is the whole reason the pet, damage-type and mastery filters
/// could be ported at all. Remaining deviations are marked PORT:.
/// </summary>
internal static class ItemQueryBuilder {
    /// <summary>
    /// Upstream's <c>PetRecordCondition</c>, verbatim.
    ///
    /// A PlayerItemRecord row is a pet record precisely when it is *not* one of the item's own
    /// core records — pet-bonus targets are the only other thing put in that table. The IFNULLs
    /// are load-bearing and upstream says why: affix and materia records are NULL rather than ''
    /// on most items, and <c>x NOT IN (a, b, NULL)</c> is NULL — never true — for every row.
    /// </summary>
    private const string PetRecordCondition = """
        pir.record NOT IN (
            IFNULL(pi2.BaseRecord, ''), IFNULL(pi2.PrefixRecord, ''), IFNULL(pi2.SuffixRecord, ''),
            IFNULL(pi2.MateriaRecord, ''), IFNULL(pi2.AscendantAffixNameRecord, ''), IFNULL(pi2.AscendantAffix2hNameRecord, '')
        )
        """;

    /// <summary>
    /// Upstream's <c>RecordStatSubquery</c>: wraps a condition on a game stat row into a
    /// subquery yielding the matching player item ids.
    ///
    /// Driven from PlayerItemRecord — owned records only — rather than a correlated EXISTS over
    /// every item in the game. Upstream measured that as 20-25x faster on large collections, and
    /// the shape is kept for the same reason.
    /// </summary>
    private static string RecordStatSubquery(string dbsCondition, bool petOnly = false) {
        var petJoin = petOnly ? "JOIN PlayerItem pi2 ON pi2.Id = pir.Playeritemid" : "";
        var petFilter = petOnly ? $"AND {PetRecordCondition}" : "";

        return $"""
            SELECT pir.Playeritemid FROM PlayerItemRecord pir
            {petJoin}
            JOIN databaseitem_v2 db ON db.baserecord = pir.record
            JOIN databaseitemstat_v2 dbs ON dbs.id_databaseitem = db.id_databaseitem AND ({dbsCondition})
            {petFilter}
            """;
    }

    /// <summary>
    /// Stat names arrive from a query string and end up inside an IN list, which SQLite cannot
    /// parameterise. They are restricted to the shape a Grim Dawn field name actually has, and
    /// anything else is dropped rather than escaped — a stat name with a quote in it is a bug or
    /// an attack, never a real search.
    /// </summary>
    private static bool IsSafeIdentifier(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsLetterOrDigit(c) || c is '_');

    private static string QuotedList(IEnumerable<string> values) =>
        string.Join(", ", values.Where(IsSafeIdentifier).Select(v => $"'{v}'"));

    public static (string Where, Dictionary<string, object> Parameters) Build(ItemQuery query) {
        var fragments = new List<string>();
        var parameters = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(query.Wildcard)) {
            // Upstream:
            //   (PI.namelowercase LIKE :name OR R.id IN (SELECT replicaitemid FROM
            //    replicaitemrow WHERE IFNULL(textlowercase, text) LIKE :wildcard))
            //
            // PORT: the trailing clause on the template name is ours. Upstream has no equivalent
            // because it stores the composed name on the item itself; here an item whose tooltip
            // was never captured would otherwise be unsearchable.
            fragments.Add("""
                (p.namelowercase LIKE :name
                 OR r.Id IN (SELECT replicaitemid FROM ReplicaItemRow
                              WHERE IFNULL(TextLowercase, Text) LIKE :wildcard)
                 OR LOWER(IFNULL(COALESCE(tm.Name, tv.Name), '')) LIKE :name)
                """);
            var wildcard = query.Wildcard.Trim().ToLowerInvariant();
            parameters["wildcard"] = $"%{wildcard}%";
            // Upstream turns spaces into wildcards for names, so "mythical revolver" matches.
            parameters["name"] = $"%{wildcard.Replace(' ', '%')}%";
        }

        // Upstream always partitions by mod; null here means "any", which upstream has no
        // equivalent for because its UI always has a mod selected.
        if (query.Mod is not null) {
            if (query.Mod.Length == 0) {
                fragments.Add("(p.Mod IS NULL OR p.Mod = '')");
            } else {
                fragments.Add("LOWER(p.Mod) = LOWER(:mod)");
                parameters["mod"] = query.Mod;
            }
        }

        if (query.IsHardcore is not null) {
            fragments.Add(query.IsHardcore.Value ? "p.IsHardcore" : "NOT p.IsHardcore");
        }

        if (!string.IsNullOrEmpty(query.Rarity)) {
            fragments.Add("p.Rarity = :rarity");
            parameters["rarity"] = query.Rarity;
        }

        if (query.PrefixRarity > 0) {
            fragments.Add("p.PrefixRarity >= :prefixRarity");
            parameters["prefixRarity"] = query.PrefixRarity;
        }

        if (query.SocketedOnly) {
            fragments.Add("p.MateriaRecord is not null and p.MateriaRecord != ''");
        }

        if (query.DuplicatesOnly) {
            // Upstream groups on baserecord||prefixrecord||suffixrecord: two items are
            // "the same" when their records match, regardless of seed. The subquery repeats
            // the mod and hardcore conditions, because a softcore copy is not a duplicate of
            // a hardcore one.
            var hcSc = query.IsHardcore is null ? "1=1"
                     : query.IsHardcore.Value ? "IsHardcore" : "NOT IsHardcore";
            var modCondition = query.Mod is null ? "1=1"
                             : query.Mod.Length == 0 ? "(Mod IS NULL OR Mod = '')"
                             : "LOWER(Mod) = LOWER( :mod )";

            // PORT: the IFNULLs are ours. Upstream concatenates the three records directly, and
            // in SQLite 'x' || NULL is NULL — so every item without affixes collapses into one
            // group keyed NULL, and the subquery returns a single arbitrary base record for the
            // whole lot. That makes upstream's "duplicates only" miss most plain duplicates.
            // Reproducing the bug was the alternative; it is flagged here instead so the
            // difference is visible rather than discovered.
            fragments.Add($"""
                p.baserecord IN (SELECT BaseRecord FROM (
                    SELECT baserecord || IFNULL(PrefixRecord,'') || IFNULL(SuffixRecord,'') AS Records,
                           COUNT(*) AS N, baserecord AS BaseRecord
                    FROM PlayerItem
                    WHERE {modCondition}
                      AND {hcSc}
                    GROUP BY Records
                    HAVING N > 1
                    ORDER BY N DESC
                ))
                """);
        }

        // Upstream's own PlayerItem.LevelRequirement column: the highest level requirement
        // across the item's records, not the base record's alone, since an affix or a socketed
        // component can gate an item above its base. Filled in by the precompute pass, which is
        // why 'iagd stats' has to have run — matching upstream, where the same column is empty
        // until its stat parse runs.
        if (query.MinimumLevel > 0) {
            fragments.Add("p.LevelRequirement >= :minlevel");
            parameters["minlevel"] = query.MinimumLevel;
        }
        if (query.MaximumLevel is > 0 and < 120) {
            fragments.Add("p.LevelRequirement <= :maxlevel");
            parameters["maxlevel"] = query.MaximumLevel;
        }

        if (query.RecentOnly) {
            fragments.Add("p.created_at > :recent");
            parameters["recent"] = DateTimeOffset.UtcNow.AddHours(-12).ToUnixTimeSeconds();
        }

        // Only items which grants new skills.
        //
        // Upstream: PI.baserecord IN (SELECT PlayerItemRecord FROM (ItemSkillDaoImpl.ListItemsQuery) y),
        // which joins itemskill_v2 to itemskill_mapping to DatabaseItem_v2 to PlayerItem purely
        // to get back to the base records that have a skill. Upstream's TODO ("Are there any
        // prefixes or suffixes which grants skills?") is preserved by matching the base record.
        if (query.WithGrantSkillsOnly) {
            fragments.Add("""
                p.baserecord IN (
                    SELECT db.baserecord FROM itemskill_v2 s, itemskill_mapping map, DatabaseItem_v2 db
                     WHERE s.id_skill = map.id_skill
                       AND map.id_databaseitem = db.id_databaseitem)
                """);
        }

        if (query.WithSummonerSkillOnly) {
            // Upstream, verbatim: a granted skill whose own record carries 'spawnObjects'.
            fragments.Add("""
                p.baserecord IN (SELECT p2.baserecord
                    from itemskill_v2 s, itemskill_mapping map, DatabaseItem_v2 db,  playeritem p2, DatabaseItemStat_v2 stat
                    where s.id_skill = map.id_skill
                    and map.id_databaseitem = db.id_databaseitem
                    and db.baserecord = p2.baserecord
                    and stat.id_databaseitem = s.id_databaseitem
                    and stat.stat = 'spawnObjects')
                """);
        }

        // Can be several slots for stuff like "2 Handed".
        if (query.Slot.Count > 0) {
            var classes = QuotedList(query.Slot);
            if (classes.Length > 0) {
                var subQuery = RecordStatSubquery($"dbs.stat = 'Class' AND dbs.TextValue in ( {classes} )");
                fragments.Add($"p.Id {(query.SlotInverse ? "NOT" : "")} IN ({subQuery})");

                // ItemRelic = Components: without this, asking for components returns every item
                // that *has* one socketed rather than the components themselves.
                if (query.Slot.Count == 1 && query.Slot[0] == "ItemRelic") {
                    fragments.Add("p.MateriaRecord = ''");
                }
            }
        }

        foreach (var fragment in RecordStatFragments(query)) {
            fragments.Add($"p.Id IN ({fragment})");
        }

        // Per-checkbox numeric stat filters. Each narrows to items whose pre-computed,
        // seed-applied value for the checkbox's fields (summed) reaches the threshold, read from
        // ComputedItemStat so the seed engine is not replayed here. Items not yet pre-computed
        // have no rows and are correctly excluded.
        //
        // Upstream skips these entirely when PetBonuses is set: the pre-computed values are the
        // player's, not the pet's, so applying them under a pet scope would answer a different
        // question than the one asked.
        if (!query.PetBonuses) {
            var filterIndex = 0;
            foreach (var filter in query.StatFilters) {
                var fields = QuotedList(filter.Fields);
                if (fields.Length == 0) continue;

                var thresholdParam = $"svf_threshold_{filterIndex}";
                fragments.Add($"""
                    p.Id IN (
                        SELECT playeritemid FROM ComputedItemStat
                        WHERE stat IN ({fields})
                        GROUP BY playeritemid
                        HAVING SUM(value) >= :{thresholdParam}
                    )
                    """);
                parameters[thresholdParam] = filter.Minimum;
                filterIndex++;
            }
        }

        var where = fragments.Count == 0 ? "1=1" : string.Join("\n  AND ", fragments);
        return (where, parameters);
    }

    /// <summary>
    /// Upstream's <c>CreateDatabaseStatQueryParams</c>: the filters that ask what an item's
    /// records actually contain, rather than what its rolled numbers are.
    /// </summary>
    private static IEnumerable<string> RecordStatFragments(ItemQuery query) {
        var conditions = new List<string>();

        // Pet-bonus target records store every one of their stats under a "pet"-prefixed name
        // (e.g. "petcharacterAttackSpeedModifier"), so scoping to the pet is a rename, not a
        // different table. StatPrecomputeService applies the same prefix when storing them.
        var petPrefix = query.PetBonuses ? "pet" : "";

        foreach (var filter in query.Filters) {
            var fields = QuotedList(filter.Select(f => petPrefix + f));
            if (fields.Length == 0) continue;
            conditions.Add($"dbs.stat in ( {fields} )");
        }

        if (query.IsRetaliation) {
            // Upstream matches every "retaliation*" stat with a range rather than LIKE, and says
            // why: SQLite's LIKE is case-insensitive by default and cannot use the index on
            // DatabaseItemStat_v2.Stat, so the LIKE form scanned the whole stat table. The upper
            // bound is the prefix with its last character incremented.
            var prefix = $"{petPrefix}retaliation";
            var upper = prefix[..^1] + (char)(prefix[^1] + 1);
            conditions.Add($"(dbs.stat >= '{prefix}' AND dbs.stat < '{upper}')");
        }

        foreach (var desiredClass in query.Classes) {
            // Values are class ids ("class03"), which is what upstream's checkboxes are keyed by.
            if (!IsSafeIdentifier(desiredClass)) continue;

            // Upstream's own fields and its exact test: the class filter compares against the
            // synthesised augmentSkill{i}Extras and augmentMastery{i} rows, whose TextValue is a
            // class id.
            //
            // This port used to match the *record path* of augmentSkillName instead, on the
            // mistaken grounds that upstream's fields were absent — they are absent from the raw
            // game data and created during parsing, which is where that reading went wrong. The
            // path match also answered a different question: it matched any item merely
            // referencing a skill of that mastery, including items that only modify one, so
            // filtering by Occultist returned hundreds of items with no Occultist line on them.
            var classStats = QuotedList(new[] {
                "augmentSkill1Extras", "augmentSkill2Extras", "augmentSkill3Extras", "augmentSkill4Extras",
                "augmentMastery1", "augmentMastery2", "augmentMastery3", "augmentMastery4",
            }.Select(f => petPrefix + f));

            conditions.Add($"dbs.stat IN ({classStats}) AND dbs.TextValue = '{desiredClass}'");
        }

        foreach (var condition in conditions) {
            yield return RecordStatSubquery(condition, query.PetBonuses);
        }

        // Legacy "has a pet bonus": the item's own records carry a petBonusName stat. Never
        // pet-scoped, so it combines with ordinary filters — "has a pet bonus AND cold damage
        // on me" is a question upstream can ask, and so can this.
        if (query.HasPetBonus) {
            yield return RecordStatSubquery("dbs.stat = 'petBonusName'");
        }

        // Pet scope with nothing else selected degrades to "has any pet record at all", which is
        // the plain "has a pet bonus" meaning.
        if (query.PetBonuses && conditions.Count == 0) {
            yield return $"""
                SELECT pir.Playeritemid FROM PlayerItemRecord pir
                JOIN PlayerItem pi2 ON pi2.Id = pir.Playeritemid
                WHERE {PetRecordCondition}
                """;
        }
    }
}
