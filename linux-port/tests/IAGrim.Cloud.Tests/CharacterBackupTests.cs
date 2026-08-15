using System.IO.Compression;
using Xunit;

namespace IAGrim.Cloud.Tests;

/// <summary>
/// Character backup: which files are picked up, what the archive contains, and what the server
/// makes of it.
///
/// This is the only part of online sync that uploads something other than item metadata, and the
/// files belong to the game rather than to this tool — so the tests are mostly about the
/// selection rules. An archive with the wrong contents is rejected by the server outright
/// (it re-checks every extension), which turns a wrong rule into a backup that silently never
/// happens.
/// </summary>
[Collection(CloudServerCollection.Name)]
public class CharacterBackupTests : IDisposable {
    private readonly CloudServerFixture _server;
    private readonly string _saves;
    private readonly string _staging;

    public CharacterBackupTests(CloudServerFixture server) {
        _server = server;
        Skip.IfNot(server.Available, server.SkipReason);
        server.UseUris();

        _saves = Path.Combine(Path.GetTempPath(), $"iagd-saves-{Guid.NewGuid():N}", "Save");
        _staging = Path.Combine(Path.GetTempPath(), $"iagd-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_saves, "main"));
        Directory.CreateDirectory(_staging);
    }

    /// <summary>Creates a character directory with a plausible player.gdc.</summary>
    private string AddCharacter(string name, long sizeBytes = 8 * 1024, params string[] extraFiles) {
        var directory = Path.Combine(_saves, "main", name);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "player.gdc"), new byte[sizeBytes]);
        foreach (var file in extraFiles) {
            File.WriteAllBytes(Path.Combine(directory, file), new byte[64]);
        }
        return directory;
    }

    private CharacterBackupService Service(TestSettings? settings = null) {
        settings ??= new TestSettings { CloudUser = _server.Email, CloudAuthToken = _server.Token };
        AuthService.InvalidateCache();
        using var collection = new TestCollection();
        var auth = new AuthService(new AuthenticationProvider(settings), collection.Store);
        return new CharacterBackupService(settings, auth, _saves, _staging, cooldownMs: 0);
    }

    [SkippableFact]
    public void Only_characters_changed_since_the_last_run_are_considered() {
        AddCharacter("Ulgrim");
        AddCharacter("Mazaan");

        var service = Service();

        // Everything is newer than the epoch.
        Assert.Equal(2, service.ListCharactersNewerThan(DateTime.MinValue).Count);

        // Nothing is newer than now.
        Assert.Empty(service.ListCharactersNewerThan(DateTime.UtcNow.AddMinutes(1)));
    }

    /// <summary>
    /// A save below four kilobytes is treated as corrupt. Uploading a half-written file over a
    /// good backup is worse than not backing up at all, which is the whole reason for the rule.
    /// </summary>
    [SkippableFact]
    public void A_suspiciously_small_save_is_skipped() {
        AddCharacter("Truncated", sizeBytes: 1024);
        AddCharacter("Healthy", sizeBytes: 8 * 1024);

        var characters = Service().ListCharactersNewerThan(DateTime.MinValue);

        Assert.Contains("Healthy", characters);
        Assert.DoesNotContain("Truncated", characters);
    }

    [SkippableFact]
    public void A_directory_without_a_character_save_is_skipped() {
        Directory.CreateDirectory(Path.Combine(_saves, "main", "_Empty"));
        AddCharacter("Real");

        Assert.Equal(["Real"], Service().ListCharactersNewerThan(DateTime.MinValue));
    }

    /// <summary>
    /// The archive carries the game's own files and nothing else. The server re-checks every
    /// extension and rejects the whole upload on the first stranger, so a stray file here is a
    /// backup that never lands.
    /// </summary>
    [SkippableFact]
    public void An_archive_contains_only_grim_dawns_own_files() {
        AddCharacter("Ulgrim", extraFiles: ["levels_world001.map.fow", "notes.txt", "player.gdc.bak"]);

        var target = Path.Combine(_staging, "test.zip");
        Service().BackupCharacter(target, "Ulgrim");

        using var zip = ZipFile.OpenRead(target);
        var names = zip.Entries.Select(entry => Path.GetFileName(entry.FullName)).ToList();

        Assert.Contains("player.gdc", names);
        Assert.Contains("levels_world001.map.fow", names);
        Assert.DoesNotContain("notes.txt", names);
        Assert.DoesNotContain("player.gdc.bak", names);
    }

    /// <summary>Grim Dawn's own "(1)" duplicates are not saves worth uploading.</summary>
    [SkippableFact]
    public void Duplicated_saves_are_not_archived() {
        var directory = AddCharacter("Ulgrim");
        File.WriteAllBytes(Path.Combine(directory, "player (1).gdc"), new byte[64]);

        var target = Path.Combine(_staging, "test.zip");
        Service().BackupCharacter(target, "Ulgrim");

        using var zip = ZipFile.OpenRead(target);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains('('));
    }

    [SkippableFact]
    public void Oversized_files_are_left_out() {
        var directory = AddCharacter("Ulgrim");
        File.WriteAllBytes(Path.Combine(directory, "huge.fow"), new byte[CharacterBackupService.MaxFileBytes + 1]);

        var target = Path.Combine(_staging, "test.zip");
        Service().BackupCharacter(target, "Ulgrim");

        using var zip = ZipFile.OpenRead(target);
        Assert.DoesNotContain(zip.Entries, entry => entry.Name == "huge.fow");
        Assert.Contains(zip.Entries, entry => entry.Name == "player.gdc");
    }

    /// <summary>
    /// The archive is a real zip the server can open. It validates the container before it looks
    /// at anything else, and an archive it cannot open is refused with a message upstream
    /// specifically watches for.
    /// </summary>
    [SkippableFact]
    public void The_archive_is_one_the_server_accepts() {
        AddCharacter("Ulgrim");
        var target = Path.Combine(_staging, "upload.zip");
        Service().BackupCharacter(target, "Ulgrim");

        using var client = _server.Client();
        using var content = new MultipartFormDataContent();
        using var stream = File.OpenRead(target);
        content.Add(new StreamContent(stream), "file", "upload.zip");

        var response = client
            .PostAsync($"{CloudUris.UploadCharacterUrl}?name=Ulgrim", content)
            .GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        // The test server has no S3 bucket configured, so the upload cannot complete — but it
        // gets past every check the *client* is responsible for, which is what this asserts.
        // A 400 here would mean a malformed archive or a file the game does not own.
        Assert.NotEqual(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("does not appear to be a valid zip file", body);
        Assert.DoesNotContain("does not appear to belong to Grim Dawn", body);
        Assert.DoesNotContain("Forgot to attach the file", body);
    }

    /// <summary>An archive with a stranger in it is refused, which is what makes the filter load-bearing.</summary>
    [SkippableFact]
    public void The_server_refuses_an_archive_containing_a_foreign_file() {
        var target = Path.Combine(_staging, "bad.zip");
        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create)) {
            zip.CreateEntry("main/Ulgrim/notes.txt");
        }

        using var client = _server.Client();
        using var content = new MultipartFormDataContent();
        using var stream = File.OpenRead(target);
        content.Add(new StreamContent(stream), "file", "bad.zip");

        var response = client
            .PostAsync($"{CloudUris.UploadCharacterUrl}?name=Ulgrim", content)
            .GetAwaiter().GetResult();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The high-water mark only moves when everything succeeded. A partial success that advanced
    /// it would leave the failed character unbacked-up until it next changed.
    /// </summary>
    [SkippableFact]
    public void A_failed_upload_leaves_the_timestamp_alone() {
        AddCharacter("Ulgrim");

        var settings = new TestSettings {
            CloudUser = _server.Email,
            CloudAuthToken = _server.Token,
            LastCharSyncUtc = DateTime.MinValue,
        };

        // The server has no S3 bucket, so every upload fails after validation.
        var result = Service(settings).ExecuteInternal();

        Assert.Equal(DateTime.MinValue, settings.LastCharSyncUtc);
        Assert.False(result.EverythingSucceeded);
        Assert.Contains("Ulgrim", result.Failed);
        Assert.Empty(result.Uploaded);
    }

    /// <summary>
    /// A pass names what it did. "Backup failed" with no names is a message nobody can act on,
    /// and character saves are the one thing here this tool cannot regenerate.
    /// </summary>
    [SkippableFact]
    public void A_pass_reports_which_characters_it_handled() {
        AddCharacter("Ulgrim");
        AddCharacter("Mazaan");

        var result = Service().ExecuteInternal();

        Assert.False(result.DidNothing);
        Assert.Equal(2, result.Uploaded.Count + result.Failed.Count);
        Assert.Contains("Ulgrim", result.Uploaded.Concat(result.Failed));
        Assert.Contains("Mazaan", result.Uploaded.Concat(result.Failed));
    }

    /// <summary>
    /// With nothing changed since the last run there is no work and no request. The panel says
    /// so rather than showing an empty list with no explanation.
    /// </summary>
    [SkippableFact]
    public void A_pass_with_nothing_to_do_says_so() {
        AddCharacter("Ulgrim");

        var settings = new TestSettings {
            CloudUser = _server.Email,
            CloudAuthToken = _server.Token,
            LastCharSyncUtc = DateTime.UtcNow.AddMinutes(1),
        };

        var result = Service(settings).ExecuteInternal();

        Assert.True(result.DidNothing);
        Assert.True(result.EverythingSucceeded);
    }

    /// <summary>
    /// Backup is suspended while the game runs. Zipping a save Grim Dawn is writing produces a
    /// corrupt archive, and uploading one over a good backup is the worst outcome available.
    /// </summary>
    [SkippableFact]
    public void Backup_is_suspended_while_the_game_is_running() {
        AddCharacter("Ulgrim");

        var settings = new TestSettings {
            CloudUser = _server.Email,
            CloudAuthToken = _server.Token,
            LastCharSyncUtc = DateTime.MinValue,
        };

        var service = Service(settings);
        service.SetIsActive(false);
        service.Execute();

        // Nothing was attempted, so nothing was staged.
        Assert.Empty(Directory.GetFiles(_staging, "*.zip"));

        service.SetIsActive(true);
        service.Execute();
        Assert.NotEmpty(Directory.GetFiles(_staging, "*.zip"));
    }

    /// <summary>Nothing is attempted when there is no save directory at all.</summary>
    [SkippableFact]
    public void Nothing_happens_without_a_save_directory() {
        var service = new CharacterBackupService(
            new TestSettings { CloudUser = _server.Email, CloudAuthToken = _server.Token },
            new AuthService(new AuthenticationProvider(new TestSettings()), new TestCollection().Store),
            Path.Combine(Path.GetTempPath(), "iagd-nonexistent-" + Guid.NewGuid().ToString("N")),
            _staging,
            cooldownMs: 0);

        Assert.False(service.SaveDirectoryExists());
        service.Execute();   // must not throw
    }

    /// <summary>
    /// <b>Upstream never uploads the shared stash.</b> Its check joins "Save" onto a path that
    /// already ends in Save, so it looks in <c>Save/Save/transfer.gst</c> and always answers
    /// false. Reproduced here, and asserted, so the day it is deliberately fixed this test is
    /// what has to be changed — rather than the behaviour drifting unnoticed in either direction.
    /// </summary>
    [SkippableFact]
    public void The_shared_stash_check_looks_where_upstream_looks() {
        File.WriteAllBytes(Path.Combine(_saves, "transfer.gst"), new byte[2048]);

        var service = Service();

        // The file exists where the game puts it, and the check still says no.
        Assert.True(File.Exists(Path.Combine(_saves, "transfer.gst")));
        Assert.False(service.IsStashFilesNewerThan(DateTime.MinValue));

        // It answers yes only for the path upstream actually tests.
        Directory.CreateDirectory(Path.Combine(_saves, "Save"));
        File.WriteAllBytes(Path.Combine(_saves, "Save", "transfer.gst"), new byte[2048]);
        Assert.True(service.IsStashFilesNewerThan(DateTime.MinValue));
    }

    /// <summary>
    /// The timestamp does read the stash files from the right place, so a stash change still
    /// moves the mark even though it never triggers an upload.
    /// </summary>
    [SkippableFact]
    public void The_high_water_mark_covers_characters_and_stash_files() {
        AddCharacter("Ulgrim");
        var stash = Path.Combine(_saves, "transfer.gst");
        File.WriteAllBytes(stash, new byte[2048]);
        File.SetLastWriteTimeUtc(stash, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Service().GetHighestCharacterTimestamp());
    }

    /// <summary>
    /// A character that has been uploaded appears in the list.
    ///
    /// Note what this also demonstrates: the server writes the character's row *before* it puts
    /// the file in storage, so a character can be listed whose archive never landed — which is
    /// exactly what happens here, the test server having no bucket. The UI therefore has to treat
    /// this list as "what has been offered", not "what is definitely restorable".
    /// </summary>
    [SkippableFact]
    public void An_uploaded_character_appears_in_the_list() {
        var name = $"Ulgrim-{Guid.NewGuid():N}";
        AddCharacter(name);

        var target = Path.Combine(_staging, "listed.zip");
        var service = Service();
        service.BackupCharacter(target, name);

        using (var client = _server.Client())
        using (var content = new MultipartFormDataContent())
        using (var stream = File.OpenRead(target)) {
            content.Add(new StreamContent(stream), "file", "listed.zip");
            client.PostAsync($"{CloudUris.UploadCharacterUrl}?name={Uri.EscapeDataString(name)}", content)
                .GetAwaiter().GetResult();
        }

        Assert.Contains(service.ListBackedUpCharacters(), character => character.Name == name);
    }

    [SkippableFact]
    public void A_download_url_for_an_unknown_character_is_null() {
        Assert.Null(Service().GetDownloadUrl("NoSuchCharacter"));
    }

    [Theory]
    [InlineData("player.gdc", true)]
    [InlineData("levels_world001.map.fow", true)]
    [InlineData("transfer.gst", true)]
    [InlineData("map.gsh", true)]
    [InlineData("quests.gdd", true)]
    [InlineData("something.dat", true)]
    [InlineData("something.bin", true)]
    [InlineData("notes.txt", false)]
    [InlineData("player.gdc.bak", false)]
    [InlineData("player (1).gdc", false)]
    [InlineData("screenshot.png", false)]
    public void The_accepted_file_formats_are_upstreams(string filename, bool accepted) {
        Assert.Equal(accepted, CharacterBackupService.IsAcceptedFileFormat(filename));
    }

    public void Dispose() {
        foreach (var directory in new[] { Directory.GetParent(_saves)?.FullName, _staging }) {
            try {
                if (directory is not null && Directory.Exists(directory)) {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException) { /* best effort */ }
        }
    }
}
