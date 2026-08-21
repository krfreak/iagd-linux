using IAGrim.Platform;
using Xunit;

namespace IAGrim.Host.Tests;

/// <summary>
/// /api/open-folder's allowlist — the directory-opening counterpart to SupportLinks' URL
/// allowlist on /api/open. See KnownFolders itself for why this takes a name rather than a path.
/// </summary>
public class KnownFoldersTests : IDisposable {
    private readonly string _root = Directory.CreateTempSubdirectory("iagd-known-folders-").FullName;
    private readonly string? _previousDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public KnownFoldersTests() {
        // LinuxPaths reads XDG_DATA_HOME on every call rather than caching it, so pointing it
        // here for the test keeps this from creating or reading anything under the account
        // actually running the tests — the same reason a manual run of the host needs it done
        // by hand.
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _root);
    }

    public void Dispose() {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _previousDataHome);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void BackupsResolvesToTheBackupDirectory() {
        var resolved = KnownFolders.Resolve(KnownFolders.Backups);

        Assert.Equal(LinuxPaths.BackupDir, resolved);
        Assert.True(Directory.Exists(resolved));
    }

    /// <summary>
    /// The whole point of naming rather than accepting a path: nothing outside the fixed set
    /// resolves to anything, however it is spelled — including something that looks like a path
    /// traversal, since the endpoint this feeds never touches the filesystem with the name
    /// itself.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Backups")]
    [InlineData("../../../../etc/passwd")]
    [InlineData("/etc")]
    [InlineData("data")]
    public void AnythingElseResolvesToNothing(string name) {
        Assert.Null(KnownFolders.Resolve(name));
    }
}
