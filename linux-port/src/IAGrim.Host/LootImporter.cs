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
        Func<(string? Setup, string? Hook)> warnings,
        CancellationToken cancellationToken) {

        // No prefix: there is no loot to import, and the loop runs anyway.
        //
        // It used to return here, which quietly took the status heartbeat with it — this is the
        // only thing that pushes one. A client that could not find a prefix therefore reported
        // nothing at all: not the parse it was running, not the analysis after it, not even that
        // it was alive. "No feedback about when parsing happens" was that, and the parsing was
        // the least of what went unreported.
        if (bridge is null) {
            Console.WriteLine("warning: no Proton prefix; loot import disabled.");
        }

        // Outside the loop on purpose: the service remembers which items it has already asked
        // the game to describe, and rebuilding it every pass would throw that away — which is
        // how the same twenty items ended up being asked for every two seconds.
        var replicas = bridge is null ? null : new ReplicaService(bridge);

        // The hook's own channel. Emptied every pass because nothing else does: these files are
        // written by the DLL and never cleaned up, and an install that has been used for a few
        // days accumulates hundreds. See HookMessageQueue.
        var messages = bridge is null ? null : new HookMessageQueue(bridge);
        var firstDrain = true;

        // The last hardcore state the game reported, so the UI is told when it *changes* rather
        // than on every pass that happens to drain a message. The hook re-sends the same value
        // freely — message 47 rides along with item initialisation — and a filter that reset
        // itself every two seconds would fight anyone trying to look at their other stash.
        bool? lastHardcore = null;

        // The last state the UI was told about. Everything the header shows — the game starting,
        // the hook attaching, the collection growing — changes while nobody is making requests,
        // and a page that asked once at load would go on saying "Grim Dawn is not running" for
        // the rest of the session. Upstream's window updates itself continuously for the same
        // reason; this is the equivalent for a UI at the end of a socket.
        HostStatus? lastStatus = null;

        // The last failure reported, so a permanent one is said once rather than every two
        // seconds for the rest of the session. A prefix with the wrong permissions fails
        // identically on every pass, and 1,800 copies of one sentence an hour buries whatever
        // else the log had to say.
        string? lastFailure = null;

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
                // Almost all of these are drained silently: the attach path reads the hook's
                // verdict from the .ABORTED marker synchronously, and reports an attach itself.
                // Announcing them would mean announcing the past — the first pass on an existing
                // install clears everything the hook ever wrote, which on this machine was 522
                // files, 60 of them "hooked successfully" from sessions weeks ago.
                //
                // The hardcore messages are the exception, and *that* is why the first sweep is
                // excluded rather than merely quiet: acting on it would switch the collection
                // view to whatever stash was being played the last time the game ran, which can
                // be weeks ago and is never what the person sitting here now is looking at.
                if (messages is not null) {
                    var drained = messages.Drain();

                    if (firstDrain) {
                        if (drained.Count > 0) {
                            Console.WriteLine(
                                $"cleared {drained.Count} message(s) left in the bridge by earlier sessions");
                        }
                    }
                    else {
                        // Last one wins: a pass can drain several, and the most recent is the
                        // state the game is in now. Drain returns them oldest first.
                        var reported = drained.Select(m => m.Hardcore)
                                              .LastOrDefault(state => state is not null);

                        if (reported is bool hardcore && hardcore != lastHardcore) {
                            lastHardcore = hardcore;
                            await events.BroadcastAsync(HostEvent.PlayingHardcore(hardcore),
                                                        cancellationToken);
                        }
                    }

                    firstDrain = false;
                }

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

                // Unconditional, and before the import: this is the client's only heartbeat,
                // and the states most worth reporting are the ones where the rest of this pass
                // has nothing to do.
                var (setupWarning, hookWarning) = warnings();
                var status = collection.Status(paths, bridge, startedAt, settings(),
                                               autoAttach?.State(settings().AutoAttach).Attaching ?? false,
                                               gameData?.Running ?? false, gameData?.Step,
                                               setupWarning, hookWarning);
                // Records compare by value, so this is "has anything the user can see moved".
                if (status != lastStatus) {
                    lastStatus = status;
                    await events.BroadcastAsync(HostEvent.Status(status), cancellationToken);
                }

                // The importing half, which needs a bridge. Guarded rather than returned
                // from: the status above has to keep going without one, and an early exit
                // here would skip the delay below and spin the loop.
                if (bridge is not null && replicas is not null) {
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

                    // Counted rather than announced one by one: a stash tab emptied in one go can
                    // hold several copies of the same roll, and four toasts saying the same thing is
                    // worse than one that says how many.
                    var duplicates = new List<string>();

                    // Set when an item arrives whose records the analysis has never read, which
                    // is every first sighting of a kind of item: this port stores stat rows only
                    // for the records a collection references, so there is nothing on disk to
                    // describe the new one with. See the pass triggered after the loop.
                    var undescribed = 0;

                    foreach (var result in watcher.ImportPending()) {
                        if (result.Error is not null) {
                            Console.Error.WriteLine(
                                $"could not import {Path.GetFileName(result.File)}: {result.Error} (file kept)");
                            await events.BroadcastAsync(
                                HostEvent.Message($"Could not import a looted item: {result.Error}", "error"),
                                cancellationToken);
                            continue;
                        }
                        // The collection already holds this exact roll, so the row is not written and
                        // the item is gone from the game — the player has one fewer than they had.
                        // Upstream drops it too (ItemClassificationService), but it says so in its log
                        // and this said nothing at all, which is how "I moved four items in and two
                        // arrived" looked like items being lost in transit.
                        if (result.Duplicate) {
                            duplicates.Add(result.Item!.PlainName ?? result.Item.BaseRecord);
                            Console.WriteLine($"already in the collection, not added: {duplicates[^1]}");
                            continue;
                        }

                        Console.WriteLine($"looted: {result.Item!.PlainName}");

                        // Rarity, level and rolled values for the item that just arrived, so it is
                        // drawn as the epic it is rather than in the "unknown" colour until the next
                        // full pass. Upstream does the same on import.
                        try {
                            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                                $"Data Source={LinuxPaths.DatabaseFile}");
                            connection.Open();
                            var (_, unread) = IAGrim.Core.ItemStats.NewItemDetails.Apply(
                                connection, [result.Id]);
                            undescribed += unread;
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

                    if (duplicates.Count > 0) {
                        await events.BroadcastAsync(HostEvent.Message(
                            duplicates.Count == 1
                                ? $"{duplicates[0]} was already in your collection, so it was not added again."
                                : $"{duplicates.Count} looted items were already in your collection "
                                  + "and were not added again.",
                            "warning"), cancellationToken);
                    }

                    // The analysis reads the game archives for the records a collection uses, so
                    // a record it has never seen is one only a pass can describe. Doing it here
                    // rather than leaving it for the next start is the difference between an item
                    // that is the right colour a few seconds after it is looted and one that is
                    // grey for the rest of the session.
                    //
                    // Fire and forget, and cheap when it is not needed: RunIfNeededAsync asks the
                    // collection whether anything is actually undescribed, and the pass holds a
                    // gate so a second batch arriving mid-pass does not start another.
                    if (undescribed > 0) {
                        Console.WriteLine(
                            $"{undescribed} looted item(s) use records the analysis has not read; analysing.");
                        _ = StatRefresh.RunIfNeededAsync(LinuxPaths.DatabaseFile,
                                                         settings().GameDir ?? paths?.GameDir,
                                                         events, cancellationToken);
                    }
                }

                // Reached only by a pass that got all the way through, which is what makes the
                // next failure worth printing again.
                lastFailure = null;
            }
            catch (Exception ex) {
                // Never let a failed pass stop the loop: the hook keeps writing files, and
                // stopping would silently strand them.
                if (ex.Message != lastFailure) {
                    lastFailure = ex.Message;
                    Console.Error.WriteLine($"loot import pass failed: {ex.Message}");
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (TaskCanceledException) { break; }
        }
    }
}
