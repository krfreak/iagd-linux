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
        SteamPaths? paths,
        GameDataRefresh? gameData,
        CancellationToken cancellationToken) {

        if (bridge is null) {
            Console.WriteLine("warning: no Proton prefix; loot import disabled.");
            return;
        }

        // Outside the loop on purpose: the service remembers which items it has already asked
        // the game to describe, and rebuilding it every pass would throw that away — which is
        // how the same twenty items ended up being asked for every two seconds.
        var replicas = new ReplicaService(bridge);

        // The hook's own channel. Emptied every pass because nothing else does: these files are
        // written by the DLL and never cleaned up, and an install that has been used for a few
        // days accumulates hundreds. See HookMessageQueue.
        var messages = new HookMessageQueue(bridge);
        var firstDrain = true;

        // The last state the UI was told about. Everything the header shows — the game starting,
        // the hook attaching, the collection growing — changes while nobody is making requests,
        // and a page that asked once at load would go on saying "Grim Dawn is not running" for
        // the rest of the session. Upstream's window updates itself continuously for the same
        // reason; this is the equivalent for a UI at the end of a socket.
        HostStatus? lastStatus = null;

        while (!cancellationToken.IsCancellationRequested) {
            try {
                // Same loop watches for transfers the game has taken, so there is one timer
                // rather than two competing for the same directories.
                if (transfers is not null) {
                    await transfers.PollAsync(cancellationToken);
                }

                var startedAt = gameStartTime();

                // Before anything else, so a backlog cannot grow while a slow pass runs.
                //
                // Drained silently. Nothing here acts on them: the attach path reads the hook's
                // verdict from the .ABORTED marker synchronously, and reports an attach itself.
                // Announcing them would mean announcing the past — the first pass on an existing
                // install clears everything the hook ever wrote, which on this machine was 522
                // files, 60 of them "hooked successfully" from sessions weeks ago.
                var drained = messages.Drain().Count;
                if (firstDrain && drained > 0) {
                    Console.WriteLine($"cleared {drained} message(s) left in the bridge by earlier sessions");
                }
                firstDrain = false;

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

                if (paths is not null) {
                    var status = collection.Status(paths, bridge, startedAt, settings(),
                                                   autoAttach?.State(settings().AutoAttach).Attaching ?? false,
                                                   gameData?.Running ?? false, gameData?.Step);
                    // Records compare by value, so this is "has anything the user can see moved".
                    if (status != lastStatus) {
                        lastStatus = status;
                        await events.BroadcastAsync(HostEvent.Status(status), cancellationToken);
                    }
                }

                using var store = new LootStore(LinuxPaths.DatabaseFile);
                var watcher = new LootWatcher(bridge, store);

                // Items that arrived from a file have no tooltip; the game can render one, but
                // only while it is running with the hook attached. Asking otherwise just piles
                // request files into the prefix for a reader that is not there.
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

                    // Rarity, level and rolled values for the item that just arrived, so it is
                    // drawn as the epic it is rather than in the "unknown" colour until the next
                    // full pass. Upstream does the same on import.
                    try {
                        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                            $"Data Source={LinuxPaths.DatabaseFile}");
                        connection.Open();
                        IAGrim.Core.ItemStats.NewItemDetails.Apply(connection, [result.Id]);
                    }
                    catch (Exception ex) {
                        // Cosmetic until the next pass; never worth failing an import over.
                        Console.Error.WriteLine($"could not describe the new item: {ex.Message}");
                    }

                    // Re-read through the collection so the UI gets the same enriched shape
                    // the search endpoint returns, not a second thinner one. By id: the list is
                    // ordered by name, so "the first row of an unfiltered search" is not this.
                    var newest = collection.Card(result.Id);
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
