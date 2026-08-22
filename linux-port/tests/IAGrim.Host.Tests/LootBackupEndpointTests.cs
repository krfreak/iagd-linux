using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IAGrim.Platform;
using Xunit;

namespace IAGrim.Host.Tests;

/// <summary>
/// GET /api/loot-backup and POST /api/loot-backup/prune — what the Settings page's "Looted item
/// files" panel reads and what its button calls.
///
/// The sweep itself is covered at the file level by
/// <c>IAGrim.Platform.Tests.LootBackupTests</c>; what these add is the HTTP wiring — that the
/// panel is told how much a sweep would take *before* it offers the button, that the button
/// actually deletes, and that <c>removed</c> distinguishes "nothing was old enough" from "not
/// asked", which the panel words differently.
///
/// <c>XDG_DATA_HOME</c> is redirected before the router is built, for the reason spelled out on
/// <see cref="ImportExportEndpointTests"/>: <c>LinuxPaths</c> is process-wide and read directly
/// by the endpoint, so this is the only way to keep a test run away from a real collection —
/// and this suite deletes files, which makes that non-negotiable.
/// </summary>
[Collection(XdgSuites.Name)]
public sealed class LootBackupEndpointTests : IDisposable {
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"iagd-lb-data-{Guid.NewGuid():N}");
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), $"iagd-lb-config-{Guid.NewGuid():N}");
    private readonly string? _previousDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly string? _previousConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly HttpClient _client;
    private readonly string _backupDir;

    public LootBackupEndpointTests() {
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_configDir);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _dataDir);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _configDir);
        LinuxPaths.ResolveDatabase(["--database", Path.Combine(_dataDir, "userdata.db")], null);
        _backupDir = LinuxPaths.LootBackupDir;

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
                catch (Exception) { return; }
                _ = router.HandleAsync(context, _cts.Token);
            }
        });

        _client = new HttpClient { BaseAddress = new Uri(url) };
    }

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

        try { Directory.Delete(_dataDir, recursive: true); } catch { }
        try { Directory.Delete(_configDir, recursive: true); } catch { }
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _previousDataHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previousConfigHome);
        LinuxPaths.UseDefaultDatabase();
    }

    private void WriteLootFile(string name, string content, TimeSpan? age = null) {
        var path = Path.Combine(_backupDir, name);
        File.WriteAllText(path, content);
        if (age is not null) File.SetLastWriteTime(path, DateTime.Now - age.Value);
    }

    /// <summary>Empty by default, and an empty directory is a state the panel has wording for.</summary>
    [Fact]
    public async Task AnUntouchedInstallationReportsNothingKept() {
        var body = await _client.GetFromJsonAsync<JsonElement>("/api/loot-backup");

        Assert.Equal(0, body.GetProperty("files").GetInt32());
        Assert.Equal(0, body.GetProperty("expired").GetInt32());
        Assert.Equal(3, body.GetProperty("retentionDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("removed").ValueKind);
        Assert.Equal(_backupDir, body.GetProperty("path").GetString());
    }

    [Fact]
    public async Task TheStatusSeparatesWhatIsKeptFromWhatASweepWouldTake() {
        WriteLootFile("stale1.csv", new string('a', 100), age: TimeSpan.FromDays(5));
        WriteLootFile("stale2.csv", new string('b', 100), age: TimeSpan.FromDays(40));
        WriteLootFile("recent.csv", new string('c', 50), age: TimeSpan.FromHours(2));

        var body = await _client.GetFromJsonAsync<JsonElement>("/api/loot-backup");

        Assert.Equal(3, body.GetProperty("files").GetInt32());
        Assert.Equal(250, body.GetProperty("bytes").GetInt64());
        Assert.Equal(2, body.GetProperty("expired").GetInt32());
        Assert.Equal(200, body.GetProperty("expiredBytes").GetInt64());

        // Reading is not sweeping: the panel loads on every visit to Settings.
        Assert.Equal(3, Directory.GetFiles(_backupDir).Length);
    }

    [Fact]
    public async Task PruningDeletesTheStaleFilesAndReportsWhatIsLeft() {
        WriteLootFile("stale1.csv", "a", age: TimeSpan.FromDays(5));
        WriteLootFile("stale2.csv", "b", age: TimeSpan.FromDays(5));
        WriteLootFile("recent.csv", "c");

        var response = await _client.PostAsync("/api/loot-backup/prune", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("removed").GetInt32());
        Assert.Equal(1, body.GetProperty("files").GetInt32());
        Assert.Equal(0, body.GetProperty("expired").GetInt32());

        Assert.True(File.Exists(Path.Combine(_backupDir, "recent.csv")));
        Assert.False(File.Exists(Path.Combine(_backupDir, "stale1.csv")));
    }

    /// <summary>
    /// "Nothing was old enough" is a result, not a no-op: the panel says so rather than
    /// leaving the button looking unpressed.
    /// </summary>
    [Fact]
    public async Task PruningWithNothingStaleAnswersZeroRatherThanNull() {
        WriteLootFile("recent.csv", "c");

        var response = await _client.PostAsync("/api/loot-backup/prune", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("removed").GetInt32());
        Assert.Equal(1, body.GetProperty("files").GetInt32());
        Assert.True(File.Exists(Path.Combine(_backupDir, "recent.csv")));
    }
}
