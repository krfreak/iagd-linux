namespace IAGrim.Host;

/// <summary>
/// The wire shapes the UI sees.
///
/// Kept separate from the storage records on purpose. The database rows follow upstream's
/// column names so a future migration to their NHibernate mappings stays a schema match;
/// the UI should not inherit that constraint, and neither layer should have to change when
/// the other does.
/// </summary>
public sealed record ItemSummary(
    long Id,
    string Name,
    string BaseRecord,
    long Seed,
    string? ItemClass,
    string? Quality,
    int Level,
    string? Icon,
    bool IsHardcore,
    /// <summary>IA's display colour ("Epic" is the game's Legendary), null until analysed.</summary>
    string? Rarity,
    /// <summary>Count of Rare affixes, not a rarity. See ItemRarity.</summary>
    int PrefixRarity,
    /// <summary>How many are in the stack; 1 for anything that does not stack.</summary>
    long StackCount);

/// <summary>
/// One tooltip line, in whichever of upstream's two shapes it has.
///
/// A line Grim Dawn drew carries the row type it gave it, and upstream colours it from that type
/// alone (ReplicaStat.css). A line this port computed has no such type: upstream renders those
/// through ItemStat.tsx, which splits each into a leading value and the rest and colours the
/// halves differently depending on which of its three lists the line is in.
/// </summary>
/// <param name="Section">"header", "body" or "pet" for a computed line; null for a captured one.</param>
/// <param name="Modifier">The leading value, e.g. "+162%". Null on a captured line.</param>
/// <param name="Skill">A skill the line modifies, drawn apart from the label in its own colour.</param>
/// <param name="Extras">That skill's tooltip, e.g. "Tier 3 Occultist".</param>
public sealed record ItemStatLine(int TextClass, string Text, string? Section = null,
                                  string? Modifier = null, string? Label = null,
                                  string? Skill = null, string? Extras = null);

/// <summary>
/// A skill an item grants. Upstream shows this on the item and offers a "Hide Skills" toggle to
/// suppress it; this port had the data (itemskill_v2, populated by the parse) and the filters
/// that use it, but never displayed it.
/// </summary>
/// <param name="Trigger">
/// The controller record that fires the skill, when it is a proc rather than something you put
/// on the action bar. Present means "happens by itself" — which is why upstream's
/// grants-skill filter returns proc items too, despite its wording.
/// </param>
public sealed record ItemSkillInfo(string? Name, string? Description, long Level,
                                   string? Trigger, bool SummonsPets);

public sealed record ItemDetail(ItemSummary Item, IReadOnlyList<ItemStatLine> Stats,
                                ItemSkillInfo? Skill);

/// <summary>
/// One card in the item list: an item, everything needed to render it, and how many identical
/// copies it stands for.
/// </summary>
/// <param name="Copies">
/// How many rows merged into this card — upstream's "Transfer all (N)". One means the card is
/// exactly one item.
/// </param>
/// <param name="Duplicates">The row ids behind it, the first being <c>Item.Id</c>.</param>
public sealed record ItemCard(ItemSummary Item, IReadOnlyList<ItemStatLine> Stats,
                              ItemSkillInfo? Skill, int Copies, IReadOnlyList<long> Duplicates);

/// <param name="Total">Cards matching, which is what paging walks.</param>
/// <param name="TotalItems">
/// Items matching. Identical items share a card, so this is the larger number and the one worth
/// showing: upstream's status bar reports the same thing (NumTotalItems, a COUNT over rows).
/// </param>
public sealed record ItemPage(IReadOnlyList<ItemCard> Items, int Total, int TotalItems,
                              int Skip, int Take);

/// <summary>What the UI needs to explain itself when something is not working.</summary>
public sealed record HostStatus(
    bool GameRunning,
    DateTime? GameStartedAt,
    bool HookAttached,
    int PendingLootFiles,
    int ItemCount,
    int TemplateCount,
    string? GameDir,
    string BridgeDir,
    string DatabaseFile,
    /// <summary>True while Grim Dawn's data is being read.</summary>
    bool ParsingGameData,
    /// <summary>What that parse is doing, for the status line.</summary>
    string? ParseStep,

    /// <summary>
    /// True while the collection is being analysed — the pass that fills in rarity, level and
    /// the rolled values, and that writes the game stat rows the record-driven filters read.
    ///
    /// Separate from <see cref="ItemsNeedingStats"/>, which counts *items* waiting. A pass can
    /// be running with that count at zero: a re-parse or a change to what the pass writes
    /// invalidates the rows for a collection whose items are all described.
    /// </summary>
    bool Analysing,
    /// <summary>What that pass is doing, for the status line.</summary>
    string? AnalysisStep,

    /// <summary>
    /// Items with no rarity yet, i.e. never seen by the precompute pass. Surfaced because the
    /// rarity and level filters read columns that pass fills in: without it, those filters
    /// return nothing at all and look broken rather than unpopulated. Upstream shows the same
    /// count for the same reason.
    /// </summary>
    int ItemsNeedingStats,
    /// <summary>
    /// Why the parsed game data is out of date, or null when current. A Grim Dawn patch or a
    /// language change invalidates every name, level and icon without anything failing.
    /// </summary>
    string? GameDataStale,
    /// <summary>Set while an attach attempt is in progress, so the UI can say so.</summary>
    bool Attaching);

/// <param name="TargetMod">
/// Send to a different mod's stash than the item was looted from. Honoured only when the
/// "transfer to any mod" setting is on, matching upstream, where the same choice comes from a
/// stash picker shown only when that setting is enabled.
/// </param>
/// <param name="TargetHardcore">Send to the hardcore or softcore branch, as above.</param>
/// <param name="GameDir">Which installation to read, or null for the configured one.</param>
public sealed record ParseRequest(string? GameDir = null);

/// <summary>A link the Support page asks the host to open. Only <see cref="SupportLinks"/>.</summary>
public sealed record OpenRequest(string? Url = null);

/// <summary>
/// Where this port sends anyone who wants to support the work.
///
/// Item Assistant is Marius Andersen's; this repository is an unaffiliated Linux port of it, and
/// the person who did the porting is not the person who wrote the tool. The Support page says so
/// and points at the original, which is the only honest arrangement — and the reason these are
/// the *only* URLs this project will open.
///
/// The Discord is deliberately not among them. Sending a Linux port's users to upstream's
/// community puts support requests for code its maintainer did not write in front of him, which
/// a page called Support would make more likely rather than less.
/// </summary>
public static class SupportLinks {
    public const string Website = "https://grimdawn.evilsoft.net";
    public const string Source = "https://github.com/marius00/iagd";
    public const string Patreon = "https://www.patreon.com/itemassistant";

    private static readonly HashSet<string> Allowed = [Website, Source, Patreon];

    public static bool Contains(string url) => Allowed.Contains(url);
}

/// <param name="DryRun">Report what would happen without writing anything.</param>
public sealed record MergeRequest(string? Path = null, bool DryRun = true);

/// <param name="Directory">Choose a folder rather than a file.</param>
public sealed record BrowseRequest(bool Directory = false, string? Title = null, string? Path = null);

public sealed record TransferRequest(int TimeoutSeconds = 120, bool Keep = false,
                                     string? TargetMod = null, bool? TargetHardcore = null);

public sealed record TransferResult(bool Collected, string Message, string? QueuedPath);

/// <summary>
/// Pushed over the WebSocket. Mirrors upstream's IOMessageType in spirit: a small closed
/// set of event kinds the UI switches on.
/// </summary>
public sealed record HostEvent(string Type, object? Data = null) {
    public static HostEvent Looted(ItemCard item) => new("itemLooted", item);
    public static HostEvent Removed(long id) => new("itemRemoved", new { id });
    public static HostEvent Status(HostStatus status) => new("status", status);
    /// <param name="stage">"merge" while rows are read, "stats" during the pass that follows.</param>
    public static HostEvent MergeProgress(int done, int total, int imported,
                                          string stage = "merge", string? message = null) =>
        new("mergeProgress", new { done, total, imported, stage, message });
    public static HostEvent Message(string text, string level = "info") =>
        new("message", new { text, level });
}
