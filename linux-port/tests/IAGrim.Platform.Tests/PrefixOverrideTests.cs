using IAGrim.Platform;
using Xunit;

namespace IAGrim.Platform.Tests;

/// <summary>
/// Naming a Proton prefix by hand.
///
/// Discovery only knows the layout Steam uses — <c>steamapps/compatdata/219990/pfx</c> under a
/// library listed in libraryfolders.vdf — and when it comes up empty there is no channel to the
/// hook at all: nothing is looted, nothing can be transferred back, and no amount of clicking in
/// the client changes that. This is the way out, so what it accepts matters more than usual.
/// </summary>
public class PrefixOverrideTests : IDisposable {
    private readonly string _root = Directory.CreateTempSubdirectory("iagd-prefix-").FullName;

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Make(params string[] parts) {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// The compatdata folder — what Steam calls the prefix, what Proton is given as
    /// STEAM_COMPAT_DATA_PATH, and what someone copying a path out of their Steam library ends
    /// up with.
    /// </summary>
    [Fact]
    public void ACompatDataFolderResolvesToTheBridgeInsideIt() {
        var compatData = Make("compatdata", "219990");
        Make("compatdata", "219990", "pfx", "drive_c");

        var bridge = PrefixBridge.ForPrefix(compatData);

        Assert.NotNull(bridge);
        Assert.Equal(SteamPaths.BridgeDirIn(Path.Combine(compatData, "pfx")), bridge!.Root);
        Assert.Equal(compatData, bridge.CompatData);
    }

    /// <summary>
    /// The prefix itself, which is what "WINEPREFIX" means everywhere else and therefore the
    /// other half of what people type. Both have to work: a setting that silently does nothing
    /// for one of the two spellings is worse than no setting, because it looks like it took.
    /// </summary>
    [Fact]
    public void ThePrefixItselfResolvesToo() {
        var prefix = Make("compatdata", "219990", "pfx");
        Make("compatdata", "219990", "pfx", "drive_c");
        File.WriteAllText(Path.Combine(_root, "compatdata", "219990", "config_info"), "proton\n");

        var bridge = PrefixBridge.ForPrefix(prefix);

        Assert.NotNull(bridge);
        Assert.Equal(SteamPaths.BridgeDirIn(prefix), bridge!.Root);
        Assert.Equal(Path.Combine(_root, "compatdata", "219990"), bridge.CompatData);
    }

    /// <summary>
    /// A prefix that Steam did not make has no compatdata folder above it. The bridge still
    /// works — it is plain file I/O inside drive_c — but the attach path has nothing to hand
    /// Proton, and claiming otherwise would send the injector at a directory chosen by
    /// coincidence.
    /// </summary>
    [Fact]
    public void APrefixOutsideCompatDataReportsNoCompatDataRatherThanGuessing() {
        var prefix = Make("lutris", "grim-dawn");
        Make("lutris", "grim-dawn", "drive_c");

        var bridge = PrefixBridge.ForPrefix(prefix);

        Assert.NotNull(bridge);
        Assert.Null(bridge!.CompatData);

        // Still nameable, so the settings page can show what is in use rather than falling back
        // to whatever discovery would have picked — a different prefix entirely.
        Assert.Equal(prefix, bridge.Prefix);
    }

    /// <summary>
    /// Anything that is not a prefix is refused, so the host can say so instead of quietly
    /// building a bridge onto a path where no hook will ever write.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyIsNotAPrefix(string path) => Assert.Null(PrefixBridge.ForPrefix(path));

    [Fact]
    public void AFolderWithNeitherDriveCNorPfxIsNotAPrefix() =>
        Assert.Null(PrefixBridge.ForPrefix(Make("some", "downloads")));
}
