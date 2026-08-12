using IAGrim.Platform;

namespace IAGrim.Core.GameData;

/// <summary>Why the parsed game data is out of date, if it is.</summary>
public sealed record GameDataStaleness(bool NeverParsed, bool GameUpdated, string? ParsedLanguage) {
    public bool IsStale => NeverParsed || GameUpdated || LanguageChanged;
    public bool LanguageChanged { get; init; }

    /// <summary>One sentence a user can act on, or null when everything is current.</summary>
    public string? Reason =>
        NeverParsed ? "Grim Dawn's item database has not been read yet."
      : GameUpdated ? "Grim Dawn has been updated since the item database was read."
      : LanguageChanged ? $"The item database was read in {ParsedLanguage}, not the selected language."
      : null;
}

/// <summary>
/// Whether what `iagd parse` produced still matches the game on disk.
///
/// This exists because the failure it detects is silent. A Grim Dawn patch rewrites the item
/// databases; nothing breaks, no error appears, and every name, level and icon in the collection
/// is simply last patch's. Same for changing the language and forgetting to re-parse. Upstream
/// tracks both (`GrimDawnLocationLastModified`, `ParsedLanguageCode`) and warns on startup.
/// </summary>
public static class GameDataStatus {
    public static GameDataStaleness Check(string databasePath, string? gameDir, string selectedLanguage) {
        using var store = new GameDataStore(databasePath);

        var recorded = store.Meta("gamedata.sourceTimestamp");
        var parsedLanguage = store.Meta("gamedata.language");

        if (recorded is null || store.TemplateCount() == 0) {
            return new GameDataStaleness(NeverParsed: true, GameUpdated: false, parsedLanguage);
        }

        var updated = false;
        if (gameDir is not null && Directory.Exists(gameDir) && long.TryParse(recorded, out var parsedAt)) {
            updated = ItemDatabase.GetHighestTimestamp(gameDir) > parsedAt;
        }

        return new GameDataStaleness(NeverParsed: false, GameUpdated: updated, parsedLanguage) {
            LanguageChanged = parsedLanguage is not null
                              && !string.Equals(parsedLanguage, selectedLanguage, StringComparison.OrdinalIgnoreCase),
        };
    }
}
