namespace IAGrim.Platform;

/// <summary>
/// Sends items from the database back into the game.
///
/// The hook polls <c>itemqueue/outgoing/{hc|sc}[/mod]</c> and, while the transfer stash is
/// open, creates each item and moves the file to <c>itemqueue/deleted/...</c>. That move is
/// the only acknowledgement available — the hook reports nothing back — so a transfer is
/// only "done" once the file is gone from the outgoing directory.
///
/// Consequences worth stating plainly, because they decide how the UI must behave:
///   * Nothing happens unless the player has the transfer stash open.
///   * An item written here is committed. If it is also left in the database, the player
///     ends up with two, so removal must follow confirmation.
/// </summary>
public sealed class TransferService {
    private readonly PrefixBridge _bridge;

    public TransferService(PrefixBridge bridge) {
        _bridge = bridge;
    }

    /// <summary>
    /// Queues one item. Returns the path written, which is also the handle used to check
    /// for completion.
    /// </summary>
    /// <param name="targetHardcore">
    /// Send to a different branch than the item was looted from. Upstream gates this behind its
    /// "transfer to any mod" setting and asks with a stash picker; the effect is the same —
    /// which <c>outgoing/{hc|sc}[/mod]</c> directory the hook finds the file in.
    ///
    /// Off by default because crossing the boundary is usually a mistake: hardcore and softcore
    /// are separate stashes, and an item moved between them cannot be moved back by the game.
    /// </param>
    public string Queue(LootedItem item, bool? targetHardcore = null, string? targetMod = null) {
        var folder = _bridge.TransferToGame(targetHardcore ?? item.IsHardcore,
                                            NormaliseMod(targetMod ?? item.Mod));
        var path = Path.Combine(folder, LootCsv.NewTransferFileName());

        // Write to a temporary name and rename into place. The hook scans this directory on
        // a timer and would otherwise be able to read a half-written row — and a truncated
        // row deserialises into a different item, not an error.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, LootCsv.SerializeForGame(item) + "\n",
                          new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path);

        return path;
    }

    /// <summary>True once the hook has taken the item, i.e. the queued file is gone.</summary>
    public bool IsCollected(string queuedPath) => !File.Exists(queuedPath);

    /// <summary>
    /// Items still sitting in the queue for a given branch. A build-up here means the hook
    /// is not attached, or the player has not opened the transfer stash.
    /// </summary>
    public IEnumerable<string> Pending(bool isHardcore, string? mod = null) {
        var folder = _bridge.TransferToGame(isHardcore, NormaliseMod(mod));
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.csv")
            : [];
    }

    /// <summary>
    /// Cancels a queued transfer, if the hook has not already taken it. Returns false when
    /// the item is already in the game — at which point removing it from the database is the
    /// correct follow-up, not retrying.
    /// </summary>
    public bool Cancel(string queuedPath) {
        try {
            if (!File.Exists(queuedPath)) return false;
            File.Delete(queuedPath);
            return true;
        }
        catch (IOException) {
            return false;
        }
    }

    /// <summary>Vanilla is the empty string on both sides; keep the sub-path out of it.</summary>
    private static string? NormaliseMod(string? mod) =>
        string.IsNullOrWhiteSpace(mod) ? null : mod.Trim();
}
