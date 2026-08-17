using IAGrim.Cloud;
using IAGrim.Cloud.Data;
using IAGrim.Platform;

namespace IAGrim.Host;

/// <summary>
/// Owns online sync for the life of the host — upstream's <c>BackupServiceWorker</c>, its
/// <c>BuddyItemsService</c> thread, and the wiring <c>MainWindow</c> does around both.
///
/// Four things run here, and only the first two are ever busy:
///
///   * the <b>backup loop</b>, once a second, doing nothing until a cooldown expires;
///   * the <b>buddy loop</b>, every five seconds, doing nothing until a buddy's own cooldown
///     expires;
///   * <b>live sync</b>, which connects only for users who play on more than one PC;
///   * <b>character backup</b>, every ten minutes, and only while the game is not running.
///
/// All of it is inert without a login. Nothing is constructed lazily on purpose — the loops are
/// cheap and the alternative is a login that does not take effect until a restart.
/// </summary>
public sealed class CloudWorker : IDisposable {
    /// <summary>
    /// Upstream waits fifteen seconds before its first pass, so start-up work (parsing, the
    /// first search) is not competing with a sync.
    /// </summary>
    private const int StartupDelayMs = 15_000;

    private readonly string _databasePath;
    private readonly AppSettings _settings;
    private readonly EventHub _events;

    private readonly CloudItemStore _itemStore;
    private readonly BuddyStore _buddyStore;
    private readonly AuthService _authService;
    private readonly BackupService _backupService;
    private readonly BuddyItemsService _buddyItemsService;
    private readonly WebSocketSyncService _liveSync;
    private readonly CharacterBackupService? _characterBackup;

    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public CloudWorker(string databasePath, AppSettings settings, EventHub events, SteamPaths? paths) {
        _databasePath = databasePath;
        _settings = settings;
        _events = events;

        // Who this installation is, for the user agent every cloud request carries. Resolved
        // here because this is where the settings that own it already are — reloading them
        // inside the cloud code would risk saving a stale copy over someone's changes.
        CloudHttp.ClientId = ClientIdentity.Resolve(settings);

        // The environment is fixed at construction. IAGD_CLOUD_ENV=localdev points everything at
        // a server on this machine, which is how this feature is developed without sending a
        // single request to the service the real account lives on.
        if (!CloudUris.IsInitialized) {
            CloudUris.Initialize(
                Environment.GetEnvironmentVariable("IAGD_CLOUD_ENV") ?? CloudUris.EnvCloud);
        }

        _itemStore = new CloudItemStore(databasePath);
        _buddyStore = new BuddyStore(databasePath);
        _authService = new AuthService(new AuthenticationProvider(settings), _itemStore);

        _backupService = new BackupService(_authService, _itemStore, settings, OnItemsUploaded);
        _backupService.OnItemsChanged += (_, _) => Arrived("Items arrived from your online backup.");

        _buddyItemsService = new BuddyItemsService(_buddyStore, settings, _authService);
        _buddyItemsService.OnItemsChanged += (_, _) => Announce("A buddy's items were updated.");

        _liveSync = new WebSocketSyncService(new AuthenticationProvider(settings), settings, _itemStore);
        _liveSync.OnItemsChanged += (_, _) => Arrived("Items arrived from your other PC.");

        // Character backup needs the game's save directory, which lives inside the Proton prefix.
        // Without a prefix there is nothing to back up and the service is simply not created.
        if (paths?.SavePath is { } savePath) {
            _characterBackup = new CharacterBackupService(
                settings, _authService, savePath, Path.Combine(LinuxPaths.BackupDir, "characters"));
        }
    }

    public AuthService Auth => _authService;
    public BackupService Backup => _backupService;
    public BuddyItemsService Buddies => _buddyItemsService;
    public BuddyStore BuddyStore => _buddyStore;
    public CloudItemStore Items => _itemStore;
    public CharacterBackupService? Characters => _characterBackup;
    public WebSocketSyncService LiveSync => _liveSync;

    public void Start() {
        if (_thread is not null) return;

        _liveSync.Start();
        _buddyItemsService.Start();

        _thread = new Thread(Loop) { IsBackground = true, Name = "CloudBackup" };
        _thread.Start();
    }

    private void Loop() {
        if (_cts.Token.WaitHandle.WaitOne(StartupDelayMs)) return;

        while (!_cts.IsCancellationRequested) {
            if (_cts.Token.WaitHandle.WaitOne(1000)) return;

            try {
                _backupService.Execute();

                // Suspended while Grim Dawn is running. Upstream watches the injector for the
                // same reason: zipping a save the game is writing produces a corrupt archive,
                // and a corrupt archive uploaded over a good one is worse than no backup at all.
                SetGameRunning(GameClock.StartTime() is not null);
                _characterBackup?.Execute();
            }
            catch (CloudHttpException ex) when (ex.Code == (int)System.Net.HttpStatusCode.Unauthorized) {
                // The token died under us. Upstream logs out here rather than retrying with a
                // credential the server has already forgotten.
                _backupService.Logout();
            }
            catch (Exception) {
                // A background loop must not take the host down.
            }
        }
    }

    /// <summary>
    /// Character backup is suspended while Grim Dawn is running: zipping a save the game is
    /// writing produces a corrupt archive, and a corrupt archive is worse than no backup.
    /// </summary>
    public void SetGameRunning(bool running) {
        _gameRunning = running;
        _characterBackup?.SetIsActive(!running);
    }

    private volatile bool _gameRunning;
    private volatile bool _characterBackupRunning;
    private CharacterBackupState _characterState = new(false, false, null, null, null);

    /// <summary>What the character-backup panel shows. Read from the UI thread, written from the loop.</summary>
    public CharacterBackupState CharacterState => _characterState with {
        Available = _characterBackup is not null,
        Running = _characterBackupRunning,
        PausedForGame = _gameRunning,
    };

    /// <summary>
    /// Runs a backup pass now, rather than waiting out the ten-minute cooldown.
    ///
    /// On its own thread: zipping and uploading several character saves takes long enough that
    /// doing it inside the request would look like the UI had hung. The panel follows it through
    /// <see cref="CharacterState"/>. A second call while one is running is ignored rather than
    /// queued — two passes would be zipping the same files into the same archive names.
    /// </summary>
    public bool BackupCharactersNow() {
        if (_characterBackup is null || _characterBackupRunning) return false;

        // Refuse while the game is running for the same reason the loop skips it. Saying so is
        // better than a pass that silently does nothing.
        if (_gameRunning) return false;

        _characterBackupRunning = true;
        _characterState = _characterState with { Message = null, Failed = null };

        new Thread(() => {
            try {
                var result = _characterBackup.ExecuteInternal();
                _characterState = _characterState with {
                    LastRunUtc = DateTime.UtcNow,
                    Failed = result.Failed.Count > 0 ? result.Failed : null,
                    Message = result.DidNothing
                        ? "Everything was already backed up."
                        : result.EverythingSucceeded
                            ? $"Backed up {result.Uploaded.Count} save file(s)."
                            : $"Backed up {result.Uploaded.Count}, {result.Failed.Count} failed. "
                              + "The failed ones are retried automatically.",
                };
            }
            catch (Exception ex) {
                _characterState = _characterState with {
                    LastRunUtc = DateTime.UtcNow,
                    Message = $"Character backup failed: {ex.Message}",
                };
            }
            finally {
                _characterBackupRunning = false;
            }
        }) { IsBackground = true, Name = "CharacterBackupNow" }.Start();

        return true;
    }

    /// <summary>Resets the download idle timer. Wired to the search endpoint.</summary>
    public void OnSearch() => _backupService.OnSearch();

    /// <summary>
    /// Pushes freshly looted items to the user's other machines. Called by the loot importer,
    /// and a no-op unless live sync is connected.
    /// </summary>
    public void OnItemsLooted(IReadOnlyList<long> ids) {
        if (!_liveSync.IsConnected || ids.Count == 0) return;

        var byId = _itemStore.GetUnsynchronizedItems().Where(item => ids.Contains(item.Id)).ToList();
        if (byId.Count > 0) _liveSync.SendItems(byId);
    }

    /// <summary>
    /// Pushes an in-game transfer, so the item disappears from the other machine before it can
    /// be transferred there too. The tombstone is already written by then, so this reads it.
    /// </summary>
    public void OnItemsTransferredToGame() {
        if (!_liveSync.IsConnected) return;

        var ids = _itemStore.GetItemsMarkedForOnlineDeletion()
            .Select(item => item.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList()!;

        if (ids.Count > 0) _liveSync.SendDeletions(ids!);
    }

    /// <summary>
    /// Logging out: forget the token, forget where the download had got to, offer the whole
    /// collection again, and drop every buddy's items. Upstream's <c>OnlineSettings</c> does
    /// exactly these five things, and the buddy half matters — those items were never the user's.
    /// </summary>
    public void Logout() {
        _settings.BuddySyncUserIdV3 = null;
        _authService.Logout();
        _settings.CloudUploadTimestamp = 0;
        _settings.Save();
        _itemStore.ResetOnlineSyncState();
        _buddyStore.DeleteAll();
    }

    /// <summary>
    /// Deletes the account and everything in it, then leaves this machine in the state a fresh
    /// login would find. Irreversible on the server.
    /// </summary>
    public bool DeleteAccount() {
        var rest = _authService.GetRestService();
        if (rest is null) return false;

        if (!new CloudSyncService(rest).DeleteAccount()) return false;

        _settings.CloudUploadTimestamp = 0;
        _settings.BuddySyncUserIdV3 = null;
        _settings.Save();
        _authService.UnAuthenticate();
        _itemStore.ResetOnlineSyncState();
        return true;
    }

    private void OnItemsUploaded(IReadOnlyList<long> ids) =>
        _ = _events.BroadcastAsync(
            new HostEvent("cloudItemsUploaded", new { count = ids.Count }), CancellationToken.None);

    private void Announce(string message) =>
        _ = _events.BroadcastAsync(HostEvent.Message(message, "info"), CancellationToken.None);

    /// <summary>
    /// Items came down from the service, so say so — and name them the way this collection names
    /// everything else.
    ///
    /// The server stores the name each item had on the machine that uploaded it, and the client
    /// that uploaded it may have been an older one, or Windows, or this port before the names
    /// were composed. Upstream has the same wire format and the same problem, and solves it the
    /// same way: what it stores is what its own game data says the item is called. Without this,
    /// a restored collection lists two names for two copies of one item.
    /// </summary>
    private void Arrived(string message) {
        Announce(message);
        try { StatRefresh.RefreshNames(_databasePath); }
        catch (Exception ex) {
            // Cosmetic, and the next start repairs it anyway; never worth killing the sync loop.
            Console.Error.WriteLine($"could not rename items that arrived online: {ex.Message}");
        }
    }

    /// <summary>
    /// The character-backup half of the panel.
    /// </summary>
    /// <param name="Available">False when there is no Proton prefix, so there are no saves to back up.</param>
    /// <param name="Running">A pass is in flight right now.</param>
    /// <param name="PausedForGame">Grim Dawn is running, so passes are suspended.</param>
    /// <param name="LastRunUtc">When the last manual pass finished.</param>
    /// <param name="Message">What it did, in words the panel can show as-is.</param>
    /// <param name="Failed">Names that did not upload, so the message can be acted on.</param>
    public sealed record CharacterBackupState(
        bool Available,
        bool Running,
        DateTime? LastRunUtc,
        string? Message,
        IReadOnlyList<string>? Failed) {
        public bool PausedForGame { get; init; }
    }

    public void Dispose() {
        try { _cts.Cancel(); } catch (Exception) { /* already disposed */ }

        _liveSync.Dispose();
        _buddyItemsService.Dispose();
        _authService.Dispose();
        _itemStore.Dispose();
        _buddyStore.Dispose();
    }
}
