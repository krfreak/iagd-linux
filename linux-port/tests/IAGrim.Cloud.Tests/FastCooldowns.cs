using System.Reflection;

namespace IAGrim.Cloud.Tests;

/// <summary>
/// Shrinks the cooldowns a <see cref="BackupService"/> is obeying, so a test does not have to
/// wait them out.
///
/// The numbers come from the server and are real: in the dual-computer mode these tests run in,
/// 10 s between deletion passes, 10 s between downloads, 1 s between uploads. **Fetching and
/// applying them is behaviour worth exercising**, which is why this runs after the service has
/// been through <c>Execute</c> once and asked <c>/logincheck</c>, rather than pre-empting the
/// fetch. What it removes is only the waiting, and no test here asserts on how long the client
/// waits — that is <see cref="PacingTests"/>, which needs no server at all.
///
/// **One second, not zero.** The server stamps items with <c>time.Now().Unix()</c> and serves
/// downloads with <c>WHERE ts &gt; ?</c>, so an item uploaded in the same second as the last
/// download carries that second's stamp and is never handed out again. A second is therefore the
/// finest spacing at which a download still means anything, and a suite that paced itself faster
/// would not be a faster version of this one — it would be a differently-behaved one that fails
/// at random.
///
/// Reflection, because <c>Limitations</c> and <c>LimitationSet</c> are private to
/// <see cref="BackupService"/> and should stay that way: the alternative is a seam in shipping
/// code whose only caller is a test. The suite already reaches in like this for
/// <c>_lastSearchDt</c>.
/// </summary>
internal static class FastCooldowns {
    /// <summary>The server's own timestamp granularity. See the note above before lowering it.</summary>
    public const int WindowMs = 1000;

    /// <summary>How long a pump sleeps between passes: just past the window.</summary>
    public const int PumpIntervalMs = WindowMs + 100;

    private static readonly FieldInfo Field =
        typeof(BackupService).GetField("_cooldowns", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(BackupService), "_cooldowns");

    private static readonly Type LimitationSetType =
        typeof(BackupService).GetNestedType("LimitationSet", BindingFlags.NonPublic)
        ?? throw new TypeLoadException("BackupService.LimitationSet");

    private static readonly Type LimitationsType =
        typeof(BackupService).GetNestedType("Limitations", BindingFlags.NonPublic)
        ?? throw new TypeLoadException("BackupService.Limitations");

    /// <summary>
    /// Replaces the fetched cooldowns with one-second ones.
    ///
    /// Returns false while the service has none — <c>Execute</c> has not reached the server yet,
    /// or came back without limits — so a caller can simply try again on the next pass.
    /// </summary>
    public static bool TryApply(BackupService backup) {
        if (Field.GetValue(backup) is null) return false;

        var fast = Activator.CreateInstance(LimitationSetType, (long)WindowMs, (long)WindowMs, (long)WindowMs);
        Field.SetValue(backup, Activator.CreateInstance(LimitationsType, fast, fast));
        return true;
    }

    /// <summary>
    /// The windows the service is currently obeying, in milliseconds, or null before it has any.
    ///
    /// This is how the one test that keeps the real numbers checks them. Every other test here
    /// replaces them, so without it a regression that ignored what the server hands out — and
    /// hammered somebody else's service — would pass the whole suite.
    /// </summary>
    /// <param name="set">"MultiUsage" or "Regular".</param>
    public static (long Deletion, long Upload, long Download)? WindowsOf(BackupService backup, string set) {
        var limitations = Field.GetValue(backup);
        if (limitations is null) return null;

        var limitationSet = LimitationsType.GetProperty(set)!.GetValue(limitations)!;

        long Window(string name) {
            var cooldown = LimitationSetType.GetProperty(name)!.GetValue(limitationSet)!;
            return (long)typeof(ActionCooldown)
                .GetField("_cooldown", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(cooldown)!;
        }

        return (Window("DeletionCooldown"), Window("UploadCooldown"), Window("DownloadCooldown"));
    }
}
