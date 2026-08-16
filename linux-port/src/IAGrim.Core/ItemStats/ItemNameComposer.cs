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

        // The socketed component, which upstream appends in brackets rather than ordering with
        // the rest. Its own UI splits the brackets back off to draw the component separately.
        var materia = string.Empty;
        var entry = tagEntries.FirstOrDefault(row => row.Record == materiaRecord && row.Stat == "description");
        if (entry is not null) {
            materia = $" [{TagName(entry.TextValue) ?? entry.TextValue}]";
        }

        return _combinator.TranslateName(prefix, quality, style, core, suffix) + materia;
    }
}
