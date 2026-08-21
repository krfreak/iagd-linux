namespace IAGrim.Platform;

/// <summary>
/// Decides when the hardcore state the hook reports is worth telling anyone about.
///
/// The hook says so freely — <c>SetHardcore</c> sends it when the mode is set, and item
/// initialisation sends the same value again — so the messages are a stream of assertions rather
/// than a stream of changes. Something has to turn one into the other before a collection view
/// follows it, and doing that inline in the import loop put three separate judgements in the
/// middle of a method about loot.
///
/// It is stateful and not thread-safe by design: there is one import loop, it is the only caller,
/// and the whole point is remembering what was said last.
/// </summary>
public sealed class HardcoreWatch {
    private bool _sweptTheBacklog;
    private bool? _announced;

    /// <summary>
    /// The state worth announcing from one drained batch, or null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Three rules, each of which exists because of a way this goes wrong:
    ///
    /// **The first batch is swallowed whole.** Nothing consumed the hook's channel before this
    /// port, so the first drain of a session clears everything the DLL has ever written — 522
    /// files on one install, some of them weeks old. Acting on that would open the app on
    /// whatever stash was last played rather than the one being played now.
    ///
    /// **Last one wins.** A pass can drain several, <see cref="HookMessageQueue.Drain"/> returns
    /// them oldest first, and only the newest describes the present.
    ///
    /// **Only changes are announced.** Otherwise every pass that happened to drain a message
    /// would reassert the same value, and a filter that reset itself every two seconds would
    /// fight anyone trying to look at their other stash.
    /// </remarks>
    public bool? Observe(IEnumerable<HookMessageRecord> drained) {
        var reported = drained.Select(message => message.Hardcore)
                              .LastOrDefault(state => state is not null);

        if (!_sweptTheBacklog) {
            // Remembered, not announced: the backlog is stale as news but still the best guess
            // at what is running, so a later message repeating it is correctly treated as "no
            // change" rather than as the first thing worth saying.
            _sweptTheBacklog = true;
            _announced = reported;
            return null;
        }

        if (reported is not bool hardcore || hardcore == _announced) return null;

        _announced = hardcore;
        return hardcore;
    }
}
