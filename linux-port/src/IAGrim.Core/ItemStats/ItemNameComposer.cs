using IAGrim.Core.ItemStats.Dto;
using Microsoft.Data.Sqlite;
using StatTranslator;

namespace IAGrim.Core.ItemStats;

/// <summary>
/// An item's name, composed from the game's own tags — upstream's
/// <c>ItemOperationsUtility.GetItemName</c>.
///
/// **Why the name is computed rather than kept.** A name arrives with an item in as many forms
/// as there are ways for an item to arrive. The hook hands over the tooltip line the game drew,
/// which carries the game's display markers: a set item reads <c>(S) ^BLokarr's Coat</c>, where
/// <c>(S)</c> says "part of a set" and <c>^B</c> is a colour code. The online backup hands over
/// whatever name the machine that uploaded the item stored, which for a collection built by an
/// older client is that same tooltip text with an older marker (<c>{}</c> where the current game
/// writes <c>^B</c>). A merged collection hands over a third. Store any of those and one
/// collection ends up holding "Lokarr's Coat", "(S) Lokarr's Coat" and "(S) {}Lokarr's Coat"
/// as three different items in the same comparison view — which is exactly what happened here.
///
/// Upstream never has that problem, because it never keeps the name it was given: every item it
/// stores has its name recomputed from <c>itemNameTag</c>/<c>itemQualityTag</c>/
/// <c>itemStyleTag</c>/<c>lootRandomizerName</c> and the socketed component's description,
/// ordered by the game's own <c>tagItemNameOrder</c>. Two items made of the same records get the
/// same name, whatever route they took to get here, and the tooltip markers never enter the
/// column at all.
///
/// The ordering and the gender handling are upstream's code, not a reimplementation — see
/// <see cref="ItemNameCombinator"/> in the pinned submodule, which this project already
/// references for the same reason elsewhere.
/// </summary>
public sealed class ItemNameComposer {
    /// <summary>
    /// The stat fields a name is made of — upstream's <c>desiredTagsNames</c>. Every one of them
    /// is on <see cref="StatFilter"/>'s whitelist, so they survive the filter that the rows are
    /// read through everywhere else and no separate read is needed.
    /// </summary>
    public static readonly string[] NameStats = [
        "lootRandomizerName", "itemNameTag", "itemQualityTag", "itemStyleTag", "description",
    ];

    /// <summary>Grim Dawn's per-language ordering of prefix, quality, style, name and suffix.</summary>
    private const string OrderTag = "tagItemNameOrder";

    private readonly Dictionary<string, string> _tags;
    private readonly ItemNameCombinator _combinator;

    /// <param name="tags">Grim Dawn's tag table, as <c>iagd parse</c> stored it.</param>
    public ItemNameComposer(Dictionary<string, string> tags) {
        _tags = tags;

        // Upstream's ThirdPartyLanguage: the game states the order per language — German genders
        // the prefix to agree with the item name where English simply concatenates — and falls
        // back to the English ordering when the tag is absent.
        var order = tags.TryGetValue(OrderTag, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : EnglishLanguage.ItemNameOrderFallback;

        _combinator = new ItemNameCombinator(order);
    }

    /// <summary>
    /// Reads the tag table from a collection. Null when the game data has not been parsed yet,
    /// which is the one state in which no name can be composed and the stored one has to do.
    /// </summary>
    public static ItemNameComposer? Load(SqliteConnection connection) {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Tag, Name FROM ItemTag;";
        try {
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1)) tags[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch (SqliteException) { return null; }   // not parsed yet

        return tags.Count == 0 ? null : new ItemNameComposer(tags);
    }

    /// <summary>
    /// The name for one item, or the empty string when its records say nothing about what it is
    /// called — an unparsed mod record, or a quest item the game names nowhere.
    ///
    /// <paramref name="stats"/> is the same record → rows map the rarity and seed passes use.
    /// </summary>
    public string Compose(IReadOnlyDictionary<string, List<DBStatRow>> stats,
                          string? baseRecord, string? prefixRecord,
                          string? suffixRecord, string? materiaRecord) {
        var (core, name) = ComposeParts(stats, baseRecord, prefixRecord, suffixRecord, materiaRecord);

        // The base record is what an item is *called*. The affixes, the quality and the style
        // only decorate that name, and the component is not part of it at all. When the base
        // record is not in the parsed game data its affixes still resolve — an affix is a
        // vanilla record shared across mods, and a modded base is not — so what comes out is the
        // decoration on its own: a magical item the game named "Ancient Warmaul of Ruin" was
        // stored as "of Ruin", and one carrying a prefix instead as "Mighty".
        //
        // That is the case the component check in ComposeParts already covers, reached by the
        // other half of the same item, and it takes the same answer. "" is how this class says
        // the game data cannot name this item, and every caller reads it as "keep the name it
        // has" — their IFNULL, and the empty check in ItemNameRefresh.
        //
        // It costs nothing on a described item: across the 38,156 records of a vanilla + Reign
        // of Terror + D2 + Owlheart installation, no record carries a quality or style tag
        // without also carrying a name tag, so an empty core means an unnameable base record
        // rather than an item this drops a legitimate name for.
        return core.Length == 0 ? string.Empty : name;
    }

    /// <summary>
    /// What an item's decoration composes to on its own, and the empty string for an item whose
    /// base record *is* named — which is every healthy item.
    ///
    /// This is the name <see cref="Compose"/> returned before it required a core, so it is also
    /// the name already sitting in collections written by that version, by the Windows tool, and
    /// by any other client sharing the flaw. It exists so <see cref="ItemNameRefresh"/> can
    /// recognise one when it arrives — through a merge, through the online backup, or through an
    /// upgrade — rather than only avoiding writing new ones.
    /// </summary>
    public string AffixOnlyName(IReadOnlyDictionary<string, List<DBStatRow>> stats,
                                string? baseRecord, string? prefixRecord,
                                string? suffixRecord, string? materiaRecord) {
        var (core, name) = ComposeParts(stats, baseRecord, prefixRecord, suffixRecord, materiaRecord);
        return core.Length == 0 ? name : string.Empty;
    }

    /// <summary>
    /// Upstream's <c>GetItemName</c>, unchanged, plus the core it was built around so the callers
    /// above can tell "the game data named this" from "the game data only decorated it". Both go
    /// through here so the two answers cannot drift apart.
    /// </summary>
    private (string Core, string Name) ComposeParts(
                          IReadOnlyDictionary<string, List<DBStatRow>> stats,
                          string? baseRecord, string? prefixRecord,
                          string? suffixRecord, string? materiaRecord) {
        // Upstream's GetItemName, step for step. The records are looked at in its order, and a
        // tag is matched to the record it came from rather than to the first record carrying it:
        // base and affix records both have a name field, and taking either would name a green
        // after its prefix.
        var records = new[] { prefixRecord, baseRecord, suffixRecord, materiaRecord };

        var tagEntries = records
            .Where(record => !string.IsNullOrEmpty(record))
            .Where(record => stats.ContainsKey(record!))
            .SelectMany(record => stats[record!].Where(row => row.Stat is not null && NameStats.Contains(row.Stat)))
            .ToList();

        // A tag that resolves to nothing is kept as its own id rather than dropped: upstream
        // does that, and an unresolved id at least says which record is unnamed.
        string? TagName(string? tag) => tag is not null && _tags.TryGetValue(tag, out var name) ? name : null;

        string Resolve(string? record, string stat) {
            var entry = tagEntries.FirstOrDefault(row => row.Record == record && row.Stat == stat);
            return entry is null ? string.Empty : TagName(entry.TextValue) ?? entry.TextValue ?? string.Empty;
        }

        var prefix = Resolve(prefixRecord, "lootRandomizerName");
        var suffix = Resolve(suffixRecord, "lootRandomizerName");

        // Potions and other consumables have no itemNameTag; upstream falls back to description.
        var core = Resolve(baseRecord, "itemNameTag");
        if (core.Length == 0) core = Resolve(baseRecord, "description");

        var quality = Resolve(baseRecord, "itemQualityTag");
        var style = Resolve(baseRecord, "itemStyleTag");

        var name = _combinator.TranslateName(prefix, quality, style, core, suffix);

        // A component decorates a name; it is not one. Upstream appends it unconditionally and
        // stores whatever comes out, but this port keeps the stored name when the game data can
        // name nothing (see the callers' IFNULL and the empty-string check in ItemNameRefresh),
        // and "" is the only way to say so. Appending to an empty name says the opposite: an
        // item whose base record is unparsed still has a socketed *vanilla* component, which is
        // parsed, so the bracket resolved when nothing else did and "Modded Blade" was rewritten
        // to " [Antivenom Salve]" — the component's name in place of the item's.
        if (string.IsNullOrWhiteSpace(name)) return (core, string.Empty);

        // The socketed component, which upstream appends in brackets rather than ordering with
        // the rest. Its own UI splits the brackets back off to draw the component separately.
        var entry = tagEntries.FirstOrDefault(row => row.Record == materiaRecord && row.Stat == "description");

        return (core, entry is null ? name : $"{name} [{TagName(entry.TextValue) ?? entry.TextValue}]");
    }
}
