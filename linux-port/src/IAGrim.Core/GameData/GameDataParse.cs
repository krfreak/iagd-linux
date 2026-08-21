using IAGrim.Core.Backup;
using IAGrim.Core.Imaging;
using IAGrim.Platform;

namespace IAGrim.Core.GameData;

/// <summary>What a parse read out of the game.</summary>
public sealed record ParseResult(int Templates, int Named, int Tags, int Skills, int Mappings,
                                 int Icons, int Mods, string Language);

/// <summary>
/// Reads Grim Dawn's own data — item templates, text tags, granted skills and icons — into the
/// collection database.
///
/// Lifted out of the CLI so the client can do it too. Upstream has no command line: choosing an
/// installation in its Grim Dawn tab and pressing Load Database is the whole interaction, and a
/// parse also happens by itself when the game has been patched. A port whose UI has to tell the
/// user to open a terminal has not finished porting that.
/// </summary>
public static class GameDataParse {
    /// <summary>
    /// Parses <paramref name="gameDir"/> into <paramref name="databasePath"/>.
    /// </summary>
    /// <param name="progress">Called with each line the CLI would have printed.</param>
    public static ParseResult Run(string gameDir, string databasePath, string backupDir,
                                  string language, Action<string>? progress = null) {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Re-parsing clears the game stat rows and reassigns every record id. That is derived
        // data and rebuildable, but the same file holds the collection, which is not — so there
        // is a copy to go back to before anything is deleted.
        try {
            DatabaseBackup.Create(databasePath, backupDir, "before-parse");
        }
        catch (Exception ex) {
            progress?.Invoke($"warning: could not back up before parsing: {ex.Message}");
        }

        // Read before anything is stored, and written back only once everything has been. Taking
        // it at the end would record a game updated *during* the parse as one this parse had
        // read, and the difference is a collection that never notices it is a patch behind.
        var sourceTimestamp = ItemDatabase.GetHighestTimestamp(gameDir);

        var available = ItemDatabase.FindLanguages(gameDir);
        if (!available.Contains(language, StringComparer.OrdinalIgnoreCase)) {
            progress?.Invoke($"warning: no Text_{language}.arc in this installation; using English.");
            progress?.Invoke($"         available: {string.Join(", ", available)}");
            language = "EN";
        }

        var archives = ItemDatabase.FindTextArchives(gameDir, language);
        progress?.Invoke($"Reading {archives.Count} text archive(s) ({language})...");
        var tags = ItemDatabase.LoadTags(archives);
        progress?.Invoke($"  {tags.Count:N0} tags");

        var databases = ItemDatabase.FindDatabases(gameDir);
        progress?.Invoke($"Reading {databases.Count} item database(s)...");

        // Later archives override earlier ones: that is how expansions retitle and rebalance
        // base-game items.
        var templates = new Dictionary<string, ItemTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var database in databases) {
            var before = templates.Count;
            foreach (var template in ItemDatabase.LoadTemplates(database, tags)) {
                templates[template.Record] = template;
            }
            progress?.Invoke($"  {Path.GetFileName(database),-16} {templates.Count - before,7:N0} new");
        }

        using var store = new GameDataStore(databasePath);
        var written = store.ReplaceTemplates(templates.Values);
        store.ReplaceTags(tags);

        // Mods layer over the base game: each ships only the records it adds or changes, so its
        // templates are stored under its own name and looked up mod-first with a vanilla
        // fallback. Items looted in a mod carry that name from the hook already.
        var mods = ItemDatabase.FindMods(gameDir);
        foreach (var (name, dir) in mods) {
            var modDatabases = ItemDatabase.FindModDatabases(dir);
            var modTemplates = new Dictionary<string, ItemTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (var database in modDatabases) {
                foreach (var template in ItemDatabase.LoadTemplates(database, tags)) {
                    modTemplates[template.Record] = template;
                }
            }

            var modWritten = store.ReplaceTemplates(modTemplates.Values, name);
            progress?.Invoke($"  mod {name,-24} {modWritten,7:N0} records");
        }

        // A mod removed since the last parse would otherwise keep winning the mod-first lookup
        // for items still tagged with it, naming them from a mod the player no longer has.
        var dropped = store.RemoveTemplatesForMissingMods(mods.Select(m => m.Name));
        if (dropped > 0) {
            progress?.Invoke($"  dropped templates for {dropped} uninstalled mod(s)");
        }

        // Which items grant which skills. A separate pass because it needs the *unfiltered*
        // records, including skill records, which the template pass does not keep.
        progress?.Invoke($"Resolving granted skills...");
        var parsedSkills = SkillParser.Parse(databases, tags, message => progress?.Invoke($"  {message}"));
        var (skills, mappings) = store.ReplaceSkills(parsedSkills);
        var summoners = parsedSkills.Skills.Count(s => s.SpawnsPets);
        progress?.Invoke($"  {skills:N0} skills, {mappings:N0} item mappings ({summoners:N0} summon pets)");

        // Icons come from the ARC archives rather than the item database. Extracting them
        // here keeps game data a single step, and the filenames are what ItemTemplate.IconFile
        // already points at.
        progress?.Invoke($"Extracting item icons...");
        var iconsBefore = Directory.GetFiles(LinuxPaths.IconDir, "*.png").Length;
        foreach (var arc in IconArchives(gameDir)) {
            try {
                DdsIconExtractor.ExtractItemIcons(arc, LinuxPaths.IconDir);
            }
            catch (Exception ex) {
                progress?.Invoke($"  warning: {Path.GetFileName(arc)}: {ex.Message}");
            }
        }
        var icons = Directory.GetFiles(LinuxPaths.IconDir, "*.png").Length;
        progress?.Invoke($"  {icons:N0} icons ({icons - iconsBefore:N0} new) in {LinuxPaths.IconDir}");

        // Last, because this is what a later run reads to decide the stored data is current —
        // so it has to mean "all of the above happened", not "some of it did". Written early, it
        // marked a parse complete before the skills and icons were in: closing the window at the
        // wrong moment left a database that looked freshly read, refused to read itself again,
        // and could only be recovered by deleting it by hand.
        store.RecordParseSource(sourceTimestamp, language);

        stopwatch.Stop();
        var named = templates.Values.Count(t => t.Name is not null);
        progress?.Invoke("");
        progress?.Invoke($"{written:N0} templates stored ({named:N0} named)"
                          + $"{(mods.Count > 0 ? $" plus {mods.Count} mod(s)" : "")}"
                          + $" in {stopwatch.Elapsed.TotalSeconds:F1}s.");
        progress?.Invoke("Records without a name are mostly loot tables and components, not items.");

        return new ParseResult(written, named, tags.Count, skills, mappings, icons, mods.Count, language);
    }

    /// <summary>
    /// The archives icons come from: Items.arc per expansion, and **Level Art.arc** with it.
    ///
    /// The second is not an oversight: a handful of items are world objects the player can pick
    /// up, so their textures live with the level art rather than with the item icons. Lokarr's
    /// set is the visible case — four pieces with no picture in the list until this was added.
    /// Upstream reads both for the base game and every expansion (ArzParser calls
    /// LoadIconsOrWarn on items.arc and on "Level Art.arc").
    ///
    /// Cheap despite those archives being gigabytes: the extractor skips anything over 45 KB and
    /// almost no level art qualifies. Measured on one installation, they add about six seconds
    /// and seven icons.
    /// </summary>
    public static IEnumerable<string> IconArchives(string gameDir) {
        string[] expansions = ["", "gdx1", "gdx2", "gdx3"];

        foreach (var expansion in expansions) {
            foreach (var archive in new[] { "Items.arc", "Level Art.arc" }) {
                var path = expansion.Length == 0
                    ? Path.Combine(gameDir, "resources", archive)
                    : Path.Combine(gameDir, expansion, "resources", archive);
                if (File.Exists(path)) yield return path;
            }
        }
    }
}
