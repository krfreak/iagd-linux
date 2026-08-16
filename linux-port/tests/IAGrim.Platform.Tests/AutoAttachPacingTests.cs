using IAGrim.Platform;
using Xunit;

namespace IAGrim.Platform.Tests;

/// <summary>
/// When an attach is attempted, and against what evidence.
///
/// This is the part that crashed Grim Dawn. Not a faulty injection — a correct one, fired while
/// the game was still loading, repeatedly. The DLL can only decline after it is already inside
/// the process, so "do not attempt yet" is the only protection that exists, and it lives here.
/// </summary>
public class AutoAttachPacingTests {
    /// <summary>A clock the test moves by hand, so pacing is asserted rather than waited out.</summary>
    private sealed class Clock {
        public DateTime Now = new(2026, 8, 16, 17, 0, 0, DateTimeKind.Local);
        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>
    /// Records what it was asked to do and answers however the test wants. Timestamps come from
    /// the fake clock, not the wall clock — the gaps between attempts are the whole assertion.
    /// </summary>
    private sealed class FakeAttacher : HookAttacher {
        private readonly Queue<AttachOutcome> _outcomes;
        private readonly Clock _clock;

        public FakeAttacher(Clock clock, params AttachOutcome[] outcomes) {
            _clock = clock;
            _outcomes = new Queue<AttachOutcome>(outcomes);
        }

        public List<DateTime> Attempts { get; } = [];
        public override bool IsAvailable => true;

        public override Task<AttachResult> AttachAsync(DateTime? gameStartedAt, CancellationToken token) {
            Attempts.Add(_clock.Now);
            var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : AttachOutcome.NotReady;
            return Task.FromResult(new AttachResult(outcome, "test"));
        }
    }

    private static (AutoAttachService, FakeAttacher, Clock) Build(
        bool hookLive = false, params AttachOutcome[] outcomes) {
        var clock = new Clock();
        var attacher = new FakeAttacher(clock, outcomes);
        var service = new AutoAttachService(_ => hookLive, attacher) { Now = () => clock.Now };
        return (service, attacher, clock);
    }

    private static Task<string?> Poll(AutoAttachService service, DateTime gameStartedAt) =>
        service.PollAsync(gameStartedAt, enabled: true, CancellationToken.None);

    /// <summary>
    /// The crash. A game that has just started must not be touched, however many times the loop
    /// comes round — and the loop comes round every couple of seconds.
    /// </summary>
    [Fact]
    public async Task NothingIsInjectedIntoAGameThatJustStarted() {
        var (service, attacher, clock) = Build();
        var gameStarted = clock.Now;

        for (var i = 0; i < 30; i++) {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Poll(service, gameStarted);
        }

        Assert.Empty(attacher.Attempts);
        Assert.True(clock.Now - gameStarted < AutoAttachService.MinimumGameAge);
    }

    [Fact]
    public async Task TheFirstAttemptWaitsForTheMinimumGameAge() {
        var (service, attacher, clock) = Build();
        var gameStarted = clock.Now;

        clock.Advance(AutoAttachService.MinimumGameAge - TimeSpan.FromSeconds(1));
        await Poll(service, gameStarted);
        Assert.Empty(attacher.Attempts);

        clock.Advance(TimeSpan.FromSeconds(2));
        await Poll(service, gameStarted);
        Assert.Single(attacher.Attempts);
    }

    /// <summary>
    /// Someone who starts this app while already playing has a game that is minutes old, and
    /// must not be made to wait again.
    /// </summary>
    [Fact]
    public async Task AGameAlreadyRunningIsAttachedAtOnce() {
        var (service, attacher, clock) = Build();

        await Poll(service, clock.Now - TimeSpan.FromMinutes(10));

        Assert.Single(attacher.Attempts);
    }

    /// <summary>
    /// Refusals used to cost a flat 8 s each: seventeen injections for a four-minute sit in
    /// character select. The interval has to grow, and it has to stay bounded so that loading a
    /// character is not followed by a long wait for capture to start.
    /// </summary>
    [Fact]
    public async Task RepeatedRefusalsBackOffAndThenHoldSteady() {
        var (service, attacher, clock) = Build(hookLive: false);
        var gameStarted = clock.Now - TimeSpan.FromMinutes(5);

        var gaps = new List<TimeSpan>();
        for (var i = 0; i < 8; i++) {
            var before = attacher.Attempts.Count;
            // Walk the clock forward a second at a time until the service tries again.
            while (attacher.Attempts.Count == before) {
                clock.Advance(TimeSpan.FromSeconds(1));
                await Poll(service, gameStarted);
                Assert.True(clock.Now - gameStarted < TimeSpan.FromHours(1), "never retried");
            }
            if (attacher.Attempts.Count > 1) {
                gaps.Add(attacher.Attempts[^1] - attacher.Attempts[^2]);
            }
        }

        Assert.Equal(8, attacher.Attempts.Count);

        // Growing, never shrinking, and capped.
        for (var i = 1; i < gaps.Count; i++) {
            Assert.True(gaps[i] >= gaps[i - 1], $"gap {i} shrank: {gaps[i - 1]} -> {gaps[i]}");
        }
        Assert.True(gaps[^1] <= TimeSpan.FromSeconds(31), $"unbounded backoff: {gaps[^1]}");

        // The old flat pace would have fired far more often over the same stretch.
        var elapsed = attacher.Attempts[^1] - attacher.Attempts[0];
        Assert.True(elapsed > TimeSpan.FromSeconds(8 * 7),
                    $"backoff bought nothing: 8 attempts in {elapsed}");
    }

    /// <summary>A successful attach must not leave the next session paced like a failing one.</summary>
    [Fact]
    public async Task ANewSessionStartsEagerAgain() {
        var (service, attacher, clock) = Build(hookLive: false,
            AttachOutcome.NotReady, AttachOutcome.NotReady, AttachOutcome.Attached);

        var first = clock.Now - TimeSpan.FromMinutes(5);
        for (var i = 0; i < 120; i++) {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Poll(service, first);
        }
        var duringFirstSession = attacher.Attempts.Count;
        Assert.True(duringFirstSession >= 3);

        // The game restarts: a different start time, and a game that is brand new again.
        var second = clock.Now;
        clock.Advance(TimeSpan.FromSeconds(5));
        await Poll(service, second);
        Assert.Equal(duringFirstSession, attacher.Attempts.Count);   // still too young

        clock.Advance(AutoAttachService.MinimumGameAge);
        await Poll(service, second);
        Assert.Equal(duringFirstSession + 1, attacher.Attempts.Count);
    }

    /// <summary>A live hook is left alone; a second copy is what crashes the game.</summary>
    [Fact]
    public async Task AnAttachedHookIsNeverInjectedAgain() {
        var (service, attacher, clock) = Build(hookLive: true);
        var gameStarted = clock.Now - TimeSpan.FromMinutes(5);

        for (var i = 0; i < 60; i++) {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Poll(service, gameStarted);
        }

        Assert.Empty(attacher.Attempts);
    }

    [Fact]
    public async Task NothingHappensWhileTheGameIsNotRunning() {
        var (service, attacher, clock) = Build();

        for (var i = 0; i < 20; i++) {
            clock.Advance(TimeSpan.FromSeconds(10));
            await service.PollAsync(null, enabled: true, CancellationToken.None);
        }

        Assert.Empty(attacher.Attempts);
    }
}
