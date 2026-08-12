using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace IAGrim.Host;

/// <summary>
/// Broadcasts events to connected UIs.
///
/// Replaces upstream's <c>ExecuteScriptAsync("window.message(...)")</c>, which is how the
/// WinForms host pushed into its embedded WebView2. The payload shape is deliberately the
/// same idea — a typed envelope the UI switches on — so the existing Preact
/// <c>window.message</c> entry point needs a new transport, not a rewrite.
/// </summary>
public sealed class EventHub {
    private readonly List<WebSocket> _sockets = [];
    private readonly Lock _gate = new();

    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public int ConnectionCount {
        get { lock (_gate) return _sockets.Count; }
    }

    public void Add(WebSocket socket) {
        lock (_gate) _sockets.Add(socket);
    }

    public void Remove(WebSocket socket) {
        lock (_gate) _sockets.Remove(socket);
    }

    public async Task BroadcastAsync(HostEvent hostEvent, CancellationToken cancellationToken = default) {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hostEvent, Json));

        List<WebSocket> targets;
        lock (_gate) targets = [.. _sockets];

        foreach (var socket in targets) {
            if (socket.State != WebSocketState.Open) {
                Remove(socket);
                continue;
            }

            try {
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            catch (WebSocketException) {
                // The UI went away mid-send; drop it rather than letting one dead socket
                // block delivery to the others.
                Remove(socket);
            }
        }
    }
}
