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
/// - One attempt at a time, never overlapping.
/// - A quiet period between attempts that grows on repeated failure, because a game that is not
///   ready now is usually not ready two seconds later either.
/// - "Not ready" is treated as ordinary and keeps a short interval, since it means the game is
///   loading and will become hookable shortly. A hard failure backs off much further.
/// - State resets when the game restarts, so a new session starts eager again.
///
/// Retrying at all is only safe because the hook unloads itself when it declines (DllMain
/// returns FALSE) and refuses a second copy through a named mutex. See <see cref="HookAttacher"/>.
/// </summary>
public sealed class AutoAttachService {
    /// <summary>After the game appears but is not yet hookable. Short: it is about to be.</summary>
    private static readonly TimeSpan NotReadyDelay = TimeSpan.FromSeconds(10);

    /// <summary>First delay after a real failure, doubling up to <see cref="MaxFailureDelay"/>.</summary>
    private static readonly TimeSpan InitialFailureDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxFailureDelay = TimeSpan.FromMinutes(5);

    private readonly HookAttacher _attacher;
    private readonly Func<DateTime?, bool> _isHookLive;

    private bool _attaching;
    private DateTime _nextAttempt = DateTime.MinValue;
    private TimeSpan _failureDelay = InitialFailureDelay;
    private DateTime? _sessionStartedAt;
    private int _attempts;
    private string? _lastMessage;

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
            _attempts = 0;
            _lastMessage = null;
        }

        if (!enabled || _attaching || gameStartedAt is null) return null;
        if (!_attacher.IsAvailable) return null;
        if (_isHookLive(gameStartedAt)) return null;
        if (DateTime.UtcNow < _nextAttempt) return null;

        _attaching = true;
        _attempts++;
        try {
            var result = await _attacher.AttachAsync(gameStartedAt, cancellationToken);
            _lastMessage = result.Detail;

            switch (result.Outcome) {
                case AttachOutcome.Attached:
                    _failureDelay = InitialFailureDelay;
                    return $"Hook attached to Grim Dawn ({result.Detail}).";

                case AttachOutcome.NotReady:
                    _nextAttempt = DateTime.UtcNow + NotReadyDelay;
                    // Deliberately silent: this is the normal state while a game loads, and
                    // saying so every ten seconds would be noise, not information.
                    return null;

                default:
                    _nextAttempt = DateTime.UtcNow + _failureDelay;
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
