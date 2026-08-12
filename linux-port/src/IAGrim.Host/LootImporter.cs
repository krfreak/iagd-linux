using IAGrim.Platform;

namespace IAGrim.Host;

/// <summary>
/// Imports loot in the background and pushes each new item to connected UIs, which is what
/// makes items appear as they are deposited rather than on request.
///
/// Polls, for the reason documented on <see cref="LootWatcher"/>: the hook creates a file
/// and fills it in separate steps, so a change notification routinely fires on an empty one.
/// </summary>
internal static class LootImporter {
    public static async Task RunAsync(
        PrefixBridge? bridge,
        CollectionService collection,
        EventHub events,
        TransferTracker? transfers,
        Func<DateTime?> gameStartTime,
        Func<AppSettings> settings,
        AutoAttachService? autoAttach,
        CancellationToken cancellationToken) {

        if (bridge is null) {
            Console.WriteLine("warning: no Proton prefix; loot import disabled.");
            return;
        }

        while (!cancellationToken.IsCancellationRequested) {
            try {
                // Same loop watches for transfers the game has taken, so there is one timer
                // rather than two competing for the same directories.
                if (transfers is not null) {
                    await transfers.PollAsync(cancellationToken);
                }

                var startedAt = gameStartTime();

                // Attach the hook when the game shows up. Paced inside the service: one attempt
                // at a time, and a growing quiet period rather than a retry every two seconds.
                if (autoAttach is not null) {
                    var message = await autoAttach.PollAsync(startedAt, settings().AutoAttach,
                                                             cancellationToken);
                    if (message is not null) {
                        Console.WriteLine(message);
                        await events.BroadcastAsync(HostEvent.Message(message, "info"), cancellationToken);
                    }
                }

                using var store = new LootStore(LinuxPaths.DatabaseFile);
                var watcher = new LootWatcher(bridge, store);

                // Items that arrived from a file have no tooltip; the game can render one, but
                // only while it is running with the hook attached. Asking otherwise just piles
                // request files into the prefix for a reader that is not there.
                var replicas = new ReplicaService(bridge);
                var completed = replicas.CollectResults(store);
                if (startedAt is not null) {
                    replicas.RequestMissing(store);
                }
                if (completed > 0) {
                    Console.WriteLine($"filled in stats for {completed} item(s) from the game");
                    await events.BroadcastAsync(
                        HostEvent.Message($"Filled in stats for {completed} item(s).", "info"),
                        cancellationToken);
                }

                foreach (var result in watcher.ImportPending()) {
                    if (result.Error is not null) {
                        Console.Error.WriteLine(
                            $"could not import {Path.GetFileName(result.File)}: {result.Error} (file kept)");
                        await events.BroadcastAsync(
                            HostEvent.Message($"Could not import a looted item: {result.Error}", "error"),
                            cancellationToken);
                        continue;
                    }
                    if (result.Duplicate) continue;

                    Console.WriteLine($"looted: {result.Item!.PlainName}");

                    // Re-read through the collection so the UI gets the same enriched shape
                    // the search endpoint returns, not a second thinner one.
                    var newest = collection.Search(new ItemQuery(), 0, 1).Items.FirstOrDefault();
                    if (newest is not null) {
                        await events.BroadcastAsync(HostEvent.Looted(newest), cancellationToken);
                    }
                }
            }
            catch (Exception ex) {
                // Never let a failed pass stop the loop: the hook keeps writing files, and
                // stopping would silently strand them.
                Console.Error.WriteLine($"loot import pass failed: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (TaskCanceledException) { break; }
        }
    }
}
