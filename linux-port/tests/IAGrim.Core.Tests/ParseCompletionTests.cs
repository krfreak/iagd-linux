using IAGrim.Core.GameData;
using IAGrim.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IAGrim.Core.Tests;

/// <summary>
/// What a half-finished parse leaves behind, and whether the next start notices.
///
/// Reading Grim Dawn's data takes a while and writes as it goes — templates, then tags, then
/// mods, then skills, then icons. Someone who closes the window in the middle of that has a
/// database with some of it in, and the only question that matters afterwards is whether the
/// client will read it again by itself. It did not: the marker saying "this is what the parse
/// was made from" was written partway through, so an interrupted run left a database that
/// looked freshly parsed, refused to re-read itself, and showed no templates and no item names.
/// The way out was to find the file under ~/.local/share and delete it by hand.
///
/// The marker is now written last, which makes these two assertions the whole contract: no
/// marker means never parsed, whatever else is in the file.
/// </summary>
public class ParseCompletionTests : IDisposable {
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"iagd-parse-{Guid.NewGuid():N}.db");

    public ParseCompletionTests() {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        Schema.Apply(connection);
    }

    public void Dispose() {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" }) {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static ItemTemplate Template(string record) =>
        new() { Record = record, Name = "Something", LevelRequirement = 50 };

    [Fact]
    public void TemplatesWithoutTheMarkerReadAsNeverParsed() {
        using (var store = new GameDataStore(_path)) {
            store.ReplaceTemplates([Template("records/items/a.dbr")]);
        }

        var status = GameDataStatus.Check(_path, gameDir: null, selectedLanguage: "EN");

        Assert.True(status.NeverParsed);
        Assert.True(status.IsStale);
        Assert.Equal("Grim Dawn's item database has not been read yet.", status.Reason);
    }

    /// <summary>
    /// And the other half: a run that reached the end is not read again. Without this the client
    /// would re-parse at every start, which is a minute of work and a cleared stat table each
    /// time — the opposite failure, and just as visible.
    /// </summary>
    [Fact]
    public void TemplatesWithTheMarkerReadAsCurrent() {
        using (var store = new GameDataStore(_path)) {
            store.ReplaceTemplates([Template("records/items/a.dbr")]);
            store.RecordParseSource(sourceTimestamp: 1_700_000_000, language: "EN");
        }

        var status = GameDataStatus.Check(_path, gameDir: null, selectedLanguage: "EN");

        Assert.False(status.NeverParsed);
        Assert.Null(status.Reason);
    }
}
