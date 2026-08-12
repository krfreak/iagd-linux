using System.Diagnostics;

namespace IAGrim.Platform;

/// <summary>What an attach attempt did.</summary>
public enum AttachOutcome {
    /// <summary>The hook is live. Either this attempt worked, or it already was.</summary>
    Attached,

    /// <summary>
    /// The DLL loaded, decided the game was not ready, and unloaded itself. Normal while the
    /// game is loading or sitting in character select — retrying later is the correct response.
    /// </summary>
    NotReady,

    /// <summary>The attempt failed for a reason retrying will probably not fix.</summary>
    Failed,
}

public sealed record AttachResult(AttachOutcome Outcome, string Detail);

/// <summary>
/// Attaches the hook to a running Grim Dawn, by running the same script a person would.
///
/// **Why shell out rather than reimplement it.** The script does more than run the injector: it
/// stages the DLL inside the prefix, converts paths to their Windows form, invokes Proton so the
/// injector runs inside the game's own container, clears stale markers, and — most importantly —
/// refuses to inject when the hook is already live. That refusal is not politeness. Loading a
/// second copy puts two sets of MinHook patches over the same functions and reliably crashes the
/// game. Reimplementing all of that in C# would mean maintaining two copies of the one procedure
/// that can destroy a play session.
///
/// **Retrying is safe**, which is what makes polling reasonable. Every abort path in the hook's
/// DllMain returns FALSE, so Windows unloads the module and a later attempt loads it fresh; and
/// a named mutex in the DLL refuses a second copy even if something else got it wrong.
/// </summary>
public class HookAttacher {
    private readonly PrefixBridge _bridge;
    private readonly string _scriptPath;

    public HookAttacher(PrefixBridge bridge, string? scriptPath = null) {
        _bridge = bridge;
        _scriptPath = scriptPath ?? FindScript();
    }

    /// <summary>For substituting the attach step in tests.</summary>
    protected HookAttacher() {
        _bridge = null!;
        _scriptPath = string.Empty;
    }

    /// <summary>
    /// Whether the attach script could be located at all.
    ///
    /// Virtual, with <see cref="AttachAsync"/>, so the pacing in <see cref="AutoAttachService"/>
    /// can be tested without a running game — that logic decides how often a Proton process is
    /// launched, which is exactly the part that must not be verified by hand.
    /// </summary>
    public virtual bool IsAvailable => File.Exists(_scriptPath);

    public string ScriptPath => _scriptPath;

    /// <summary>
    /// Locates attach-gd.sh. A package sets IAGD_SCRIPTS; a source checkout has it beside the
    /// repository.
    /// </summary>
    private static string FindScript() {
        var fromEnvironment = Environment.GetEnvironmentVariable("IAGD_SCRIPTS");
        if (!string.IsNullOrEmpty(fromEnvironment)) {
            return Path.Combine(fromEnvironment, "attach-gd.sh");
        }

        // Walk up from the executable looking for the checkout layout, so `make run` works.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) {
            var candidate = Path.Combine(directory.FullName, "scripts", "attach-gd.sh");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return "attach-gd.sh";
    }

    /// <summary>
    /// Runs one attach attempt, and waits for its verdict.
    /// </summary>
    /// <param name="gameStartedAt">
    /// Used to tell a marker from this game session apart from one left by a previous run.
    /// </param>
    public virtual async Task<AttachResult> AttachAsync(DateTime? gameStartedAt, CancellationToken cancellationToken) {
        if (!IsAvailable) {
            return new AttachResult(AttachOutcome.Failed, $"attach script not found at {_scriptPath}");
        }
        if (_bridge.IsHookLive(gameStartedAt)) {
            return new AttachResult(AttachOutcome.Attached, "already attached");
        }

        var startInfo = new ProcessStartInfo {
            FileName = "/usr/bin/env",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add(_scriptPath);

        // The script waits forever by default, retrying every 5 s until the game accepts. That
        // is right for a person watching a terminal and wrong here: this runs on a timer, so it
        // must come back and let the loop decide whether to try again.
        startInfo.Environment["ATTACH_TIMEOUT_MS"] = "8000";
        startInfo.Environment["RETRY_MS"] = "2000";

        using var process = new Process { StartInfo = startInfo };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        try {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // A hard ceiling regardless of what the script does; it should return well inside
            // this, and a wedged injector must not wedge the host with it.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));

            try {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new AttachResult(AttachOutcome.Failed, "the attach attempt timed out");
            }
        }
        catch (Exception ex) {
            return new AttachResult(AttachOutcome.Failed, ex.Message);
        }

        // The script's own words are the least ambiguous signal it produces, but the marker on
        // disk is the authority: the DLL writes it itself once it has actually hooked.
        if (_bridge.IsHookLive(gameStartedAt)) {
            return new AttachResult(AttachOutcome.Attached, "hook is live");
        }

        var text = output.ToString();
        if (text.Contains("ABORTED", StringComparison.Ordinal)) {
            return new AttachResult(AttachOutcome.NotReady,
                "the game is still loading or in character select");
        }

        var lastLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .LastOrDefault(l => l.Trim().Length > 0)?.Trim();
        return new AttachResult(AttachOutcome.Failed, lastLine ?? "no hook marker was written");
    }
}
