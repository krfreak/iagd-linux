using IAGrim.Platform;
using Xunit;

namespace IAGrim.Platform.Tests;

/// <summary>
/// Telling the game apart from our own injector.
///
/// Every command line below was captured from /proc on a real machine, with NULs replaced by
/// spaces the way /proc presents them. The injector cases are the bug: launching the injector
/// as `--attach-name "Grim Dawn.exe"` put the game's name into three unrelated processes, and
/// a plain substring match counted all three as a running game.
/// </summary>
public class GameProcessTests {
    // Captured with no game running at all, during a single attach attempt.
    private const string ProtonWrapper =
        "python3 /home/tim/.local/share/Steam/compatibilitytools.d/GE-Proton11-1/proton run " +
        "Z:\\home\\tim\\workspaces\\iagd-linux\\build\\proton-injector\\bin\\injector64.exe " +
        "C:\\iagd\\ItemAssistantHook_x64.dll --attach-window Grim Dawn --attach-name Grim Dawn.exe " +
        "--attach-retry 0 --attach-timeout 1500";

    private const string WineSteamHelper =
        "c:\\windows\\system32\\steam.exe " +
        "Z:\\home\\tim\\workspaces\\iagd-linux\\build\\proton-injector\\bin\\injector64.exe " +
        "C:\\iagd\\ItemAssistantHook_x64.dll --attach-window Grim Dawn --attach-name Grim Dawn.exe " +
        "--attach-retry 0 --attach-timeout 1500";

    private const string Injector =
        "Z:\\home\\tim\\workspaces\\iagd-linux\\build\\proton-injector\\bin\\injector64.exe " +
        "C:\\iagd\\ItemAssistantHook_x64.dll --attach-window Grim Dawn --attach-name Grim Dawn.exe " +
        "--attach-retry 0 --attach-timeout 1500";

    [Theory]
    [InlineData(ProtonWrapper)]
    [InlineData(WineSteamHelper)]
    [InlineData(Injector)]
    public void OurOwnInjectorIsNotTheGame(string commandLine) {
        Assert.False(GameProcess.IsGameCommandLine(commandLine));
    }

    /// <summary>
    /// The shapes Grim Dawn itself takes under Proton: wine's steam.exe launching it, and the
    /// game process proper.
    /// </summary>
    [Theory]
    [InlineData("c:\\windows\\system32\\steam.exe " +
                "Z:\\home\\tim\\.local\\share\\Steam\\steamapps\\common\\Grim Dawn\\x64\\Grim Dawn.exe")]
    [InlineData("Z:\\home\\tim\\.local\\share\\Steam\\steamapps\\common\\Grim Dawn\\x64\\Grim Dawn.exe")]
    [InlineData("/home/tim/.local/share/Steam/steamapps/common/Grim Dawn/x64/Grim Dawn.exe")]
    public void TheGameIsTheGame(string commandLine) {
        Assert.True(GameProcess.IsGameCommandLine(commandLine));
    }

    [Fact]
    public void UnrelatedProcessesAreNotTheGame() {
        Assert.False(GameProcess.IsGameCommandLine("/usr/bin/dolphin /home/tim/Grim Dawn"));
        Assert.False(GameProcess.IsGameCommandLine("/usr/bin/firefox"));
        Assert.False(GameProcess.IsGameCommandLine(""));
    }

    /// <summary>
    /// /proc separates *arguments* with NUL, keeping the spaces inside each one — so
    /// "Grim Dawn.exe" survives as one argument, and so does "--attach-name". Built here the
    /// way the kernel presents it rather than by substituting spaces, which would split the
    /// game's name in half and make the assertion pass for the wrong reason.
    /// </summary>
    [Fact]
    public void SeparatorsDoNotMatter() {
        var injector = string.Join('\0', [
            "Z:\\home\\tim\\workspaces\\iagd-linux\\build\\proton-injector\\bin\\injector64.exe",
            "C:\\iagd\\ItemAssistantHook_x64.dll",
            "--attach-window", "Grim Dawn",
            "--attach-name", "Grim Dawn.exe",
        ]) + "\0";

        var game = string.Join('\0', [
            "c:\\windows\\system32\\steam.exe",
            "Z:\\Steam\\steamapps\\common\\Grim Dawn\\x64\\Grim Dawn.exe",
        ]) + "\0";

        Assert.False(GameProcess.IsGameCommandLine(injector));
        Assert.True(GameProcess.IsGameCommandLine(game));
    }
}
