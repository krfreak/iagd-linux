using Xunit;

namespace IAGrim.Platform.Tests;

/// <summary>
/// Settings-page-owned keys added to match upstream toggles this port used to hardcode.
///
/// Each one's default deliberately picks whichever value keeps this port's existing behaviour
/// unchanged for someone who has never opened the settings page, even where that is not
/// upstream's own default for a fresh install — see the doc comments on each property for why.
/// A regression here silently flips a checkbox nobody touched, which is the failure mode
/// BridgeSettingsTests worries about for the hook's settings and this guards for ours.
/// </summary>
public class AppSettingsTests {
    [Fact]
    public void ANewInstallLeavesGrantedSkillsShowing() {
        // Upstream hides them by default; this port never has, so hiding on the first run after
        // an update would be a surprise nobody asked for.
        Assert.False(new AppSettings().HideSkills);
    }

    /// <summary>
    /// <see cref="AppSettings.HideSkills"/> belongs to the settings page, unlike the online-sync
    /// keys <see cref="AppSettings.CarryOverUnmanaged"/> exists for. Adding it to that whitelist
    /// by mistake would mean a save could never turn it off — the stored value would always win
    /// over what the page just sent.
    /// </summary>
    [Fact]
    public void SavingDoesNotCarryHideSkillsOverFromTheStoredCopy() {
        var stored = new AppSettings { HideSkills = true };
        var incoming = new AppSettings { HideSkills = false };

        incoming.CarryOverUnmanaged(stored);

        Assert.False(incoming.HideSkills);
    }

    [Fact]
    public void HideSkillsRoundTripsThroughSaveAndLoad() {
        var path = Path.Combine(Directory.CreateTempSubdirectory("iagd-settings-").FullName, "settings.json");
        try {
            new AppSettings { HideSkills = true }.Save(path);

            Assert.True(AppSettings.Load(path).HideSkills);
        }
        finally {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
