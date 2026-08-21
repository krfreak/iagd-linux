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
    /// The game called <c>GameInfo::SetHardcore</c>. One byte of payload: whether the character
    /// now being played is hardcore.
    /// </summary>
    GameInfoIsHardcore = 20,

    /// <summary>
    /// The same value, reported when an item is initialised rather than when the mode is set —
    /// <c>InventorySack_AddItem.cpp:179</c>. Upstream treats the two identically and so does
    /// this port; it exists because the mode is often already set by the time anything hooks it.
    /// </summary>
    GameInfoIsHardcoreViaInit = 47,

    /// <summary>
    /// The DLL loaded, found the game unready, and unloaded itself. Written on every aborted
    /// attach, alongside the .ABORTED marker the attach script reads synchronously.
    /// </summary>
    InjectionCancelled = 8100,
}

/// <summary>One drained message: its type, how many bytes of payload followed, and those bytes.</summary>
public readonly record struct HookMessageRecord(int Type, int PayloadLength, byte[] Payload) {
    public HookMessage? Known =>
        Enum.IsDefined(typeof(HookMessage), Type) ? (HookMessage)Type : null;

    /// <summary>
    /// The payload as the one-byte boolean the two hardcore messages carry, or null for anything
    /// else. <c>SetHardcore::HookedMethod</c> sends <c>sizeof(bool)</c>, so a record of any other
    /// length is not one of these however its type field reads, and is refused rather than
    /// guessed at.
    /// </summary>
    public bool? Hardcore =>
        Known is HookMessage.GameInfoIsHardcore or HookMessage.GameInfoIsHardcoreViaInit
        && Payload.Length == 1
            ? Payload[0] != 0
            : null;
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
/// Reading used to stop at the 8-byte header, on the grounds that the payloads carried hardcore
/// state and mod names the loot CSV already supplies. That is true for an item that has been
/// looted and useless before one has: the hardcore messages are how the game says which stash is
/// being played *now*, with no loot to hang it on, and the collection view follows them. The
/// payload is small — one byte for the messages anything acts on — so it is carried rather than
/// re-read on demand.
///
/// There is no mod name here to go with it. <c>TYPE_GameInfo_SetModName</c> is declared in
/// <c>MessageType.h</c> and **no code in either tree ever sends it**; the hook resolves the mod
/// name internally to pick a loot folder and never puts it on the wire. See BACKLOG entry 9.
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
                // The declared length is the hook's, and the file's is what actually arrived.
                // They agree unless a write was cut short, and trusting the declared one would
                // mean copying past the end of the array; taking the smaller keeps a truncated
                // message readable as far as it got rather than throwing.
                var declared = BitConverter.ToInt32(bytes, 4);
                var available = Math.Clamp(declared, 0, bytes.Length - 8);

                drained.Add(new HookMessageRecord(
                    BitConverter.ToInt32(bytes, 0),
                    declared,
                    bytes[8..(8 + available)]));
            }

            try { File.Delete(file); }
            catch (IOException) { /* next pass */ }
        }

        return drained;
    }
}
