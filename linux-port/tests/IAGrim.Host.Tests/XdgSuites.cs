using Xunit;

namespace IAGrim.Host.Tests;

/// <summary>
/// The suites that redirect <c>XDG_DATA_HOME</c> and <c>XDG_CONFIG_HOME</c> at a temp directory,
/// gathered into one xUnit collection so they never run at the same time.
///
/// Those variables are process-wide and <see cref="IAGrim.Platform.LinuxPaths"/> reads them on
/// every access, so two suites redirecting them in parallel do not merely interfere — each one
/// spends part of its run pointed at the *other* suite's directory, and a run that deletes files
/// (LootBackupEndpointTests) must never be one of the two. xUnit runs classes in the same
/// collection sequentially, which is exactly the guarantee needed here.
/// </summary>
[CollectionDefinition(Name)]
public sealed class XdgSuites {
    public const string Name = "xdg-redirected";
}
