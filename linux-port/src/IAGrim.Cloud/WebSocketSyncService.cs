using System.Net.WebSockets;
using System.Text;
using IAGrim.Cloud.Data;
using IAGrim.Cloud.Dto;
using IAGrim.Platform;

namespace IAGrim.Cloud;

/// <summary>
/// Live sync between a user's own machines — upstream's
/// <c>Backup/Cloud/Service/WebSocketSyncService.cs</c>.
///
/// An addition on top of the REST backup, not a replacement: the REST sync stays the source of
/// truth and owns the timestamp, and this socket only makes the same events arrive sooner. A
/// dropped connection costs nothing, because the next REST pass reconciles whatever was missed.
/// That is why every send here is a no-op when disconnected rather than something queued.
///
/// It runs only when <b>both</b> conditions hold: the user has said they play on more than one
/// PC, and there is a token. Neither is a performance switch — a socket held open for a user who
/// has one machine is a connection somebody else's server is paying for and nothing is listening
/// to.
///
/// The deletion half is the one that matters for not losing items. When an item is transferred
/// into the game here, the other machine has to stop offering it *before* someone transfers it
/// there too; the REST path would take up to ten seconds, and this takes one round trip.
/// </summary>
public sealed class WebSocketSyncService : IDisposable {
    private const int InitialBackoffMs = 2000;
    private const int MaxBackoffMs = 60000;

    private readonly AuthenticationProvider _authenticationProvider;
    private readonly ICloudSettings _settings;
    private readonly ICloudItemStore _playerItemDao;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private volatile ClientWebSocket? _socket;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>Raised on a background thread after live sync changed the collection.</summary>
    public event EventHandler? OnItemsChanged;

    public WebSocketSyncService(
        AuthenticationProvider authenticationProvider,
        ICloudSettings settings,
        ICloudItemStore playerItemDao) {
        _authenticationProvider = authenticationProvider;
        _settings = settings;
        _playerItemDao = playerItemDao;
    }

    public void Start() {
        if (_running) return;
        _running = true;
        _thread = new Thread(ConnectionLoop) { IsBackground = true, Name = "WebSocketSync" };
        _thread.Start();
    }

    /// <summary>
    /// False whenever live sync is inactive: not logged in, "multiple PCs" off, or simply
    /// between reconnects. Callers skip their work rather than buffering it.
    /// </summary>
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    // Keeps a connection up for as long as it is wanted, backing off when it is not available.
    private void ConnectionLoop() {
        var backoff = InitialBackoffMs;

        while (_running && !_cts.IsCancellationRequested) {
            if (!ShouldConnect()) {
                CloseSocket();
                Sleep(2000);
                continue;
            }

            try {
                Connect();
                backoff = InitialBackoffMs;
                SuperviseConnection();
            }
            catch (OperationCanceledException) {
                // Shutting down.
            }
            catch (Exception) {
                // Unreachable, refused, rejected: retried with backoff below.
            }
            finally {
                CloseSocket();
            }

            if (_running && !_cts.IsCancellationRequested) {
                Sleep(backoff);
                backoff = Math.Min(backoff * 2, MaxBackoffMs);
            }
        }
    }

    private bool ShouldConnect() => _settings.UsingDualComputer && _authenticationProvider.HasToken();

    // Watches the enabling conditions while the receive loop runs, so turning the setting off or
    // logging out tears the connection down promptly instead of leaving it until the socket
    // happens to error.
    private void SuperviseConnection() {
        var receiveTask = Task.Run(ReceiveLoop);

        while (_running && !_cts.IsCancellationRequested) {
            if (receiveTask.IsCompleted) break;   // closed or dropped
            if (!ShouldConnect()) break;
            Sleep(1000);
        }

        // Abort any in-flight receive so the task can unwind, then wait for it.
        CloseSocket();
        try { receiveTask.Wait(2000); }
        catch (Exception) { /* faults with the expected abort */ }
    }

    private void Connect() {
        if (CloudUris.WebSocketUrl is null) {
            throw new InvalidOperationException("Websocket URL is not configured");
        }

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", _authenticationProvider.GetToken());
        socket.Options.SetRequestHeader("X-Api-User", _authenticationProvider.GetUser());
        socket.ConnectAsync(new Uri(CloudUris.WebSocketUrl), _cts.Token).GetAwaiter().GetResult();
        _socket = socket;
    }

    private void ReceiveLoop() {
        var socket = _socket;
        if (socket is null) return;

        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();

        try {
            while (_running && socket.State == WebSocketState.Open && !_cts.IsCancellationRequested) {
                stream.SetLength(0);
                WebSocketReceiveResult result;
                do {
                    result = socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token)
                        .GetAwaiter().GetResult();

                    if (result.MessageType == WebSocketMessageType.Close) {
                        socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
                            .GetAwaiter().GetResult();
                        return;
                    }

                    stream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                HandleMessage(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
            }
        }
        catch (Exception) {
            // Aborted during teardown, or a network error. The supervisor reconnects if it should.
        }
    }

    /// <summary>Applies one frame. Internal so the tests can drive it without a socket.</summary>
    internal void HandleMessage(string json) {
        WsEnvelope? envelope;
        try {
            envelope = CloudJson.Deserialize<WsEnvelope>(json);
        }
        catch (Exception) {
            return;   // malformed: dropped, the REST sync carries it instead
        }

        if (envelope is null) return;

        switch (envelope.Type) {
            case "item":
                HandleIncomingItems(envelope.Items);
                break;
            case "delete":
                HandleIncomingDeletions(envelope.Removed);
                break;
        }
    }

    private void HandleIncomingItems(List<CloudItemDto>? items) {
        if (items is null || items.Count == 0) return;

        // The same item also arrives over REST, so without deduplicating by cloud id the two
        // paths together produce two copies. Locally deleted items are skipped for the same
        // reason the REST download skips them: the deletion has not been reported yet.
        var known = _playerItemDao.GetOnlineIds().ToHashSet(StringComparer.Ordinal);
        var deleted = _playerItemDao.GetItemsMarkedForOnlineDeletion()
            .Select(item => item.Id)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        var toStore = items
            .Where(item => !string.IsNullOrEmpty(item.Id))
            .Where(item => !known.Contains(item.Id!) && !deleted.Contains(item.Id!))
            .Select(ItemConverter.ToPlayerItem)   // marked synchronised, so never re-uploaded
            .ToList();

        if (toStore.Count == 0) return;

        _playerItemDao.Save(toStore);
        OnItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleIncomingDeletions(List<DeleteItemDto>? removed) {
        if (removed is null || removed.Count == 0) return;

        var toDelete = removed.Where(item => !string.IsNullOrEmpty(item.Id)).ToList();
        if (toDelete.Count == 0) return;

        _playerItemDao.Delete(toDelete);
        OnItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pushes freshly looted items to the user's other machines. Items without a cloud id are
    /// skipped: a peer receiving one could not deduplicate it against the REST copy that follows.
    /// </summary>
    public void SendItems(IList<CloudItem> items) {
        if (!IsConnected) return;

        var withCloudId = items.Where(item => !string.IsNullOrEmpty(item.CloudId)).ToList();
        if (withCloudId.Count == 0) return;

        foreach (var batch in BatchUtil.ToBatches(withCloudId)) {
            Send(new WsEnvelope {
                Type = "item",
                Items = batch.Select(ItemConverter.ToUpload).ToList(),
            });
        }
    }

    /// <summary>
    /// Pushes in-game transfers, so the item disappears from the other machine before it can be
    /// transferred a second time and duplicated in the game.
    /// </summary>
    public void SendDeletions(IList<string> cloudIds) {
        if (!IsConnected) return;

        var ids = cloudIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
        if (ids.Count == 0) return;

        foreach (var batch in BatchUtil.ToBatches(ids)) {
            Send(new WsEnvelope {
                Type = "delete",
                Removed = batch.Select(id => new DeleteItemDto { Id = id }).ToList(),
            });
        }
    }

    private void Send(WsEnvelope envelope) {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;

        byte[] bytes;
        try {
            // camelCase with nulls dropped: the live-sync shape, not the REST one. See CloudJson.
            bytes = Encoding.UTF8.GetBytes(CloudJson.SerializeLive(envelope));
        }
        catch (Exception) {
            return;
        }

        // A websocket allows only one writer at a time.
        _sendLock.Wait();
        try {
            socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token)
                .GetAwaiter().GetResult();
        }
        catch (Exception) {
            // Dropped mid-send. The REST sync carries it.
        }
        finally {
            _sendLock.Release();
        }
    }

    private void Sleep(int milliseconds) {
        try { _cts.Token.WaitHandle.WaitOne(milliseconds); }
        catch (Exception) { /* cancelled */ }
    }

    private void CloseSocket() {
        var socket = _socket;
        _socket = null;
        if (socket is null) return;

        try {
            // Abort rather than a graceful close: a graceful one awaits the close handshake on
            // the receive side this thread is already occupying, which deadlocks.
            socket.Abort();
        }
        catch (Exception) { /* best effort */ }
        finally {
            socket.Dispose();
        }
    }

    public void Dispose() {
        _running = false;
        try { _cts.Cancel(); }
        catch (Exception) { /* already disposed */ }
        CloseSocket();
    }

    /// <summary>One frame. Exactly one of <see cref="Items"/> / <see cref="Removed"/> is set.</summary>
    internal sealed class WsEnvelope {
        public string Type { get; set; } = string.Empty;
        public List<CloudItemDto>? Items { get; set; }
        public List<DeleteItemDto>? Removed { get; set; }
    }
}
