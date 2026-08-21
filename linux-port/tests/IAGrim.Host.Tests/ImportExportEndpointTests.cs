using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IAGrim.Core.Backup;
using IAGrim.Platform;
using Xunit;

namespace IAGrim.Host.Tests;

/// <summary>
/// POST /api/export and POST /api/import, over real HTTP against a real <see cref="ApiRouter"/>
/// — the two endpoints the Settings page's Export and Import panels call (BACKLOG entry 4). Both
/// endpoints are thin wrappers over <c>ItemExport</c>, already covered at the format level by
/// <c>IAGrim.Core.Tests.ItemExportTests</c>; what is worth testing here is the HTTP wiring itself
/// — validation, status codes, the safety backup, and the hardcore/softcore warning — none of
/// which the CLI or the core library exercises.
///
/// <c>LinuxPaths.DatabaseFile</c> and <c>LinuxPaths.BackupDir</c> are process-wide statics that
/// both endpoints read directly, the same way <c>/api/merge</c> already does — there is no way to
/// inject a path into <see cref="ApiRouter"/> for just this test. Each test therefore points
/// <c>XDG_DATA_HOME</c> and <c>XDG_CONFIG_HOME</c> at its own temp directory before the router is
/// built, so a run of this suite never touches a real collection or a real settings.json.
/// </summary>
public sealed class ImportExportEndpointTests : IDisposable {
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"iagd-ie-data-{Guid.NewGuid():N}");
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), $"iagd-ie-config-{Guid.NewGuid():N}");
    private readonly string? _previousDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly string? _previousConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly HttpClient _client;

    public ImportExportEndpointTests() {
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_configDir);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _dataDir);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _configDir);
        LinuxPaths.ResolveDatabase(["--database", Path.Combine(_dataDir, "userdata.db")], null);

        var (listener, url) = StartListener();
        _listener = listener;

        var collection = new CollectionService(LinuxPaths.DatabaseFile);
        var views = new CollectionViewService(LinuxPaths.DatabaseFile);
        var events = new EventHub();
        var router = new ApiRouter(collection, views, events, null, null, null);

        _loop = Task.Run(async () => {
            while (!_cts.IsCancellationRequested) {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) { return; } // listener stopped/disposed at teardown
                _ = router.HandleAsync(context, _cts.Token);
            }
        });

        _client = new HttpClient { BaseAddress = new Uri(url) };
    }

    /// <summary>A free loopback port, tried a few times since nothing here reserves one in advance.</summary>
    private static (HttpListener, string) StartListener() {
        for (var attempt = 0; attempt < 20; attempt++) {
            var port = Random.Shared.Next(35000, 45000);
            var url = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            try {
                listener.Start();
                return (listener, url);
            }
            catch (HttpListenerException) {
                listener.Close();
            }
        }
        throw new InvalidOperationException("could not find a free loopback port for the test listener");
    }

    public void Dispose() {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _client.Dispose();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
        try { Directory.Delete(_configDir, recursive: true); } catch { }
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _previousDataHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previousConfigHome);
        LinuxPaths.UseDefaultDatabase();
    }

    private static LootedItem Item(string baseRecord, uint seed, bool hardcore = false) => new() {
        IsHardcore = hardcore,
        BaseRecord = baseRecord,
        Seed = seed,
        Stats = [new LootStat(6, "Test Item")],
    };

    /// <summary>
    /// Posts JSON built the way <see cref="ApiRouter.ReadJsonAsync{T}"/> needs it: a
    /// <c>Content-Length</c> header, not chunked transfer-encoding. <c>PostAsJsonAsync</c>
    /// leaves that to negotiation and HttpClient does not always choose to send one, and
    /// <c>ReadJsonAsync</c> treats a request with no declared length as an empty body —
    /// <c>ContentLength64 &lt;= 0</c> — the same as a POST with none at all. That is fine
    /// against a browser, which always sends one for a small JSON body, but it made every
    /// request in this file silently look bodyless until this replaced <c>PostAsJsonAsync</c>.
    /// </summary>
    private Task<HttpResponseMessage> PostJson(string path, object body) =>
        _client.PostAsync(path, new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    // -------------------------------------------------------------------------------- export

    [Fact]
    public async Task ExportWithNoPathIsRefused() {
        var response = await PostJson("/api/export", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task ExportingBothBranchesTogetherCarriesTheMixedStashWarning() {
        using (var store = new LootStore(LinuxPaths.DatabaseFile)) {
            store.Insert(Item("records/items/gearweapons/swords1h/d013_sword.dbr", 1, hardcore: true));
            store.Insert(Item("records/items/gearweapons/swords1h/d013_sword.dbr", 2, hardcore: false));
        }

        var target = Path.Combine(_dataDir, "export.gds");
        var response = await PostJson("/api/export", new { path = target });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("count").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("warning").GetString()));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task ExportingOneBranchCarriesNoWarning() {
        using (var store = new LootStore(LinuxPaths.DatabaseFile)) {
            store.Insert(Item("records/items/gearweapons/swords1h/d013_sword.dbr", 1, hardcore: true));
            store.Insert(Item("records/items/gearweapons/swords1h/d013_sword.dbr", 2, hardcore: false));
        }

        var target = Path.Combine(_dataDir, "hardcore.gds");
        var response = await PostJson("/api/export", new { path = target, hardcore = true });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("warning").ValueKind);
    }

    // -------------------------------------------------------------------------------- import

    [Fact]
    public async Task ImportWithNoPathIsRefused() {
        var response = await PostJson("/api/import", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ImportOfAMissingFileIsRefused() {
        var response = await PostJson("/api/import",
            new { path = Path.Combine(_dataDir, "does-not-exist.gds") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("no such file", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// The collection is the one thing this whole port promises never to lose without a way
    /// back — so a successful import has to leave a copy of what the collection looked like
    /// before it ran, the same rule 'iagd import-file' and 'before-merge' follow.
    /// </summary>
    [Fact]
    public async Task ImportBacksUpFirstAndReportsWhatItDid() {
        // Something already in the collection, so there is a "before" for the backup to capture.
        using (var store = new LootStore(LinuxPaths.DatabaseFile)) {
            store.Insert(Item("records/items/gearweapons/swords1h/d013_sword.dbr", 999));
        }

        var file = Path.Combine(_dataDir, "incoming.gds");
        ItemExport.Export(LinuxPaths.DatabaseFile, file);

        // A second item the export above did not carry, so the import actually adds something.
        var items = ItemExport.Parse(File.ReadAllBytes(file));
        items.Add(items[0] with { Seed = 1000 });
        ItemExport.Write(file, items);

        var response = await PostJson("/api/import", new { path = file, mod = "Grimarillion" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("imported").GetInt32());
        Assert.Equal(1, body.GetProperty("skipped").GetInt32());
        Assert.Equal(0, body.GetProperty("refused").GetInt32());

        var backupName = body.GetProperty("backup").GetString();
        Assert.False(string.IsNullOrEmpty(backupName));
        Assert.True(File.Exists(Path.Combine(LinuxPaths.BackupDir, backupName!)));
    }

    [Fact]
    public async Task ImportingTheSameFileTwiceSkipsTheSecondTime() {
        // Written directly rather than exported from LinuxPaths.DatabaseFile: exporting the
        // collection this is about to import into would make the item a duplicate from the
        // start, which is not what "the same file twice" is meant to exercise.
        var file = Path.Combine(_dataDir, "roundtrip.gds");
        ItemExport.Write(file, [new ItemExport.ExportedItem {
            BaseRecord = "records/items/gearweapons/swords1h/d013_sword.dbr", Seed = 5,
        }]);

        var first = await PostJson("/api/import", new { path = file });
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, firstBody.GetProperty("imported").GetInt32());

        var second = await PostJson("/api/import", new { path = file });
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, secondBody.GetProperty("imported").GetInt32());
        Assert.Equal(1, secondBody.GetProperty("skipped").GetInt32());
    }
}
