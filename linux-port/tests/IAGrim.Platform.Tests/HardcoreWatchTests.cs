using IAGrim.Platform;
using Xunit;

namespace IAGrim.Platform.Tests;

/// <summary>
/// Turning a stream of assertions from the hook into the few changes worth acting on.
/// </summary>
public class HardcoreWatchTests {
    private static HookMessageRecord Hardcore(bool value) =>
        new((int)HookMessage.GameInfoIsHardcore, 1, [value ? (byte)1 : (byte)0]);

    private static HookMessageRecord ViaInit(bool value) =>
        new((int)HookMessage.GameInfoIsHardcoreViaInit, 1, [value ? (byte)1 : (byte)0]);

    private static HookMessageRecord Other() =>
        new((int)HookMessage.HookedSuccessfully, 4, [0, 0, 0, 7]);

    /// <summary>
    /// The first drain of a session is everything the hook ever wrote, which on one install was
    /// 522 files with weeks between the oldest and the newest. Opening the app on the stash that
    /// was being played a fortnight ago is worse than opening it on the one the collection
    /// happens to start on.
    /// </summary>
    [Fact]
    public void SaysNothingAboutTheBacklogItInherits() {
        var watch = new HardcoreWatch();

        Assert.Null(watch.Observe([Hardcore(true), Other(), ViaInit(true)]));
    }

    /// <summary>
    /// The backlog is stale as news but is still the best available guess at what is running, so
    /// a live message repeating it is not a change. Without this, the first real message after
    /// startup would always announce, whether or not anything had moved.
    /// </summary>
    [Fact]
    public void TreatsTheBacklogAsWhatIsAlreadyTrue() {
        var watch = new HardcoreWatch();
        watch.Observe([Hardcore(true)]);

        Assert.Null(watch.Observe([Hardcore(true)]));
        Assert.True(watch.Observe([Hardcore(false)]) is false);
    }

    [Fact]
    public void AnnouncesAChangeOnceRatherThanEveryPass() {
        var watch = new HardcoreWatch();
        watch.Observe([]);

        Assert.True(watch.Observe([Hardcore(true)]));
        Assert.Null(watch.Observe([Hardcore(true)]));
        Assert.Null(watch.Observe([ViaInit(true)]));
    }

    /// <summary>
    /// Drain returns oldest first, and a slow pass can cover a character being swapped. Only the
    /// newest describes the present.
    /// </summary>
    [Fact]
    public void TakesTheNewestOfSeveralInOnePass() {
        var watch = new HardcoreWatch();
        watch.Observe([]);

        Assert.True(watch.Observe([Hardcore(false), ViaInit(false), Hardcore(true)]));
    }

    /// <summary>
    /// Passes that drain only unrelated traffic must not be read as "softcore" — the two are very
    /// different to something deciding whether to move a collection view.
    /// </summary>
    [Fact]
    public void IsSilentWhenNothingReportsTheState() {
        var watch = new HardcoreWatch();
        watch.Observe([]);
        watch.Observe([Hardcore(true)]);

        Assert.Null(watch.Observe([Other()]));
        Assert.Null(watch.Observe([]));
    }

    /// <summary>
    /// The mode is often already set before anything hooks it, so the first thing a session hears
    /// can be message 47 rather than message 20. They are the same news.
    /// </summary>
    [Fact]
    public void AcceptsEitherOfTheTwoMessagesAsTheSameNews() {
        var watch = new HardcoreWatch();
        watch.Observe([]);

        Assert.True(watch.Observe([ViaInit(true)]));
        Assert.Null(watch.Observe([Hardcore(true)]));
    }
}
