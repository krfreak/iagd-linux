namespace IAGrim.Platform;

/// <summary>What the auto-attach loop is currently doing, for the UI and the CLI.</summary>
public sealed record AutoAttachState(bool Enabled, bool Attaching, int Attempts, string? LastMessage);

/// <summary>
/// Watches for Grim Dawn and attaches the hook when it appears.
///
/// Without this, capturing loot needs a terminal and a script run at the right moment. The hook
/// cannot be attached at launch — that was established early and is why `--attach-name` exists —
/// so something has to notice the game and act, and a poll is the honest way to do it: there is
/// no notification to subscribe to when a Wine process reaches the point of being hookable.
///
/// **The pacing is the whole design.** A naive "attach whenever the hook is not live" retries
/// every couple of seconds forever, launching a Proton process each time, while the player sits
/// in character select. So:
///
/// - Nothing at all until the game has been up for <see cref="MinimumGameAge"/>. See below.
/// - One attempt at a time, never overlapping.
/// - A quiet period between attempts that grows on repeated failure, because a game that is not
///   ready now is usually not ready two seconds later either.
/// - "Not ready" keeps a short interval that still grows, since it means the game is loading and
///   will become hookable shortly. A hard failure backs off much further.
/// - State resets when the game restarts, so a new session starts eager again.
///
/// Retrying at all is only safe because the hook unloads itself when it declines (DllMain
/// returns FALSE) and refuses a second copy through a named mutex. See <see cref="HookAttacher"/>.
/// </summary>
public sealed class AutoAttachService {
    /// <summary>
    /// How long the game must have been running before the first attempt.
    ///
    /// **This is the one that stops crashing the game.** The DLL can only decline *after* it is
    /// already mapped into the process: DllMain resolves game.dll's exports and dereferences
    /// gGameEngine to ask IsGameLoading/IsGameWaiting/IsGameEngineOnline. During the game's
    /// initial load that engine object is still being built by the main thread, and we are
    /// reading it from an injected remote thread while the loader is busy. Sometimes it survives;
    /// sometimes the game dies. Every crash report collected here ends in the same place — just
    /// after "Renderer Initialized", before the main menu — which is precisely that window.
    ///
    /// The attach script waits for the game's *window*, and under Proton the window exists
    /// seconds into startup, long before anything is safe to touch. So the window cannot be the
    /// gate; time has to be.
    ///
    /// 45 s costs nothing real. The earliest a character can actually be in the world — launch,
    /// main menu, character select, load — is beyond it, and the hook has nothing to capture
    /// before then anyway. A player who starts this app while already in-game is unaffected: the
    /// game's start time is already well in the past on the first poll.
    /// </summary>
    public static readonly TimeSpan MinimumGameAge = TimeSpan.FromSeconds(45);

    /// <summary>
    /// After the game appears but is not yet hookable.
    ///
    /// This interval is what a player actually feels: the DLL refuses to attach at the main menu
    /// and at character select, so the wait between attempts is the wait between loading a
    /// character and the hook going live. Upstream retries roughly once a second — it can, since
    /// its check is an in-process FindWindow and its injector is a one-second subprocess. Here
    /// each attempt launches Proton, which costs seconds of CPU.
    ///
    /// It grows rather than staying flat, because a flat 8 s is not a pace, it is a drip: a
    /// player sitting in character select for four minutes collected seventeen injections, and
    /// the recorded total across a few days of use was 391 refusals against 12 successful
    /// attaches. Each of those is a full DLL load into a live game. Growing to a 30 s ceiling
    /// keeps the worst case a player can feel — loading a character and waiting for the hook —
    /// bounded, while making a long sit in the menus cost a handful of attempts instead of dozens.
    /// </summary>
    private static readonly TimeSpan InitialNotReadyDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MaxNotReadyDelay = TimeSpan.FromSeconds(30);

    /// <summary>First delay after a real failure, doubling up to <see cref="MaxFailureDelay"/>.</summary>
    private static readonly TimeSpan InitialFailureDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxFailureDelay = TimeSpan.FromMinutes(5);

    private readonly HookAttacher _attacher;
    private readonly Func<DateTime?, bool> _isHookLive;

    private bool _attaching;
    private DateTime _nextAttempt = DateTime.MinValue;
    private TimeSpan _failureDelay = InitialFailureDelay;
    private TimeSpan _notReadyDelay = InitialNotReadyDelay;
    private DateTime? _sessionStartedAt;
    private int _attempts;
    private string? _lastMessage;

    /// <summary>Overridable so the pacing can be tested without a 45-second wait per case.</summary>
    internal TimeSpan MinimumAge { get; init; } = MinimumGameAge;

    /// <summary>
    /// Injectable clock, for the same reason. **Local time, not UTC**: it is compared against
    /// the game's start time, which comes from Process.StartTime — local — and against marker
    /// mtimes read with File.GetLastWriteTime, also local. Mixing the two would put the whole
    /// pacing out by the machine's UTC offset, which here would have meant a two-hour-old
    /// marker counting as this session's.
    /// </summary>
    internal Func<DateTime> Now { get; init; } = () => DateTime.Now;

    public AutoAttachService(PrefixBridge bridge, HookAttacher? attacher = null)
        : this(bridge.IsHookLive, attacher ?? new HookAttacher(bridge)) { }

    /// <summary>For tests: supply both the "is it attached" check and the attach step.</summary>
    public AutoAttachService(Func<DateTime?, bool> isHookLive, HookAttacher attacher) {
        _isHookLive = isHookLive;
        _attacher = attacher;
    }

    public AutoAttachState State(bool enabled) => new(enabled, _attaching, _attempts, _lastMessage);

    /// <summary>
    /// One turn of the loop. Returns a message when something happened worth telling the user
    /// about, and null when it did nothing — which is most of the time.
    /// </summary>
    /// <param name="gameStartedAt">When the running game started, or null if it is not running.</param>
    public async Task<string?> PollAsync(DateTime? gameStartedAt, bool enabled,
                                         CancellationToken cancellationToken) {
        // A different game session: forget everything learned about the last one.
        if (gameStartedAt != _sessionStartedAt) {
            _sessionStartedAt = gameStartedAt;
            _nextAttempt = DateTime.MinValue;
            _failureDelay = InitialFailureDelay;
            _notReadyDelay = InitialNotReadyDelay;
            _attempts = 0;
            _lastMessage = null;
        }

        if (!enabled || _attaching || gameStartedAt is null) return null;
        if (!_attacher.IsAvailable) return null;

        // Too early to touch it. Silent: this is the first three quarters of a minute of every
        // session, and it is not a state anyone needs told about.
        if (Now() - gameStartedAt.Value < MinimumAge) return null;

        if (_isHookLive(gameStartedAt)) return null;
        if (Now() < _nextAttempt) return null;

        _attaching = true;
        _attempts++;
        try {
            var result = await _attacher.AttachAsync(gameStartedAt, cancellationToken);
            _lastMessage = result.Detail;

            switch (result.Outcome) {
                case AttachOutcome.Attached:
                    _failureDelay = InitialFailureDelay;
                    _notReadyDelay = InitialNotReadyDelay;
                    return $"Hook attached to Grim Dawn ({result.Detail}).";

                case AttachOutcome.NotReady:
                    _nextAttempt = Now() + _notReadyDelay;
                    _notReadyDelay = _notReadyDelay * 1.5 > MaxNotReadyDelay
                        ? MaxNotReadyDelay
                        : _notReadyDelay * 1.5;
                    // Deliberately silent: this is the normal state while a game loads, and
                    // saying so every ten seconds would be noise, not information.
                    return null;

                default:
                    _nextAttempt = Now() + _failureDelay;
                    var delay = _failureDelay;
                    _failureDelay = _failureDelay * 2 > MaxFailureDelay ? MaxFailureDelay : _failureDelay * 2;
                    return $"Could not attach the hook: {result.Detail}. Retrying in {delay.TotalSeconds:F0}s.";
            }
        }
        finally {
            _attaching = false;
        }
    }
}
