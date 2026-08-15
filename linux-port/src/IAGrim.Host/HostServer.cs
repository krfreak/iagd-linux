using System.Net;
using IAGrim.Platform;

namespace IAGrim.Host;

/// <summary>
/// The local API and the loot importer, as one startable unit.
///
/// Extracted so the desktop app can run the host in-process rather than spawning it: one
/// process means one lifecycle, no port negotiation between parent and child, and no orphaned
/// server if the window dies. `iagd-host` remains a separate entry point for running headless.
/// </summary>
public sealed class HostServer : IAsyncDisposable {
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _requestLoop;
    private Task? _importer;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/";

    public SteamPaths? Paths { get; }
    public PrefixBridge? Bridge { get; }

    /// <summary>Set when discovery failed, so a UI can explain itself rather than load blank.</summary>
    public string? DiscoveryWarning { get; }

    /// <summary>Current settings. Replaced wholesale by <see cref="UpdateSettings"/>.</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>Watches for the game and attaches the hook; null when there is no prefix.</summary>
    public AutoAttachService? AutoAttach { get; private set; }

    /// <summary>
    /// Shows a native file or folder chooser, when something is able to.
    ///
    /// Set by the desktop app, which owns the window; null when running headless. The settings
    /// page is served over HTTP and can be opened in a browser, where a page cannot choose a
    /// path on the machine running the host — so the UI asks whether this exists and falls back
    /// to typing a path when it does not.
    /// </summary>
    public Func<bool, string, string?, string?>? FilePicker { get; set; }

    public HostServer(int port) {
        Port = port;
        Settings = AppSettings.Load();

        // Discovery can legitimately fail (the game has never been launched under Proton). The
        // host still starts, so a UI can load and explain what is wrong.
        try {
            Paths = SteamPaths.Discover();
            Bridge = PrefixBridge.Discover(Paths);
        }
        catch (DirectoryNotFoundException ex) {
            DiscoveryWarning = ex.Message;
        }

        // The hook only uses file-based IPC when the bridge settings say so, and without it
        // nothing is captured — silently. Doing this on every start rather than once at install
        // time means a prefix rebuilt by Steam repairs itself.
        if (Bridge is not null) {
            var applied = BridgeSettings.Apply(Bridge, Settings);
            if (applied.Error is not null) {
                Console.Error.WriteLine($"warning: could not configure the hook: {applied.Error}");
            }
            else if (applied.Created) {
                Console.WriteLine($"created {applied.Path} (enabled the hook's Wine mode)");
            }
        }

        // Loopback only. This exposes the player's collection and can push items into their
        // running game; it has no business being reachable from the network.
        _listener.Prefixes.Add(Url);
    }

    /// <summary>
    /// Binds the port and starts serving. Throws if the port is taken, which the caller should
    /// treat as "an instance is already running" rather than as a crash.
    /// </summary>
    /// <summary>Reads Grim Dawn's data when asked, and when it has gone stale.</summary>
    public GameDataRefresh? GameData { get; private set; }

    /// <summary>
    /// Online backup, buddy sharing and live sync. Present from <see cref="Start"/> onwards
    /// regardless of whether anyone is logged in.
    /// </summary>
    public CloudWorker? Cloud { get; private set; }

    public void Start() {
        var collection = new CollectionService(LinuxPaths.DatabaseFile);
        var views = new CollectionViewService(LinuxPaths.DatabaseFile);
        var events = new EventHub();
        var transfers = Bridge is null ? null : new TransferTracker(Bridge, events);

        // Online sync. Constructed whether or not anyone is logged in: every loop inside it is a
        // no-op without a token, and building it lazily would mean a login that does not take
        // effect until the next start.
        Cloud = new CloudWorker(LinuxPaths.DatabaseFile, Settings, events, Paths);
        Cloud.Start();

        // An item the game has just taken is pushed to the user's other machines straight away,
        // rather than waiting for the next deletion window.
        if (transfers is not null) {
            transfers.OnItemTakenByGame = Cloud.OnItemsTransferredToGame;
        }

        var api = new ApiRouter(collection, views, events, Paths, Bridge, transfers, this,
                                new CloudApi(Cloud, Settings));

        _listener.Start();
        AutoAttach = Bridge is null ? null : new AutoAttachService(Bridge);
        GameData = new GameDataRefresh(events);

        _importer = LootImporter.RunAsync(Bridge, collection, events, transfers,
                                          GameClock.StartTime, () => Settings, AutoAttach,
                                          Paths, GameData, _shutdown.Token);

        // Grim Dawn's own data first: a patched game, a changed language or a collection that
        // was never parsed. Upstream makes the same check when it starts. The analysis pass
        // follows it, so this covers both — and when nothing is stale it costs one timestamp
        // comparison and does nothing.
        var gameDir = Settings.GameDir ?? Paths?.GameDir;
        _ = GameData.StartIfStaleAsync(gameDir, Settings.Language, _shutdown.Token)
                    .ContinueWith(_ => StatRefresh.RunIfNeededAsync(LinuxPaths.DatabaseFile, gameDir,
                                                                    events, _shutdown.Token),
                                  TaskScheduler.Default);
        _requestLoop = RunAsync(api);
    }

    private async Task RunAsync(ApiRouter api) {
        while (!_shutdown.IsCancellationRequested) {
            HttpListenerContext context;
            try {
                context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
            }
            catch (OperationCanceledException) {
                break;
            }
            catch (HttpListenerException) {
                break;   // listener stopped underneath us
            }

            // One request must never take the server down, so each is isolated.
            _ = Task.Run(async () => {
                try {
                    await api.HandleAsync(context, _shutdown.Token);
                }
                catch (Exception ex) {
                    Console.Error.WriteLine($"request failed: {ex.Message}");
                    try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
                }
            }, _shutdown.Token);
        }
    }

    /// <summary>
    /// Replaces the settings and re-applies the ones the hook reads.
    ///
    /// The stash-tab settings only reach the hook through the bridge file, so persisting them
    /// without pushing them there would appear to work and change nothing.
    /// </summary>
    public string? UpdateSettings(AppSettings settings) {
        // Pointing the client at a different installation, or asking for another language, is
        // upstream's "Load Database" — the data has to be read again for the change to mean
        // anything. Doing it here is why the UI never has to send anyone to a terminal.
        var reparse = !string.Equals(settings.GameDir, Settings.GameDir, StringComparison.Ordinal)
                   || !string.Equals(settings.Language, Settings.Language, StringComparison.OrdinalIgnoreCase);

        settings.Save();
        Settings = settings;

        if (reparse && (settings.GameDir ?? Paths?.GameDir) is { } dir) {
            _ = GameData?.StartAsync(dir, settings.Language, _shutdown.Token);
        }

        if (Bridge is null) return null;
        return BridgeSettings.Apply(Bridge, settings).Error;
    }

    /// <summary>Blocks until the server stops, for the headless entry point.</summary>
    public Task WaitAsync() => _requestLoop ?? Task.CompletedTask;

    public async ValueTask DisposeAsync() {
        await _shutdown.CancelAsync();
        try { _listener.Stop(); } catch { }

        if (_importer is not null) {
            try { await _importer; } catch (OperationCanceledException) { }
        }
        if (_requestLoop is not null) {
            try { await _requestLoop; } catch (OperationCanceledException) { }
        }

        // After the request loop, so an in-flight cloud request finishes rather than being cut
        // off mid-upload with the items left marked unsynchronised.
        Cloud?.Dispose();

        _listener.Close();
        _shutdown.Dispose();
    }
}
