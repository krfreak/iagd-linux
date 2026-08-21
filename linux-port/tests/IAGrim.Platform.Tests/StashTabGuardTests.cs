using Xunit;

namespace IAGrim.Platform.Tests;

/// <summary>
/// The rule PUT /api/settings enforces before it saves. Upstream's stash picker refuses to
/// close over the same collision (StashTabPicker.cs); this port has no modal to block a save
/// on, so ApiRouter refuses the save itself instead. See StashTabGuard for the full reasoning.
/// </summary>
public class StashTabGuardTests {
    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(12, 12)]
    public void TheSameNonZeroTabOnBothSidesCollides(int lootFrom, int depositTo) {
        Assert.True(StashTabGuard.Collide(lootFrom, depositTo));
    }

    /// <summary>
    /// 0 means "the last tab" for either setting, not a specific one — upstream's default, and
    /// upstream exempts the same case from its own guard.
    /// </summary>
    [Fact]
    public void ZeroOnBothSidesIsExempt() {
        Assert.False(StashTabGuard.Collide(0, 0));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(1, 2)]
    public void DifferentTabsOrOneLeftAtTheDefaultDoNotCollide(int lootFrom, int depositTo) {
        Assert.False(StashTabGuard.Collide(lootFrom, depositTo));
    }
}
