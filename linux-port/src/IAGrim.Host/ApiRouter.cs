using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Web;
using IAGrim.Platform;

namespace IAGrim.Host;

/// <summary>
/// Routes the handful of endpoints the UI needs.
///
/// Hand-rolled because the surface is small and the alternative is an ASP.NET Core runtime
/// dependency — see the note in IAGrim.Host.csproj.
/// </summary>
public sealed class ApiRouter {
    private static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly CollectionService _collection;
    private readonly CollectionViewService _views;
    private readonly EventHub _events;
    private readonly SteamPaths? _paths;
    private readonly PrefixBridge? _bridge;
    private readonly TransferTracker? _transfers;
    private readonly HostServer? _server;

    /// <summary>
    /// Online sync, when it is running. Null in a host built without it — the CLI's own
    /// short-lived host, and anything started before a collection database exists — in which case
    /// the endpoints answer 503 rather than pretending to be logged out.
    /// </summary>
    private readonly CloudApi? _cloud;

    private static readonly object CloudUnavailable = new { error = "online sync is not running" };

    public ApiRouter(CollectionService collection, CollectionViewService views, EventHub events,
                     SteamPaths? paths, PrefixBridge? bridge, TransferTracker? transfers,
                     HostServer? server = null, CloudApi? cloud = null) {
        _server = server;
        _cloud = cloud;
        _collection = collection;
        _views = views;
        _events = events;
        _paths = paths;
        _bridge = bridge;
        _transfers = transfers;
    }

    public async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken) {
        var request = context.Request;
        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "/";
        if (path.Length == 0) path = "/";

        if (request.IsWebSocketRequest && path == "/ws") {
            await HandleWebSocketAsync(context, cancellationToken);
            return;
        }

        var query = HttpUtility.ParseQueryString(request.Url?.Query ?? "");

        switch (request.HttpMethod, path) {
            case ("GET", "/"):
                // The UI takes precedence when built; the banner is the API-only fallback.
                if (await TryServeStaticAsync(context, "/", cancellationToken)) return;
                await Text(context, Banner);
                return;

            case ("GET", "/api/status"):
                // Answered unconditionally. This used to 503 when there was no prefix, on the
                // reasoning that there was nothing to report — but a client that cannot capture
                // loot still has a collection to show and, above all, an explanation it owes the
                // user. The UI swallowed the failure and sat on "Connecting to iagd-host…",
                // which is indistinguishable from a host that never came up. What was missing is
                // now a field on the status rather than the absence of one.
                await Json_(context, CurrentStatus());
                return;

            case ("GET", "/api/items"): {
                // Upstream wires its search box to the same call. It is what keeps downloads
                // alive: after 31 minutes without a search the client is considered idle and
                // stops polling until someone looks at it again.
                _server?.Cloud?.OnSearch();
                await Json_(context, _collection.Search(Branch(ParseItemQuery(query)),
                                                        ParseInt(query["skip"], 0),
                                                        ParseInt(query["take"], 100)));
                return;
            }

            // The copies behind one card, each with its own rolled values — what the comparison
            // view shows before the player picks which one to send. The ids come from the card
            // rather than being re-derived from its merge key here, because the group is the set
            // of rows *that search* matched: two items made of the same records but looted under
            // different mods are one group to the merge key and two cards on screen.
            case ("GET", "/api/items/details"):
                await Json_(context, _collection.Details(ParseIds(query["ids"])));
                return;

            // Not branch-scoped, and upstream's is not either: its collection query counts
            // softcore and hardcore copies of each item side by side and never looks at the mod
            // (ItemCollectionDaoImpl.GetItemCollection).
            case ("GET", "/api/collection"):
                await Json_(context, _views.Collection(ParseItemQuery(query)));
                return;

            case ("GET", "/api/collection/stats"):
                await Json_(context, _views.Aggregate());
                return;

            case ("GET", "/api/sets"):
                await Json_(context, _views.Sets(query["q"]));
                return;

            // Upstream's Components tab opens its author's website; this one is built from the
            // game's own data. See CollectionViewService.Components.
            case ("GET", "/api/components"):
                await Json_(context, _views.Components(query["q"]));
                return;

            case ("GET", "/api/mods"):
                await Json_(context, _collection.Mods());
                return;

            case ("GET", "/api/filters"):
                await Json_(context, FilterCatalogue());
                return;

            // Whether a native chooser is available at all — the UI hides its buttons if not.
            case ("GET", "/api/browse"):
                await Json_(context, new { available = _server?.FilePicker is not null });
                return;

            case ("POST", "/api/browse"): {
                var picker = _server?.FilePicker;
                if (picker is null) {
                    await Json_(context, new { error = "no file chooser available" }, 501);
                    return;
                }

                var browse = await ReadJsonAsync<BrowseRequest>(context) ?? new BrowseRequest();
                string? picked;
                try {
                    picked = picker(browse.Directory, browse.Title ?? "Choose", browse.Path);
                }
                catch (Exception ex) {
                    await Json_(context, new { error = ex.Message }, 500);
                    return;
                }

                await Json_(context, new { path = picked });
                return;
            }

            case ("POST", "/api/merge"): {
                var merge = await ReadJsonAsync<MergeRequest>(context);
                if (string.IsNullOrWhiteSpace(merge?.Path)) {
                    await Json_(context, new { error = "no database given" }, 400);
                    return;
                }

                try {
                    // A real merge adds rows that cannot be regenerated, so there is something
                    // to go back to. A dry run writes nothing and needs no copy.
                    string? backup = null;
                    if (!merge.DryRun) {
                        backup = IAGrim.Core.Backup.DatabaseBackup
                            .Create(LinuxPaths.DatabaseFile, LinuxPaths.BackupDir, "before-merge").Path;
                    }

                    // Progress goes out over the event socket rather than the response, because
                    // the response cannot say anything until the merge is finished. Two rules keep
                    // a per-row callback from flooding it: no more than one report every 150 ms,
                    // and never a second send while the first is still in flight — EventHub writes
                    // to each socket from the calling thread, and concurrent sends on one socket
                    // are not allowed. Dropping a tick costs nothing; the final one is always sent.
                    // Not disposed on purpose: the last send completes on a background thread and
                    // releases it there, which would race a dispose at the end of this block.
                    var sending = new SemaphoreSlim(1, 1);
                    var lastReport = DateTime.MinValue;

                    void Push(HostEvent progressEvent, bool always) {
                        if (!always && DateTime.UtcNow - lastReport < TimeSpan.FromMilliseconds(150)) return;
                        if (!sending.Wait(always ? 2000 : 0)) return;

                        lastReport = DateTime.UtcNow;
                        _ = _events
                            .BroadcastAsync(progressEvent, cancellationToken)
                            .ContinueWith(_ => sending.Release(), TaskScheduler.Default);
                    }

                    void Report(IAGrim.Core.Backup.MergeProgress p) =>
                        Push(HostEvent.MergeProgress(p.Done, p.Total, p.Imported), p.Done >= p.Total);

                    var result = IAGrim.Core.Backup.CollectionMerge
                        .Merge(LinuxPaths.DatabaseFile, merge.Path, merge.DryRun, Report);

                    // Merged rows arrive with their own records but not their rolled values, their
                    // rarity, or their pet-bonus records — all of which come from the game's own
                    // data rather than from the source collection. Leaving that to the user means
                    // items that are in the collection but invisible to most of the filters, so
                    // the pass runs here. It is the whole collection rather than the new rows,
                    // which is what upstream's own recompute does and what makes it also repair
                    // anything looted since the last one.
                    int? statsComputed = null;
                    string? statsNote = null;

                    if (!merge.DryRun && result.Imported > 0) {
                        var gameDir = (_server?.Settings ?? AppSettings.Load()).GameDir ?? _paths?.GameDir;
                        if (gameDir is null) {
                            statsNote = "Grim Dawn was not found, so the values could not be computed. "
                                      + "Set the game folder above, then run 'iagd stats'.";
                        }
                        else {
                            try {
                                var precompute = new IAGrim.Core.ItemStats.StatPrecomputeService(
                                    LinuxPaths.DatabaseFile, gameDir);
                                var stats = precompute.Run(message =>
                                    Push(HostEvent.MergeProgress(0, 0, result.Imported, "stats", message), false));
                                statsComputed = stats.ItemsComputed;
                            }
                            catch (Exception ex) {
                                // The merge itself succeeded; a failed pass is worth reporting but
                                // must not turn the whole request into an error.
                                statsNote = $"The items were added, but computing their values failed: {ex.Message}";
                            }
                        }

                        // Names last, and unconditionally: the merged rows carry whatever the
                        // other collection called them, which is the moment this port composes
                        // a name of its own — and the moment a name that another client cropped
                        // to an affix would otherwise settle in for good. It is the sweep the
                        // online backup already runs for the same reason, it costs 150-190 ms,
                        // and unlike the pass above it needs no game folder, so it is also what
                        // covers a merge done before one is set.
                        StatRefresh.RefreshNames(LinuxPaths.DatabaseFile);
                    }

                    // Let the final progress frame land before the completion message, so the bar
                    // reaches its end rather than being overtaken by the result.
                    await sending.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                    sending.Release();

                    if (!merge.DryRun && result.Imported > 0) {
                        // The grid is showing a collection that just grew.
                        await _events.BroadcastAsync(
                            HostEvent.Message($"Merged in {result.Imported:N0} item(s).", "info"),
                            cancellationToken);

                        // The collection just changed size and, if the pass ran, is no longer
                        // waiting on stats — both of which the header shows.
                        await _events.BroadcastAsync(HostEvent.Status(CurrentStatus()),
                                                     cancellationToken);
                    }

                    await Json_(context, new {
                        result.Considered, result.Imported, result.Duplicates, result.Rejected,
                        merge.DryRun,
                        backup = backup is null ? null : Path.GetFileName(backup),
                        statsComputed,
                        statsNote,
                    });
                }
                catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException) {
                    await Json_(context, new { error = ex.Message }, 400);
                }
                catch (Exception ex) {
                    await Json_(context, new { error = ex.Message }, 500);
                }
                return;
            }

            // Upstream's ExportMode over ItemExport.Export, which is upstream's GDFileExporter —
            // the GD Stash interchange format. That is the half of upstream's dialog this port
            // can serve: it has no reader for upstream's own IAFileExporter (.ias) format, so
            // there is no IA-format source to offer here without inventing one from nothing.
            case ("POST", "/api/export"): {
                var export = await ReadJsonAsync<ExportRequest>(context);
                if (string.IsNullOrWhiteSpace(export?.Path)) {
                    await Json_(context, new { error = "no file given" }, 400);
                    return;
                }

                try {
                    var count = IAGrim.Core.Backup.ItemExport.Export(
                        LinuxPaths.DatabaseFile, export.Path, export.Hardcore, export.Mod);

                    await Json_(context, new {
                        count,
                        // Hardcore and softcore are separate stashes in Grim Dawn, so a file that
                        // mixes them produces an import nobody asked for on the other end. The
                        // CLI carries the same warning for the same reason ('iagd export').
                        warning = export.Hardcore is null && count > 0
                            ? "This includes both hardcore and softcore items. They are separate "
                              + "stashes in game — export softcore and hardcore separately if the "
                              + "file is going back into one."
                            : null,
                    });
                }
                catch (Exception ex) {
                    await Json_(context, new { error = ex.Message }, 500);
                }
                return;
            }

            // Upstream's ImportMode, restricted to the GD Stash radio button for the same reason
            // export is: this port only has a reader for that format. The mod field matches the
            // CLI's --mod, since the interchange format itself carries no mod — GDFileExporter
            // never wrote one, so on the way back in it has to be told rather than read.
            case ("POST", "/api/import"): {
                var import = await ReadJsonAsync<ImportRequest>(context);
                if (string.IsNullOrWhiteSpace(import?.Path)) {
                    await Json_(context, new { error = "no file given" }, 400);
                    return;
                }
                if (!File.Exists(import.Path)) {
                    await Json_(context, new { error = $"no such file: {import.Path}" }, 400);
                    return;
                }

                try {
                    // Importing adds rows to the one thing that cannot be regenerated, so there
                    // is something to go back to if the file turns out to hold the wrong
                    // collection, or a stale one dragged out of a backup by mistake. Same rule
                    // 'iagd import-file' follows.
                    var safety = IAGrim.Core.Backup.DatabaseBackup
                        .Create(LinuxPaths.DatabaseFile, LinuxPaths.BackupDir, "before-import");

                    var (imported, skipped, refused) = IAGrim.Core.Backup.ItemExport
                        .Import(LinuxPaths.DatabaseFile, import.Path, import.Mod);

                    if (imported > 0) {
                        // The same pass a merge triggers, and for the same reason: an imported
                        // item has no rarity or rolled values until this runs, and without it the
                        // new items sit grey and unfilterable until the next restart. The cheap
                        // per-item passes inside it run synchronously here; the full precompute,
                        // only if the collection actually needs it, continues in the background
                        // and reports over the event socket the status poll already listens to —
                        // there is no per-item progress to show for the import itself, since
                        // ItemExport.Import has no callback and adding one would mean changing
                        // the shared format code the CLI also relies on for one caller's UI.
                        var gameDir = (_server?.Settings ?? AppSettings.Load()).GameDir ?? _paths?.GameDir;
                        _ = StatRefresh.RunIfNeededAsync(LinuxPaths.DatabaseFile, gameDir, _events,
                                                         cancellationToken);

                        await _events.BroadcastAsync(
                            HostEvent.Message($"Imported {imported:N0} item(s).", "info"), cancellationToken);
                        await _events.BroadcastAsync(HostEvent.Status(CurrentStatus()), cancellationToken);
                    }

                    await Json_(context, new {
                        imported, skipped, refused,
                        backup = Path.GetFileName(safety.Path),
                    });
                }
                catch (InvalidDataException ex) {
                    await Json_(context, new { error = ex.Message }, 400);
                }
                catch (Exception ex) {
                    await Json_(context, new { error = ex.Message }, 500);
                }
                return;
            }

            // Upstream's "Load Database": read the game's data again on demand.
            // Opens one of the Support page's links in the user's browser.
            //
            // The window is a WebKitGTK view with no external-link handling, so an ordinary
            // anchor would navigate the app itself onto the page and leave no way back. The
            // allowlist is not decoration: this endpoint is an "open anything on the user's
            // desktop" primitive, and it is reachable by any page the browser has open while
            // the host is running.
            case ("POST", "/api/open"): {
                var open = await ReadJsonAsync<OpenRequest>(context);
                var url = open?.Url ?? "";

                if (!SupportLinks.Contains(url)) {
                    await Json_(context, new { error = "not an allowed link" }, 400);
                    return;
                }

                // No xdg-open, or no desktop session: the page falls back to opening it
                // itself, and shows the address either way.
                var opened = DesktopBrowser.Open(url, out var openError);
                await Json_(context, new { opened, error = openError });
                return;
            }

            case ("POST", "/api/parse"): {
                var parse = await ReadJsonAsync<ParseRequest>(context);
                var settings = _server?.Settings ?? AppSettings.Load();
                var directory = parse?.GameDir ?? settings.GameDir ?? _paths?.GameDir;

                if (directory is null) {
                    await Json_(context, new { error = "Grim Dawn was not found; set the game folder first." }, 400);
                    return;
                }

                _ = _server?.GameData?.StartAsync(directory, settings.Language, cancellationToken);
                await Json_(context, new { started = true, gameDir = directory });
                return;
            }

            // ------------------------------------------------------------- online sync
            //
            // The panel behind these is upstream's "Backups" tab. Everything with consequences
            // lives in CloudWorker; these only expose it.

            case ("GET", "/api/cloud"):
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                await Json_(context, _cloud.Status());
                return;

            case ("POST", "/api/cloud/login"):
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                await Json_(context, _cloud.Login());
                return;

            case ("POST", "/api/cloud/logout"):
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                await Json_(context, _cloud.Logout());
                return;

            // Deleting the online backup is irreversible on the server, so it is a DELETE on its
            // own path rather than a flag on something else: nothing reaches it by accident.
            case ("DELETE", "/api/cloud/account"):
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                await Json_(context, _cloud.DeleteAccount());
                return;

            case ("PUT", "/api/cloud/settings"): {
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                var update = await ReadJsonAsync<CloudSettingsRequest>(context) ?? new CloudSettingsRequest();
                await Json_(context, _cloud.UpdateSettings(update.UsingDualComputer, update.OptOutOfBackups));
                return;
            }

            case ("GET", "/api/cloud/buddies"):
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                await Json_(context, _cloud.Buddies());
                return;

            case ("POST", "/api/cloud/buddies"): {
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                var buddy = await ReadJsonAsync<BuddyRequest>(context);
                if (buddy is null || buddy.Id <= 0) { await Json_(context, new { error = "no buddy id given" }, 400); return; }
                var result = _cloud.AddBuddy(buddy.Id, buddy.Nickname);
                await Json_(context, result, HasError(result) ? 400 : 200);
                return;
            }

            case ("PUT", _) when path.StartsWith("/api/cloud/buddies/")
                                 && long.TryParse(path["/api/cloud/buddies/".Length..], out var buddyToUpdate): {
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                var buddy = await ReadJsonAsync<BuddyRequest>(context) ?? new BuddyRequest();
                var result = _cloud.UpdateBuddy(buddyToUpdate, buddy.Nickname, buddy.IsHidden);
                await Json_(context, result, HasError(result) ? 404 : 200);
                return;
            }

            case ("DELETE", _) when path.StartsWith("/api/cloud/buddies/")
                                    && long.TryParse(path["/api/cloud/buddies/".Length..], out var buddyToRemove):
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                await Json_(context, _cloud.RemoveBuddy(buddyToRemove));
                return;

            case ("GET", "/api/cloud/characters"):
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                await Json_(context, _cloud.Characters());
                return;

            // Backs up now rather than on the ten-minute timer. Returns at once; the outcome
            // arrives through GET /api/cloud/characters.
            case ("POST", "/api/cloud/characters/backup"): {
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                var result = _cloud.BackupCharactersNow();
                await Json_(context, result, HasError(result) ? 409 : 202);
                return;
            }

            case ("GET", _) when path.StartsWith("/api/cloud/characters/"): {
                if (_cloud is null) { await Json_(context, CloudUnavailable, 503); return; }
                var name = Uri.UnescapeDataString(path["/api/cloud/characters/".Length..]);
                var result = _cloud.CharacterUrl(name);
                await Json_(context, result, HasError(result) ? 404 : 200);
                return;
            }

            case ("GET", "/api/settings"):
                await Json_(context, SettingsPayload());
                return;

            case ("PUT", "/api/settings"): {
                if (_server is null) { await Json_(context, new { error = "unavailable" }, 503); return; }

                var incoming = await ReadJsonAsync<AppSettings>(context);
                if (incoming is null) { await Json_(context, new { error = "bad request" }, 400); return; }

                // Everything else here takes effect immediately; this one cannot, because the
                // loot importer, the transfer tracker and the attach loop are all built around
                // one bridge when the host starts. Saying so is the honest answer — silently
                // storing it would look like a setting that does nothing.
                var prefixChanged =
                    !string.Equals(incoming.PrefixDir, _server.Settings.PrefixDir, StringComparison.Ordinal);

                // Stash indices address tabs, so a negative one is meaningless; the hook would
                // read it and behave unpredictably rather than reject it.
                incoming.StashToLootFrom = Math.Max(0, incoming.StashToLootFrom);
                incoming.StashToDepositTo = Math.Max(0, incoming.StashToDepositTo);

                // Everything this page does not own is carried across from what is already
                // stored. The body is whatever the settings page holds, and the settings page is
                // served the payload below — which has never included the online-sync keys,
                // since they are managed by the Online tab and one of them is a credential.
                // Deserialising into a fresh object and saving it therefore wrote defaults over
                // the session token: changing a stash tab logged the user out of online sync,
                // and the only visible sign was at the next start.
                incoming.CarryOverUnmanaged(_server.Settings);

                var error = _server.UpdateSettings(incoming);
                await Json_(context, new {
                    settings = SettingsPayload(),
                    warning = error,
                    // Game data is read at parse time, so a language change needs a re-parse to
                    // take effect. Saying so beats leaving the user to wonder.
                    message = prefixChanged
                        ? "Saved. The Proton prefix takes effect when the application restarts."
                        : "Saved.",
                });
                return;
            }

            case ("GET", _) when path.StartsWith("/api/items/") && long.TryParse(path[11..], out var itemId): {
                var detail = _collection.Get(itemId);
                if (detail is null) { await Json_(context, new { error = "not found" }, 404); return; }
                await Json_(context, detail);
                return;
            }

            case ("POST", _) when path.StartsWith("/api/items/") && path.EndsWith("/transfer"): {
                var segment = path["/api/items/".Length..^"/transfer".Length];
                if (!long.TryParse(segment, out var id)) { await Json_(context, new { error = "bad id" }, 400); return; }
                await TransferAsync(context, id, cancellationToken);
                return;
            }

            case ("GET", "/api/transfers"):
                await Json_(context, _transfers?.Pending ?? []);
                return;

            case ("DELETE", _) when path.StartsWith("/api/transfers/"):
                await CancelTransferAsync(context, path["/api/transfers/".Length..]);
                return;

            case ("GET", _) when path.StartsWith("/api/icons/"): {
                // The name arrives from a URL, so collapse it to a bare filename before it
                // is used as a path.
                var file = Path.GetFileName(Uri.UnescapeDataString(path["/api/icons/".Length..]));
                var full = Path.Combine(LinuxPaths.IconDir, file);
                if (string.IsNullOrEmpty(file) || !File.Exists(full)) {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }
                context.Response.ContentType = "image/png";
                context.Response.Headers["Cache-Control"] = "public, max-age=86400";
                var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
                await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
                context.Response.Close();
                return;
            }

            case ("GET", _):
                // Anything else is the built web UI, when present.
                if (await TryServeStaticAsync(context, path, cancellationToken)) return;
                await Json_(context, new { error = "not found" }, 404);
                return;

            default:
                await Json_(context, new { error = "not found" }, 404);
                return;
        }
    }

    /// <summary>
    /// Queues a transfer and returns immediately.
    ///
    /// This used to block until the hook collected the file. That matched the CLI, but the
    /// hook only deposits while the player has the transfer stash open, so the request could
    /// hang for minutes and a page reload lost track of it entirely. The outcome now arrives
    /// as a `transferCompleted` event instead.
    /// </summary>
    private async Task TransferAsync(HttpListenerContext context, long id, CancellationToken cancellationToken) {
        if (_bridge is null || _transfers is null) {
            await Json_(context, new TransferResult(false, "No Proton prefix found.", null), 503);
            return;
        }

        var request = await ReadJsonAsync<TransferRequest>(context) ?? new TransferRequest();

        using var store = new LootStore(LinuxPaths.DatabaseFile);
        var item = store.GetById(id);
        if (item is null) { await Json_(context, new { error = "not found" }, 404); return; }

        // Refuse rather than queue blindly: an unattended file in the outgoing directory is
        // collected whenever the stash is next opened, possibly days later.
        var startedAt = GameClock.StartTime();
        if (startedAt is null) {
            await Json_(context, new TransferResult(false, "Grim Dawn is not running.", null), 409);
            return;
        }
        if (!_bridge.IsHookLive(startedAt)) {
            await Json_(context, new TransferResult(false, "No hook attached to the running game.", null), 409);
            return;
        }

        // Upstream gates the target choice behind the same setting; without it the request's
        // override is ignored rather than rejected, so a stale client cannot move items between
        // stashes the user did not opt into.
        var allowRetarget = (_server?.Settings ?? AppSettings.Load()).TransferAnyMod;
        var targetMod = allowRetarget ? request.TargetMod : null;
        var targetHardcore = allowRetarget ? request.TargetHardcore : null;

        var result = _transfers.Queue(item, id, request.TimeoutSeconds, targetHardcore, targetMod);
        var pending = result.Transfer;

        // Already on its way: a second click while the first transfer is still waiting for the
        // game. Writing another file would put a second copy of the item in the stash, and the
        // game cannot tell that one of them was never earned — so this is a refusal, and it
        // hands back the transfer already in flight rather than a new handle for the same file.
        // Upstream never reaches this case: it removes the row inside the same call that
        // deposits, so its second click finds nothing to transfer.
        if (result.WasAlreadyQueued) {
            await Json_(context, new {
                transferId    = pending.TransferId,
                itemId        = pending.ItemId,
                queuedPath    = pending.QueuedPath,
                alreadyQueued = true,
                message       = $"{pending.ItemName} is already on its way. "
                              + "Open the transfer stash in game.",
            }, 409);
            return;
        }

        await _events.BroadcastAsync(new HostEvent("transferQueued", new {
            transferId = pending.TransferId,
            itemId     = pending.ItemId,
        }), cancellationToken);

        var retargeted = targetMod is not null || targetHardcore is not null;
        await Json_(context, new {
            transferId = pending.TransferId,
            itemId     = pending.ItemId,
            queuedPath = pending.QueuedPath,
            message    = retargeted
                ? $"Queued for {(targetHardcore ?? item.IsHardcore ? "hardcore" : "softcore")}"
                  + $"{(string.IsNullOrEmpty(targetMod) ? " vanilla" : " " + targetMod)}. "
                  + "Open that transfer stash in game."
                : "Queued. Open the transfer stash in game.",
        }, 202);
    }

    /// <summary>Cancels a queued transfer that the hook has not yet collected.</summary>
    private async Task CancelTransferAsync(HttpListenerContext context, string transferId) {
        if (_transfers is null) { await Json_(context, new { error = "unavailable" }, 503); return; }

        var cancelled = _transfers.Cancel(transferId);
        await Json_(context, new {
            cancelled,
            message = cancelled
                ? "Transfer cancelled; the item stays in your collection."
                : "Too late - the game already took it, or it is not queued.",
        }, cancelled ? 200 : 409);
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken cancellationToken) {
        var wsContext = await context.AcceptWebSocketAsync(null);
        var socket = wsContext.WebSocket;
        _events.Add(socket);

        // The first thing a freshly connected page is told, and the one that has to work when
        // nothing else does: a client with no prefix learns why from this frame.
        await _events.BroadcastAsync(HostEvent.Status(CurrentStatus()), cancellationToken);

        // Commands travel over HTTP; this channel is push-only. Hold it open so the socket
        // stays registered for broadcasts.
        var buffer = new byte[1024];
        try {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested) {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally {
            _events.Remove(socket);
        }
    }

    /// <summary>
    /// Serves the built Preact app from wwwroot, when it has been built. Unknown paths fall
    /// back to index.html so client-side routing works, but only for document requests —
    /// a missing asset should 404 rather than silently return HTML.
    /// </summary>
    private static async Task<bool> TryServeStaticAsync(
        HttpListenerContext context, string path, CancellationToken cancellationToken) {

        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(root)) return false;

        var relative = path == "/" ? "index.html" : path.TrimStart('/');

        // Resolve and confirm the result is still inside wwwroot: the path comes from a URL.
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!candidate.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                                  StringComparison.Ordinal)) {
            return false;
        }

        if (!File.Exists(candidate)) {
            if (Path.HasExtension(relative)) return false;   // a real missing asset
            candidate = Path.Combine(root, "index.html");
            if (!File.Exists(candidate)) return false;
        }

        context.Response.ContentType = ContentTypeFor(candidate);
        // Vite fingerprints asset filenames, so they can be cached hard; index.html cannot.
        context.Response.Headers["Cache-Control"] =
            candidate.Contains("/assets/", StringComparison.Ordinal)
                ? "public, max-age=31536000, immutable"
                : "no-cache";

        var bytes = await File.ReadAllBytesAsync(candidate, cancellationToken);
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();
        return true;
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".html" => "text/html; charset=utf-8",
        ".js"   => "text/javascript; charset=utf-8",
        ".css"  => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg"  => "image/svg+xml",
        ".png"  => "image/png",
        ".woff2"=> "font/woff2",
        _       => "application/octet-stream",
    };

    /// <summary>
    /// The filter checkboxes and the stat fields behind each, plus the classes this
    /// installation defines.
    ///
    /// Served rather than hardcoded in the UI so there is exactly one definition of what "Fire"
    /// means — the one checked against upstream by scripts/verify-filter-groups.sh. The UI had
    /// its own copy once, invented from the shape of the stat names, and it was wrong.
    /// </summary>
    private object FilterCatalogue() {
        static object[] Groups(IReadOnlyList<IAGrim.Core.ItemStats.FilterGroup> groups) =>
            groups.Select(g => (object)new { g.Label, g.Fields }).ToArray();

        return new {
            damage = Groups(IAGrim.Core.ItemStats.FilterGroups.Damage),
            damageOverTime = Groups(IAGrim.Core.ItemStats.FilterGroups.DamageOverTime),
            resistances = Groups(IAGrim.Core.ItemStats.FilterGroups.Resistances),
            misc = Groups(IAGrim.Core.ItemStats.FilterGroups.Misc),
            classes = _collection.Classes(),

            // The two dropdowns above the list. Sent rather than hardcoded in the UI so there is
            // one copy of them, and so verify-slot-filters.sh checks what the UI actually shows.
            slots = IAGrim.Core.ItemStats.SlotFilters.Slots
                .Select(s => (object)new { s.Tag, s.Label, s.ItemClasses, s.Inverse }).ToArray(),
            rarities = IAGrim.Core.ItemStats.SlotFilters.Qualities
                .Select(q => (object)new { q.Tag, q.Label, q.Rarity, q.PrefixRarity }).ToArray(),
        };
    }

    /// <summary>
    /// The settings, plus what the hook will actually see. Those can disagree — the bridge file
    /// lives inside the Wine prefix and can be replaced by Steam or by the Windows tool — and a
    /// silent disagreement means loot stops being captured for no visible reason.
    /// </summary>
    /// <summary>True while an attach attempt is running.</summary>
    private bool Attaching =>
        _server?.AutoAttach?.State(_server.Settings.AutoAttach).Attaching ?? false;

    /// <summary>
    /// The current status, warnings included.
    ///
    /// One method because there are four places that report status and every one of them has to
    /// carry the same bad news. The version of this that had each call site assemble its own
    /// simply omitted the setup warning everywhere, which is how the most consequential state in
    /// the port came to have no words attached to it.
    /// </summary>
    private HostStatus CurrentStatus() =>
        _collection.Status(_paths, _bridge, GameClock.StartTime(), _server?.Settings, Attaching,
                           _server?.GameData?.Running ?? false, _server?.GameData?.Step,
                           _server?.DiscoveryWarning, _server?.HookWarning);

    private object SettingsPayload() {
        var settings = _server?.Settings ?? AppSettings.Load();
        var hook = _bridge is null ? null : BridgeSettings.Read(_bridge);

        // Offered languages are whatever this installation actually ships. Hardcoding a list
        // would let someone pick a language whose archive is absent, which does nothing visible.
        var gameDir = settings.GameDir ?? _paths?.GameDir;
        var languages = gameDir is not null && Directory.Exists(gameDir)
            ? IAGrim.Core.GameData.ItemDatabase.FindLanguages(gameDir)
            : ["EN"];

        return new {
            settings.StashToLootFrom,
            settings.StashToDepositTo,
            settings.Language,
            settings.GameDir,
            settings.PrefixDir,
            settings.TransferAnyMod,
            settings.AutoAttach,
            settings.DatabaseFile,
            databaseInUse = LinuxPaths.DatabaseFile,
            availableLanguages = languages,
            resolvedGameDir = gameDir,
            // The prefix actually in use, which is the bridge's own root walked back up to the
            // prefix rather than anything re-derived — so what is shown is what the hook is
            // being talked to through, not what discovery would find if asked again.
            resolvedPrefixDir = _bridge?.CompatData ?? _bridge?.Prefix ?? _paths?.PrefixDir,
            hook = hook is null ? null : new {
                wineModeEnabled = hook.Value.WineMode,
                stashToLootFrom = hook.Value.LootFrom,
                stashToDepositTo = hook.Value.DepositTo,
                gameDataParsed = hook.Value.Parsed,
            },
        };
    }

    /// <summary>
    /// Scopes a search to one mod and one hardcore branch, which is the only kind of search
    /// upstream can run.
    ///
    /// Its search window reads both from the selected transfer file and there is always one
    /// selected (SplitSearchWindow.UpdateListView); a client that leaves them out is asking a
    /// question upstream cannot ask, and gets an item count that does not match the Windows
    /// tool's for the same collection. The game itself draws the same line: each mod and each
    /// branch has its own transfer stash, and no item crosses between them.
    ///
    /// Vanilla softcore is the fallback because that is where an unmodded game puts everything.
    /// </summary>
    private static ItemQuery Branch(ItemQuery query) => query with {
        Mod = query.Mod ?? "",
        IsHardcore = query.IsHardcore ?? false,
    };

    /// <summary>
    /// Search criteria from the query string. Shared by /api/items and /api/collection, which
    /// upstream also drives from one <c>ItemSearchRequest</c> — the collection view honours the
    /// slot, name and level filters, and ignores the rest.
    /// </summary>
    private static ItemQuery ParseItemQuery(System.Collections.Specialized.NameValueCollection query) =>
        new() {
            Wildcard              = query["q"],
            IsHardcore            = ParseBool(query["hardcore"]),
            Mod                   = query["mod"],
            MinimumLevel          = ParseInt(query["minLevel"], 0),
            MaximumLevel          = ParseInt(query["maxLevel"], 0),
            SocketedOnly          = ParseBool(query["socketed"]) ?? false,
            DuplicatesOnly        = ParseBool(query["duplicates"]) ?? false,
            OrderByLevel          = ParseBool(query["orderByLevel"]) ?? false,
            OrderByNewest         = ParseBool(query["orderByNewest"]) ?? false,
            RecentOnly            = ParseBool(query["recent"]) ?? false,
            // Repeatable, because one UI slot can mean several item classes ("two-handed").
            Slot                  = query.GetValues("slot") ?? [],
            SlotInverse           = ParseBool(query["slotInverse"]) ?? false,
            Rarity                = query["rarity"],
            PrefixRarity          = ParseInt(query["prefixRarity"], 0),
            WithGrantSkillsOnly   = ParseBool(query["grantsSkill"]) ?? false,
            WithSummonerSkillOnly = ParseBool(query["summoner"]) ?? false,
            IsRetaliation         = ParseBool(query["retaliation"]) ?? false,
            PetBonuses            = ParseBool(query["petScope"]) ?? false,
            HasPetBonus           = ParseBool(query["hasPetBonus"]) ?? false,
            Classes               = query.GetValues("mastery") ?? [],
            // Repeatable groups: ?has=offensiveFire,offensiveFireMin means "any fire field",
            // and a second ?has= is ANDed against it, matching upstream's checkbox semantics.
            Filters               = (query.GetValues("has") ?? [])
                                        .Select(g => g.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                                | StringSplitOptions.TrimEntries))
                                        .Where(g => g.Length > 0)
                                        .ToList(),
            // Repeatable: ?stat=offensiveBaseFireMin>=50&stat=characterIntelligence>=40
            StatFilters           = (query.GetValues("stat") ?? [])
                                        .Select(StatValueFilter.Parse)
                                        .Where(f => f is not null)
                                        .Select(f => f!)
                                        .ToList(),
        };

    /// <summary>
    /// Whether a handler answered with an error. The cloud handlers return anonymous objects
    /// describing what happened rather than throwing, so the status code is decided here.
    /// </summary>
    private static bool HasError(object result) =>
        result.GetType().GetProperty("error")?.GetValue(result) is not null;

    private static bool? ParseBool(string? value) =>
        value is null ? null : value is "1" or "true" or "True";

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    /// <summary>A comma-separated list of row ids. Anything that is not one is dropped.</summary>
    private static List<long> ParseIds(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => long.TryParse(part, out var id) ? id : -1)
            .Where(id => id >= 0)
            .ToList();

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerContext context) {
        if (context.Request.ContentLength64 <= 0) return default;
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body)) return default;
        try { return JsonSerializer.Deserialize<T>(body, Json); }
        catch (JsonException) { return default; }
    }

    private static async Task Json_(HttpListenerContext context, object payload, int status = 200) {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json));
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task Text(HttpListenerContext context, string body) {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private const string Banner = """
        iagd-host is running.

          GET  /api/status
          GET  /api/items?q=&skip=&take=
                 &rarity=Epic|Blue|Green|Yellow|White   display colour, not the game's tier
                 &prefixRarity=2                        at least N rare affixes
                 &grantsSkill=1  &summoner=1  &retaliation=1
                 &petScope=1                            scope stat filters to the pet
                 &hasPetBonus=1                         grants any pet bonus
                 &mastery=Occultist                     repeatable
                 &has=offensiveFire,offensiveFireMin    repeatable; group is OR, groups AND
                 &socketed=1  &duplicates=1  &recent=1
                 &slot=ItemRelic  &slotInverse=1        slot repeatable
                 &minLevel=  &maxLevel=  &mod=  &hardcore=
                 &stat=offensiveBaseFireMin>=50         repeatable; a+b sums fields
          GET  /api/items/{id}
          GET  /api/items/details?ids=1,2 several items with their own tooltips
          GET  /api/collection            every legendary/epic, and what you own
          GET  /api/collection/stats      owned counts by rarity and slot
          GET  /api/sets?q=               item sets and set completion
          GET  /api/filters               filter checkboxes and the fields behind each
          GET  /api/mods                  mods with items in the collection or parsed
          GET  /api/browse                is a native file chooser available
          POST /api/browse                {directory, title, path} -> chosen path
          POST /api/merge                 {path, dryRun} -> merge another collection in
          GET  /api/settings              settings, and what the hook actually sees
          PUT  /api/settings              {stashToLootFrom, stashToDepositTo, language, gameDir}
          POST /api/items/{id}/transfer
          GET  /api/transfers
          DELETE /api/transfers/{transferId}
          GET  /api/icons/{file}
          WS   /ws
        """;
}

/// <summary>
/// Kept as the host's name for "when did the running game start", but the detection itself
/// lives in <see cref="GameProcess"/> — the attach path in IAGrim.Platform has to agree with
/// it exactly, and two copies of that rule is how the injector came to be mistaken for the
/// game in the first place.
/// </summary>
internal static class GameClock {
    public static DateTime? StartTime() => GameProcess.StartTime();
}
