using IAGrim.Platform;
using Xunit;

namespace IAGrim.Platform.Tests;

/// <summary>
/// Emptying the hook → host channel. Nothing read these before, so they accumulated for the
/// lifetime of an install — 522 files across a few days of ordinary use.
/// </summary>
public class HookMessageQueueTests : IDisposable {
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "iagd-msgq-" + Guid.NewGuid().ToString("N"));

    private readonly PrefixBridge _bridge;

    public HookMessageQueueTests() {
        Directory.CreateDirectory(_root);
        _bridge = new PrefixBridge(_root);
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>[int32 type][int32 dataLength][raw data] — the hook's WriteMessageToFile.</summary>
    private void Write(string name, int type, byte[]? payload = null) {
        payload ??= [];
        var bytes = new byte[8 + payload.Length];
        BitConverter.GetBytes(type).CopyTo(bytes, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(bytes, 4);
        payload.CopyTo(bytes, 8);
        File.WriteAllBytes(Path.Combine(_bridge.LinuxHack, name + ".msg"), bytes);
    }

    [Fact]
    public void ReadsTheHeaderAndRemovesTheFile() {
        Write("a", (int)HookMessage.InjectionCancelled);
        Write("b", (int)HookMessage.HookedSuccessfully, BitConverter.GetBytes(7));

        var drained = new HookMessageQueue(_bridge).Drain();

        Assert.Equal(2, drained.Count);
        Assert.Contains(drained, m => m.Known == HookMessage.InjectionCancelled && m.PayloadLength == 0);
        Assert.Contains(drained, m => m.Known == HookMessage.HookedSuccessfully && m.PayloadLength == 4);
        Assert.Empty(Directory.GetFiles(_bridge.LinuxHack, "*.msg"));
    }

    /// <summary>The backlog is the reason this exists; draining it must not need many passes.</summary>
    [Fact]
    public void ClearsAWholeBacklogInOnePass() {
        for (var i = 0; i < 522; i++) {
            Write($"backlog-{i:D4}", (int)HookMessage.InjectionCancelled);
        }

        var drained = new HookMessageQueue(_bridge).Drain();

        Assert.Equal(522, drained.Count);
        Assert.Empty(Directory.GetFiles(_bridge.LinuxHack, "*.msg"));
    }

    /// <summary>
    /// A message from a game that died mid-write. Keeping it would mean retrying it on every
    /// pass for ever; the file is worth less than the loop it would occupy.
    /// </summary>
    [Fact]
    public void DropsATruncatedMessageRatherThanRetryingItForever() {
        File.WriteAllBytes(Path.Combine(_bridge.LinuxHack, "short.msg"), [1, 2, 3]);
        Write("good", (int)HookMessage.WorkerThreadLaunched);

        var drained = new HookMessageQueue(_bridge).Drain();

        Assert.Single(drained);
        Assert.Equal(HookMessage.WorkerThreadLaunched, drained[0].Known);
        Assert.Empty(Directory.GetFiles(_bridge.LinuxHack, "*.msg"));
    }

    /// <summary>Unknown types are still drained; the directory must stay bounded regardless.</summary>
    [Fact]
    public void DrainsTypesItDoesNotRecognise() {
        Write("odd", 4242);

        var drained = new HookMessageQueue(_bridge).Drain();

        Assert.Single(drained);
        Assert.Equal(4242, drained[0].Type);
        Assert.Null(drained[0].Known);
    }

    [Fact]
    public void LeavesTheOtherBridgeFilesAlone() {
        Write("a", (int)HookMessage.InjectionCancelled);
        File.WriteAllText(Path.Combine(_bridge.LinuxHack, "1234.PID"), "");
        File.WriteAllText(Path.Combine(_bridge.LinuxHack, "1234.ABORTED"), "");

        new HookMessageQueue(_bridge).Drain();

        // The markers are the attach path's evidence; draining messages must not disturb them.
        Assert.True(File.Exists(Path.Combine(_bridge.LinuxHack, "1234.PID")));
        Assert.True(File.Exists(Path.Combine(_bridge.LinuxHack, "1234.ABORTED")));
    }

    /// <summary>
    /// The hardcore messages are the only ones anything acts on, and the payload is the whole of
    /// what they say. Both types carry it: 20 when the game sets the mode, 47 when an item is
    /// initialised in it.
    /// </summary>
    [Theory]
    [InlineData(HookMessage.GameInfoIsHardcore, 1, true)]
    [InlineData(HookMessage.GameInfoIsHardcore, 0, false)]
    [InlineData(HookMessage.GameInfoIsHardcoreViaInit, 1, true)]
    [InlineData(HookMessage.GameInfoIsHardcoreViaInit, 0, false)]
    public void ReadsTheHardcoreFlagOutOfThePayload(HookMessage type, byte value, bool expected) {
        Write("hc", (int)type, [value]);

        var drained = new HookMessageQueue(_bridge).Drain();

        Assert.Single(drained);
        Assert.Equal(expected, drained[0].Hardcore);
    }

    /// <summary>
    /// Every other message is silent on the subject rather than reporting softcore. The two are
    /// very different to a caller deciding whether to move somebody's collection view.
    /// </summary>
    [Fact]
    public void OnlyTheHardcoreMessagesReportAHardcoreState() {
        Write("launched", (int)HookMessage.WorkerThreadLaunched);
        Write("hooked", (int)HookMessage.HookedSuccessfully, BitConverter.GetBytes(7));

        Assert.All(new HookMessageQueue(_bridge).Drain(), m => Assert.Null(m.Hardcore));
    }

    /// <summary>
    /// <c>SetHardcore::HookedMethod</c> sends exactly one byte. Anything else claiming to be one
    /// of these messages is not one, whatever its type field says, and guessing at the first byte
    /// of a differently-shaped payload would silently move the collection view.
    /// </summary>
    [Fact]
    public void RefusesAHardcoreMessageOfTheWrongShape() {
        Write("wide", (int)HookMessage.GameInfoIsHardcore, BitConverter.GetBytes(1));
        Write("empty", (int)HookMessage.GameInfoIsHardcoreViaInit);

        Assert.All(new HookMessageQueue(_bridge).Drain(), m => Assert.Null(m.Hardcore));
    }

    /// <summary>
    /// A length header larger than the file. Reading the declared length would run off the end of
    /// the array; what arrived is what can be trusted.
    /// </summary>
    [Fact]
    public void DoesNotReadPastTheEndOfAShortenedPayload() {
        var bytes = new byte[9];
        BitConverter.GetBytes((int)HookMessage.GameInfoIsHardcore).CopyTo(bytes, 0);
        BitConverter.GetBytes(64).CopyTo(bytes, 4);   // claims 64 bytes, carries one
        bytes[8] = 1;
        File.WriteAllBytes(Path.Combine(_bridge.LinuxHack, "lying.msg"), bytes);

        var drained = new HookMessageQueue(_bridge).Drain();

        Assert.Single(drained);
        Assert.Single(drained[0].Payload);
        Assert.Equal(64, drained[0].PayloadLength);
    }

    [Fact]
    public void AnEmptyOrMissingDirectoryIsNotAnError() {
        Assert.Empty(new HookMessageQueue(_bridge).Drain());
        Assert.Empty(new HookMessageQueue(new PrefixBridge(
            Path.Combine(_root, "nope"))).Drain());
    }
}
