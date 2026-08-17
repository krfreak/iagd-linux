using IAGrim.Core.GameData;
using IAGrim.Platform;

namespace IAGrim.Host;

/// <summary>
/// Reads Grim Dawn's data into the collection, from the client, without anyone typing a command.
///
/// Upstream has no command line. Its Grim Dawn tab lists the installations it has found, and
/// Load Database parses the selected one; it also parses by itself at startup when the game has
/// been patched or was never read. This port had that work only in `iagd parse`, so the UI's job
/// was to tell the user to open a terminal — which is not a port of anything.
///
/// One parse at a time, since it rewrites the tables every other query reads.
/// </summary>
public sealed class GameDataRefresh {
    private readonly EventHub _events;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GameDataRefresh(EventHub events) => _events = events;

    /// <summary>True while a parse is running, for the status the UI shows.</summary>
    public bool Running { get; private set; }

    /// <summary>The last line the parse reported, or null when it is not running.</summary>
    public string? Step { get; private set; }

    /// <summary>
    /// Raised after a parse succeeds. The host uses it to tell the hook that Grim Dawn has been
    /// read: until it knows, it refuses to loot anything, and a fresh install only ever reaches
    /// that state here — long after the settings were last written.
    /// </summary>
    public Action? OnParsed { get; set; }

    /// <summary>
    /// Parses <paramref name="gameDir"/> in the background. Returns immediately; the collection
    /// stays readable throughout, and the analysis pass that has to follow is run for you.
    /// </summary>
    public Task StartAsync(string gameDir, string language, CancellationToken cancellationToken) {
        if (!Directory.Exists(gameDir)) {
            return BroadcastAsync($"Grim Dawn was not found at {gameDir}.", cancellationToken);
        }

        return Task.Run(async () => {
            // Never two at once: a parse replaces every template and reassigns every record id.
            if (!await _gate.WaitAsync(0, cancellationToken)) {
                await BroadcastAsync("A parse is already running.", cancellationToken);
                return;
            }

            try {
                Running = true;
                await BroadcastAsync("Reading Grim Dawn's data…", cancellationToken);

                var result = await Task.Run(() => GameDataParse.Run(
                    gameDir, LinuxPaths.DatabaseFile, LinuxPaths.BackupDir, language,
                    line => {
                        if (string.IsNullOrWhiteSpace(line)) return;
                        Step = line.Trim();
                        Console.WriteLine($"parse: {Step}");
                    }), cancellationToken);

                await BroadcastAsync(
                    $"Read {result.Templates:N0} item(s), {result.Skills:N0} skill(s) and "
                    + $"{result.Icons:N0} icon(s) from Grim Dawn.", cancellationToken);

                // Before the analysis pass, not after: the hook only needs the item table, and
                // this is what lets a first-run install start looting without a restart.
                try { OnParsed?.Invoke(); }
                catch (Exception ex) { Console.Error.WriteLine($"could not configure the hook: {ex.Message}"); }

                // A parse clears the game's stat rows and every rolled value with them, so the
                // analysis pass has to follow it. Upstream does the same, and doing it here is
                // the difference between the collection working afterwards and looking broken.
                Step = "analysing the collection";
                await StatRefresh.RunIfNeededAsync(LinuxPaths.DatabaseFile, gameDir, _events,
                                                   cancellationToken);
            }
            catch (Exception ex) {
                Console.Error.WriteLine($"parse failed: {ex.Message}");
                await BroadcastAsync($"Reading Grim Dawn's data failed: {ex.Message}", cancellationToken);
            }
            finally {
                Running = false;
                Step = null;
                _gate.Release();
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Parses when the stored data is out of date — a patched game, a changed language, or a
    /// collection that has never been parsed at all. Upstream makes the same check on startup.
    /// </summary>
    public Task StartIfStaleAsync(string? gameDir, string language, CancellationToken cancellationToken) {
        if (gameDir is null) return Task.CompletedTask;

        try {
            var status = GameDataStatus.Check(LinuxPaths.DatabaseFile, gameDir, language);
            if (status.Reason is null) return Task.CompletedTask;

            Console.WriteLine($"parse: {status.Reason}");
            return StartAsync(gameDir, language, cancellationToken);
        }
        catch (Exception) {
            return Task.CompletedTask;   // never let a status check stop the server starting
        }
    }

    private Task BroadcastAsync(string message, CancellationToken cancellationToken) {
        Console.WriteLine($"parse: {message}");
        return _events.BroadcastAsync(HostEvent.Message(message, "info"), cancellationToken);
    }
}
