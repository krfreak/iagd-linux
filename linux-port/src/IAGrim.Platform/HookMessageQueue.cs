namespace IAGrim.Platform;

/// <summary>
/// What the hook sends the host. Upstream receives these as WM_COPYDATA; under Proton they
/// arrive as files. Only the values this port acts on are named — see the hook's MessageType.h
/// for the full set, and tools/probe for a reader that decodes all of them.
/// </summary>
public enum HookMessage {
    WorkerThreadLaunched = 3,
    HookedSuccessfully = 52,

    /// <summary>
    /// The DLL loaded, found the game unready, and unloaded itself. Written on every aborted
    /// attach, alongside the .ABORTED marker the attach script reads synchronously.
    /// </summary>
    InjectionCancelled = 8100,
}

/// <summary>One drained message: its type, and how many bytes of payload followed.</summary>
public readonly record struct HookMessageRecord(int Type, int PayloadLength) {
    public HookMessage? Known =>
        Enum.IsDefined(typeof(HookMessage), Type) ? (HookMessage)Type : null;
}

/// <summary>
/// Drains <c>linuxhack/*.msg</c>, the hook → host channel.
///
/// **Nothing consumed these before, and they are not free.** The hook writes one per event and
/// never cleans up, so they accumulate for the lifetime of the install: 522 files had built up
/// here across a few days, 391 of them injection-cancelled notices. Upstream's host consumes
/// every message as it arrives because on Windows they are window messages and there is nothing
/// to consume — the file form is this port's, and so is the obligation to empty it.
///
/// Reading is deliberately shallow. Only the 8-byte header is parsed, because that is all
/// anything here acts on; the payloads carry hardcore state and mod names that this port already
/// gets from the loot CSV, and inventing a second source for them would be a feature upstream's
/// file bridge does not ask for. If one is ever needed, the payload length is already reported.
/// </summary>
public sealed class HookMessageQueue {
    private readonly PrefixBridge _bridge;

    public HookMessageQueue(PrefixBridge bridge) {
        _bridge = bridge;
    }

    /// <summary>
    /// Reads and deletes every complete message. Returns them oldest first.
    ///
    /// A file too short to hold a header is deleted rather than kept: it is either a truncated
    /// write from a game that died mid-message, or not ours, and either way retrying it forever
    /// is worse than dropping it. Files still being written are skipped by the mtime check —
    /// the hook writes to .tmp and renames, so a .msg is normally complete on arrival.
    /// </summary>
    public IReadOnlyList<HookMessageRecord> Drain() {
        var directory = _bridge.LinuxHack;
        if (!Directory.Exists(directory)) return [];

        var drained = new List<HookMessageRecord>();

        foreach (var file in Directory.GetFiles(directory, "*.msg")
                                      .OrderBy(File.GetLastWriteTimeUtc)) {
            byte[] bytes;
            try {
                bytes = File.ReadAllBytes(file);
            }
            catch (IOException) {
                continue;   // mid-write, or gone; next pass
            }

            if (bytes.Length >= 8) {
                drained.Add(new HookMessageRecord(
                    BitConverter.ToInt32(bytes, 0),
                    BitConverter.ToInt32(bytes, 4)));
            }

            try { File.Delete(file); }
            catch (IOException) { /* next pass */ }
        }

        return drained;
    }
}
