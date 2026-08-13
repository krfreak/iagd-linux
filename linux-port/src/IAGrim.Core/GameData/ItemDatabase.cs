using DataAccess;
using IAGrim.Parser.Arc;
using IAGrim.Parser.Arz;

namespace IAGrim.Core.GameData;

/// <summary>An item template as Grim Dawn defines it, before any roll is applied.</summary>
public sealed record ItemTemplate {
    /// <summary>The .dbr path, which is what a looted item's BaseRecord points at.</summary>
    public required string Record { get; init; }

    /// <summary>Resolved display name, or null when the tag has no translation.</summary>
    public string? Name { get; init; }

    /// <summary>Tag the record refers to, kept for diagnosing unresolved names.</summary>
    public string? NameTag { get; init; }

    public string? Quality { get; init; }
    public string? ItemClass { get; init; }

    /// <summary>
    /// Grim Dawn's own <c>itemClassification</c> (Legendary/Epic/Rare/Magical/Common), stored
    /// unfranslated. Upstream's collection view selects on exactly this raw value, whereas the
    /// per-item display colour is <see cref="ItemStats.ItemRarity"/>'s remapping of it — the
    /// two must not be confused, since IA's "Epic" is the game's "Legendary".
    /// </summary>
    public string? Classification { get; init; }

    /// <summary>
    /// The set record this item belongs to (<c>itemSetName</c>), which is itself a record whose
    /// <c>setName</c> tag gives the set's display name.
    /// </summary>
    public string? SetRecord { get; init; }

    /// <summary>Set display name, when this record *is* a set (<c>setName</c> resolved).</summary>
    public string? SetName { get; init; }

    /// <summary>Icon file referenced by the record; matches what DdsIconExtractor writes.</summary>
    public string? Bitmap { get; init; }

    public int LevelRequirement { get; init; }
}

/// <summary>
/// Reads Grim Dawn's own item definitions.
///
/// This is what makes the collection more than a loot log: a looted item carries only a
/// record path and a seed, so without the game's database there is no name, no class, and
/// no icon for anything the hook did not capture stats for.
///
/// Uses upstream's <c>Parser</c> project directly — it is referenced rather than forked
/// precisely because this is where Grim Dawn format changes land.
/// </summary>
public static class ItemDatabase {
    /// <summary>
    /// Record fields carrying the display name, in the order Grim Dawn resolves them.
    /// Artifacts (relics) and formulae name themselves differently from equipment.
    /// </summary>
    private static readonly string[] NameFields = [
        "itemNameTag", "description", "itemSetName", "relicName", "blueprintNameTag",
    ];

    /// <summary>
    /// Content folders that layer over the base game, in ascending priority — base, then gdx1,
    /// gdx2, … — because later definitions of the same record win, matching the game engine's
    /// newest-expansion-wins behaviour.
    ///
    /// Ported from upstream's <c>GrimFolderUtility.GetGrimExpansionFolders</c>, including its
    /// range: gdx1-9 and survivalmode1-9 rather than the three expansions that exist today, so
    /// a future expansion is picked up without a code change. <c>mods/survivalmode</c> is in
    /// here rather than in <see cref="FindMods"/> deliberately — Crucible layers onto the base
    /// game and the hook explicitly does not treat it as a mod (InventorySack_AddItem.cpp skips
    /// the mod name in Crucible), so its items belong to vanilla.
    /// </summary>
    public static IReadOnlyList<string> FindExpansionFolders(string gameDir) {
        var paths = new List<string>();

        void AddIfExists(string path) {
            if (Directory.Exists(path)) paths.Add(path);
        }

        for (var i = 1; i <= 9; i++) AddIfExists(Path.Combine(gameDir, $"gdx{i}"));
        for (var i = 1; i <= 9; i++) AddIfExists(Path.Combine(gameDir, $"survivalmode{i}"));
        AddIfExists(Path.Combine(gameDir, "mods", "survivalmode"));

        return paths;
    }

    /// <summary>
    /// Mods installed alongside the game — <c>mods/&lt;name&gt;</c> with a database of its own.
    ///
    /// A mod ships only the records it adds or changes and layers over the base game (measured
    /// here: SurvivalMode.arz is 7 MB against the base game's 58 MB), which is why a modded
    /// item's template is looked up mod-first and falls back to vanilla.
    ///
    /// The name is the folder name, which is what Grim Dawn reports to the hook and therefore
    /// what lands in <c>PlayerItem.Mod</c>.
    /// </summary>
    public static IReadOnlyList<(string Name, string Directory)> FindMods(string gameDir) {
        var modsDir = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(modsDir)) return [];

        var mods = new List<(string, string)>();
        foreach (var dir in Directory.GetDirectories(modsDir).OrderBy(d => d, StringComparer.Ordinal)) {
            var name = Path.GetFileName(dir);

            // Crucible is an expansion as far as items are concerned; see FindExpansionFolders.
            if (string.Equals(name, "survivalmode", StringComparison.OrdinalIgnoreCase)) continue;

            if (FindArz(dir).Count > 0) mods.Add((name, dir));
        }
        return mods;
    }

    /// <summary>Every .arz directly under a folder's <c>database</c> directory.</summary>
    private static IReadOnlyList<string> FindArz(string folder) {
        var dir = Path.Combine(folder, "database");
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.arz").OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Locates every .arz the installation provides for vanilla play: the base game and its
    /// expansions, in load order. Mods are separate — see <see cref="FindMods"/>.
    /// </summary>
    public static IReadOnlyList<string> FindDatabases(string gameDir) {
        var candidates = new List<string>();

        var baseArz = Path.Combine(gameDir, "database", "database.arz");
        if (File.Exists(baseArz)) candidates.Add(baseArz);

        foreach (var expansion in FindExpansionFolders(gameDir)) {
            candidates.AddRange(FindArz(expansion));
        }
        return candidates;
    }

    /// <summary>
    /// Newest modification time across everything a parse reads — the game's databases, its text
    /// archives, and any mod's databases.
    ///
    /// Upstream's <c>ParsingService.GetHighestTimestamp</c>, and it exists for one reason: a Grim
    /// Dawn patch rewrites these files, and until the parse is repeated every name, level and
    /// icon in the collection is quietly out of date. Nothing about that failure announces
    /// itself — the item list still renders, with last patch's data.
    /// </summary>
    public static long GetHighestTimestamp(string gameDir) {
        long highest = 0;

        void Consider(string path) {
            try {
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
                if (written > highest) highest = written;
            }
            catch (IOException) { /* vanished mid-scan */ }
        }

        foreach (var database in FindDatabases(gameDir)) Consider(database);
        foreach (var (_, dir) in FindMods(gameDir)) {
            foreach (var database in FindModDatabases(dir)) Consider(database);
        }

        // Text archives too: a patch that only retitles items still changes what a parse would
        // produce.
        var resources = Path.Combine(gameDir, "resources");
        if (Directory.Exists(resources)) {
            foreach (var arc in Directory.GetFiles(resources, "Text_*.arc")) Consider(arc);
        }
        foreach (var expansion in FindExpansionFolders(gameDir)) {
            var dir = Path.Combine(expansion, "resources");
            if (!Directory.Exists(dir)) continue;
            foreach (var arc in Directory.GetFiles(dir, "Text_*.arc")) Consider(arc);
        }

        return highest;
    }

    /// <summary>The .arz files a mod defines, which layer over <see cref="FindDatabases"/>.</summary>
    public static IReadOnlyList<string> FindModDatabases(string modDir) => FindArz(modDir);

    /// <summary>
    /// Localised text archives, in load order — base game first so expansions override it, and
    /// English first so the chosen language overlays it.
    ///
    /// **English is always included, even when another language is chosen.** Grim Dawn's
    /// translations are partial: measured on this installation, <c>Text_EN.arc</c> is 2.5 MB and
    /// <c>Text_DE.arc</c> is 191 KB, and the expansions ship English only. Loading the chosen
    /// language alone would leave most items with no name at all rather than an English one.
    /// Upstream reaches the same place differently, by passing its English language object in as
    /// the fallback for every lookup.
    /// </summary>
    public static IReadOnlyList<string> FindTextArchives(string gameDir, string language = "EN") {
        var archives = new List<string>();

        void AddSet(string code) {
            void Add(string relative) {
                var path = Path.Combine(gameDir, relative);
                if (File.Exists(path)) archives.Add(path);
            }

            Add(Path.Combine("resources", $"Text_{code}.arc"));
            foreach (var expansion in FindExpansionFolders(gameDir)) {
                var path = Path.Combine(expansion, "resources", $"Text_{code}.arc");
                if (File.Exists(path)) archives.Add(path);
            }
        }

        AddSet("EN");
        if (!string.Equals(language, "EN", StringComparison.OrdinalIgnoreCase)) {
            AddSet(language);
        }
        return archives;
    }

    /// <summary>
    /// Language codes this installation actually ships, discovered rather than hardcoded — the
    /// set differs by release and by what the user chose to download, and offering a language
    /// whose archive is absent would silently do nothing.
    /// </summary>
    public static IReadOnlyList<string> FindLanguages(string gameDir) {
        var resources = Path.Combine(gameDir, "resources");
        if (!Directory.Exists(resources)) return ["EN"];

        var codes = Directory.GetFiles(resources, "Text_*.arc")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Select(name => name["Text_".Length..])
            .Where(code => code.Length is > 0 and <= 5)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        codes.Add("EN");
        return codes.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// tag → display name, merged across archives. Later archives win, which is how
    /// expansions retitle base-game items.
    /// </summary>
    public static Dictionary<string, string> LoadTags(IEnumerable<string> textArchives) {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var archive in textArchives) {
            foreach (var tag in ArzParser.ParseArcFile(archive)) {
                if (tag.Tag is null || tag.Name is null) continue;
                tags[tag.Tag] = tag.Name;
            }
        }
        return tags;
    }

    /// <summary>
    /// Item templates from one .arz, with names resolved through <paramref name="tags"/>.
    /// </summary>
    /// <param name="skipLots">
    /// Passed through to upstream: skips loot tables, which are the bulk of the file and
    /// are not items.
    /// </param>
    public static IEnumerable<ItemTemplate> LoadTemplates(
        string arzFile,
        IReadOnlyDictionary<string, string> tags,
        bool skipLots = true) {

        foreach (var item in ArzParser.LoadItemRecords(arzFile, skipLots)) {
            if (item.Record is null || item.Stats is null) continue;

            var stats = ToLookup(item.Stats);

            var nameTag = NameFields
                .Select(field => Text(stats, field))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            var setTag = Text(stats, "setName");

            yield return new ItemTemplate {
                Record           = item.Record,
                NameTag          = nameTag,
                Name             = nameTag is not null && tags.TryGetValue(nameTag, out var resolved)
                                       ? ResolveName(resolved) : null,
                Quality          = Text(stats, "itemQualityTag") ?? Text(stats, "itemStyleTag"),
                ItemClass        = Text(stats, "Class"),
                Classification   = Text(stats, "itemClassification"),
                SetRecord        = Text(stats, "itemSetName"),
                SetName          = setTag is not null && tags.TryGetValue(setTag, out var setName)
                                       ? ResolveName(setName) : setTag,
                Bitmap           = BestBitmap(stats),
                LevelRequirement = (int)(Number(stats, "levelRequirement") ?? 0),
            };
        }
    }

    /// <summary>
    /// Every stat row for every record, which is what the seed engine needs: rolling an
    /// item's real values requires the full rollable field set of its base, prefix and suffix
    /// records, not just the handful of fields <see cref="LoadTemplates"/> summarises.
    /// </summary>
    /// <param name="applyStatFilter">
    /// Whether to apply <see cref="ItemStats.StatFilter"/>, upstream's gate on what reaches the
    /// seed engine. It must be on for anything feeding the engine, and it must be <b>off</b>
    /// for everything else.
    ///
    /// The distinction is upstream's, and easy to lose: upstream stores every stat row in
    /// <c>DatabaseItemStat_v2</c> and applies the filter only when *reading* rows for the
    /// engine. Fields it excludes are still there for other queries — the summoner-skill
    /// filter searches for <c>spawnObjects</c>, which the filter drops (zero value, not
    /// whitelisted). Filtering at load time for every caller would make that filter match
    /// nothing at all, quietly.
    /// </param>
    public static IEnumerable<(string Record, List<ItemStats.Dto.DBStatRow> Stats)> LoadAllStats(
        string arzFile, bool skipLots = true, bool applyStatFilter = true) {

        foreach (var item in ArzParser.LoadItemRecords(arzFile, skipLots)) {
            if (item.Record is null || item.Stats is null) continue;

            var rows = new List<ItemStats.Dto.DBStatRow>();
            foreach (var stat in item.Stats) {
                if (stat.Stat is null) continue;

                // Apply upstream's filter here rather than later: the seed engine draws from a
                // shared stream in a fixed field order, so an extra rollable field inserts an
                // extra draw and desyncs everything after it. See ItemStats.StatFilter.
                if (applyStatFilter && !ItemStats.StatFilter.Keep(stat.Stat, stat.Value)) continue;

                rows.Add(new ItemStats.Dto.DBStatRow {
                    Record    = item.Record,
                    Stat      = stat.Stat,
                    Value     = stat.Value,
                    TextValue = stat.TextValue,
                });
            }

            if (rows.Count > 0) {
                yield return (item.Record, rows);
            }
        }
    }

    /// <summary>
    /// Reduces a localised tag value to a plain display name.
    ///
    /// Grim Dawn packs every grammatical form of a name into one tag —
    /// <c>[ms]Mächtiger[fs]Mächtige[ns]Mächtiges[mp]Mächtige…</c> — so a German item name
    /// arrives as <c>[fs]Blutschwurpistole</c>. English tags carry no markers, which is why
    /// this never mattered until languages did.
    ///
    /// This is upstream's own resolver from the referenced StatTranslator project rather than a
    /// reimplementation: the parsing is genuinely fiddly (variants may contain spaces, and two
    /// tags run back to back with no separator), and upstream's comments record a bug it
    /// already fixed — a stray "[" downstream renders as "Mächtiger (fsMächtige)".
    ///
    /// <c>FilterGenderTag</c> keeps the first variant, which is the right choice here: a
    /// template name stands alone, with no adjective that has to agree with it. Composed names
    /// — prefix + quality + core + suffix — come from the game's own tooltip through the hook,
    /// already in the player's language and already agreeing.
    /// </summary>
    private static string ResolveName(string value) =>
        StatTranslator.ItemNameCombinator.FilterGenderTag(value);

    /// <summary>
    /// A record can repeat a stat key; the first occurrence is the effective one, matching
    /// how upstream reads these.
    /// </summary>
    private static Dictionary<string, IItemStat> ToLookup(IEnumerable<IItemStat> stats) {
        var lookup = new Dictionary<string, IItemStat>(StringComparer.OrdinalIgnoreCase);
        foreach (var stat in stats) {
            if (stat.Stat is null) continue;
            lookup.TryAdd(stat.Stat, stat);
        }
        return lookup;
    }

    /// <summary>
    /// What a parse produces, as a number that changes when the parse does.
    ///
    /// A Grim Dawn patch is not the only reason stored game data goes out of date: teaching this
    /// class to read a field it did not read before leaves every collection parsed until then
    /// missing it, with the game on disk unchanged so no other check notices. Relics kept a blank
    /// icon through any number of re-analyses for exactly that reason — an icon is chosen at
    /// parse time, and re-analysing is a different pass entirely.
    ///
    /// Raise this whenever what a parse writes changes. See also
    /// <see cref="ItemStats.StatPrecomputeService.Version"/>, which does the same for the pass
    /// after it.
    /// </summary>
    public const int Version = 2;

    /// <summary>Where that number is kept, alongside the parse's other provenance.</summary>
    public const string VersionKey = "gamedata.parserVersion";

    /// <summary>
    /// Which stat holds an item's icon, and which to prefer when a record carries several.
    ///
    /// Upstream's table, scores and all, from <c>DatabaseItemStatDaoImpl.MapItemBitmaps</c>.
    /// Most items say <c>bitmap</c>, but a relic says <c>artifactBitmap</c>, a shard says
    /// <c>shardBitmap</c> and so on — and this port read only <c>bitmap</c>, so every relic in a
    /// collection had a blank icon no matter how often the game data was re-read.
    ///
    /// The scores matter where a record has more than one: a relic's formula carries both
    /// <c>artifactFormulaBitmapName</c> and the relic's own bitmap, and the relic is the picture
    /// worth showing.
    /// </summary>
    private static readonly (string Stat, int Score)[] BitmapStats = [
        ("bitmap", 10),
        ("relicBitmap", 8),
        ("shardBitmap", 6),
        ("artifactBitmap", 4),
        ("noteBitmap", 2),
        ("artifactFormulaBitmapName", 0),
    ];

    private static string? BestBitmap(IReadOnlyDictionary<string, IItemStat> stats) =>
        BitmapStats
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => Text(stats, candidate.Stat))
            .FirstOrDefault(value => value is not null);

    private static string? Text(IReadOnlyDictionary<string, IItemStat> stats, string key) =>
        stats.TryGetValue(key, out var stat) && !string.IsNullOrWhiteSpace(stat.TextValue)
            ? stat.TextValue.Trim()
            : null;

    private static float? Number(IReadOnlyDictionary<string, IItemStat> stats, string key) =>
        stats.TryGetValue(key, out var stat) ? stat.Value : null;
}
