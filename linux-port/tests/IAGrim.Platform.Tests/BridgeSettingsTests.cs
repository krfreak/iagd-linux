using System.Text.Json.Nodes;
using IAGrim.Platform;
using Xunit;

namespace IAGrim.Platform.Tests;

/// <summary>
/// The keys the hook DLL reads out of the prefix's settings.json.
///
/// Every failure here is silent in the same way: the hook keeps running, the game keeps playing,
/// and items are simply never captured. <c>isGrimDawnParsed</c> shipped missing for exactly that
/// reason — a developer machine that had also run the Windows tool already had the key, so the
/// only visible symptom was on other people's machines, as "Item not looted / Grim Dawn not
/// parsed" flashing over the game.
/// </summary>
public class BridgeSettingsTests : IDisposable {
    private readonly string _root = Directory.CreateTempSubdirectory("iagd-bridge-").FullName;

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private PrefixBridge Bridge => new(_root);
    private JsonObject Written => (JsonObject)JsonNode.Parse(File.ReadAllText(Bridge.SettingsFile))!;

    /// <summary>
    /// The fresh-prefix case: nothing has ever written this file, so every key the hook needs
    /// has to come from us. Missing means false to the hook, and false means nothing is looted.
    /// </summary>
    [Fact]
    public void AFreshFileGetsEveryKeyTheHookReads() {
        var result = BridgeSettings.Apply(Bridge, new AppSettings { StashToLootFrom = 3 },
                                          isGrimDawnParsed: true);

        Assert.Null(result.Error);
        Assert.True(result.Created);

        var written = Written;
        Assert.True(written["persistent"]!["isRunningInWine"]!.GetValue<bool>());
        Assert.True(written["local"]!["isGrimDawnParsed"]!.GetValue<bool>());
        Assert.Equal(3, written["local"]!["stashToLootFrom"]!.GetValue<int>());
        Assert.Equal(0, written["local"]!["stashToDepositTo"]!.GetValue<int>());
    }

    /// <summary>
    /// A client that has not parsed yet leaves the key alone rather than writing false. It runs
    /// in that state on every startup — the first Apply happens before the startup parse — and a
    /// prefix shared with the Windows tool holds that tool's answer here.
    /// </summary>
    [Fact]
    public void AnUnparsedClientDoesNotStampFalseOverSomeoneElsesTrue() {
        File.WriteAllText(Bridge.SettingsFile,
            """{"local":{"isGrimDawnParsed":true},"persistent":{}}""");

        BridgeSettings.Apply(Bridge, new AppSettings(), isGrimDawnParsed: false);

        Assert.True(Written["local"]!["isGrimDawnParsed"]!.GetValue<bool>());
        Assert.True(BridgeSettings.Read(Bridge)!.Value.Parsed);
    }

    /// <summary>
    /// Nothing else in the file is ours. A real install keeps its cloud credentials here, and
    /// rewriting the file wholesale would log someone out of a service this port does not have.
    /// </summary>
    [Fact]
    public void EverythingElseInTheFileSurvives() {
        File.WriteAllText(Bridge.SettingsFile,
            """{"local":{"machineName":"VAMPIRE"},"persistent":{"cloudAuthToken":"secret"}}""");

        BridgeSettings.Apply(Bridge, new AppSettings(), isGrimDawnParsed: true);

        var written = Written;
        Assert.Equal("VAMPIRE", written["local"]!["machineName"]!.GetValue<string>());
        Assert.Equal("secret", written["persistent"]!["cloudAuthToken"]!.GetValue<string>());
    }

    /// <summary>
    /// Re-applying an unchanged file must not rewrite it: the game may be reading it, and the
    /// Windows tool may be watching it.
    /// </summary>
    [Fact]
    public void ApplyingTheSameValuesTwiceRewritesNothing() {
        var settings = new AppSettings { StashToDepositTo = 2 };
        BridgeSettings.Apply(Bridge, settings, isGrimDawnParsed: true);

        var second = BridgeSettings.Apply(Bridge, settings, isGrimDawnParsed: true);

        Assert.False(second.Changed);
    }

    /// <summary>
    /// The bridge directory belongs to the hook DLL, which creates it the first time it runs.
    /// Until then — a prefix the hook has never been injected into, or one where someone has
    /// deleted the EvilSoft folder to start clean — it is simply not there, and the write has to
    /// make it rather than fail.
    ///
    /// The failure this covers reached a user as "could not configure the hook: Could not find a
    /// part of the path .../settings.json.tmp", on a prefix where the file it named did not exist
    /// and could not be created. Worse, it repaired itself at random: the other paths on
    /// PrefixBridge create their directories on the way out, so once some unrelated call in the
    /// same session had made the parent, the next attempt worked — "after restarting a few times
    /// it eventually wrote it".
    ///
    /// Every test above starts from a directory that already exists, which is exactly why none
    /// of them caught this.
    /// </summary>
    [Fact]
    public void APrefixTheHookHasNeverRunInGetsItsDirectoryMade() {
        var untouched = new PrefixBridge(Path.Combine(_root, "AppData", "Local", "EvilSoft", "IAGD"));
        Assert.False(Directory.Exists(untouched.Root));

        var result = BridgeSettings.Apply(untouched, new AppSettings(), isGrimDawnParsed: true);

        Assert.Null(result.Error);
        Assert.True(result.Created);
        Assert.True(File.Exists(untouched.SettingsFile));
        Assert.True(BridgeSettings.Read(untouched)!.Value.WineMode);
    }
}
