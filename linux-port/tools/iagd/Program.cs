using IAGrim.Core.GameData;
using IAGrim.Core.Imaging;
using IAGrim.Core.Backup;
using IAGrim.Core.Stash;
using IAGrim.Core.ItemStats;
using IAGrim.Platform;

namespace IAGrim.Cli;

/// <summary>
/// Headless front end for the ported core: import loot, list the collection, send items
/// back to the game. This is the Phase 2 exit criterion made usable — everything the web UI
/// will eventually drive, exercised from a terminal first so the core can be trusted before
/// a UI is layered on it.
/// </summary>
internal static class Program {
    private static int Main(string[] args) {
        var command = args.FirstOrDefault() ?? "status";

        // Every entry point picks the database the same way, so the CLI and a running host
        // never end up working on different collections.
        if (!Startup.SelectDatabase(args, AppSettings.Load(), Console.Out)) return 1;

        try {
            return command switch {
                "status"   => Status(),
                "import"   => Import(),
                "watch"    => Watch(args),
                "parse"    => Parse(),
                "stats"    => Stats(),
                "list"     => List(args),
                "transfer" => Transfer(args),
                "settings" => Settings(args),
                "backup"   => Backup(args),
                "export"   => Export(args),
                "import-file" => ImportFile(args),
                "stash"    => StashCommand(args),
                "merge"    => Merge(args),
                "replica"  => Replica(args),
                "install-desktop" => InstallDesktop(args),
                "attach"   => Attach(),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(command),
            };
        }
        catch (DirectoryNotFoundException ex) {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static (SteamPaths, PrefixBridge) Resolve() {
        var paths = SteamPaths.Discover();

        // The same hand-set prefix the client uses, so the two agree about which bridge they are
        // talking through. A CLI that quietly went back to discovery here would report on, and
        // attach to, a different prefix than the one the user configured.
        var configured = AppSettings.Load().PrefixDir;
        var bridge = (configured is null ? PrefixBridge.Discover(paths) : PrefixBridge.ForPrefix(configured))
            ?? throw new DirectoryNotFoundException(
                configured is null
                    ? "No Grim Dawn Proton prefix found. Launch the game through Steam once first."
                    : $"{configured} is not a Wine prefix: it contains neither drive_c nor pfx/drive_c.");
        return (paths, bridge);
    }

    private static int Status() {
        var (paths, bridge) = Resolve();

        // The configured installation, not merely the discovered one — the same distinction the
        // staleness check below already makes. Reporting discovery alone said "not found" over a
        // path the user had set by hand, and everything downstream reads as broken from there.
        Console.WriteLine("Environment");
        Console.WriteLine($"  game      {AppSettings.Load().GameDir ?? paths.GameDir ?? "not found"}");
        Console.WriteLine($"  prefix    {bridge.CompatData ?? paths.PrefixDir ?? "not found"}");
        Console.WriteLine($"  saves     {paths.SavePath ?? "not found"}  ({paths.SaveSource})");
        Console.WriteLine($"  bridge    {bridge.Root}");
        Console.WriteLine($"  database  {LinuxPaths.DatabaseFile}");
        Console.WriteLine();

        var started = GameStartTime();
        Console.WriteLine("Game");
        Console.WriteLine($"  running   {(started is null ? "no" : $"yes, since {started:HH:mm:ss}")}");
        var hooked = bridge.IsHookLive(started);
        Console.WriteLine($"  hooked    {(hooked ? "yes" : "no")}");
        if (started is not null && !hooked) {
            Console.WriteLine(AppSettings.Load().AutoAttach
                ? "            the running host attaches automatically; start it with 'iagd' or 'iagd-host'"
                : "            autoAttach is off — attach by hand with 'iagd attach'");
        }
        Console.WriteLine();

        var pending = bridge.PendingLootFiles().Count();
        using var store = new LootStore(LinuxPaths.DatabaseFile);
        Console.WriteLine("Collection");
        Console.WriteLine($"  items     {store.CountItems()}");
        Console.WriteLine($"  pending   {pending} loot file(s) waiting for import");
        if (pending > 0) {
            Console.WriteLine("            run:  iagd import");
        }

        // A Grim Dawn patch silently invalidates every name, level and icon; so does changing
        // the language without re-parsing. Neither announces itself.
        var settings = AppSettings.Load();
        var staleness = GameDataStatus.Check(LinuxPaths.DatabaseFile,
                                             settings.GameDir ?? paths.GameDir, settings.Language);
        if (staleness.IsStale) {
            Console.WriteLine();
            Console.WriteLine("Game data");
            Console.WriteLine($"  {staleness.Reason}");
            Console.WriteLine("            run:  iagd parse");
        }

        // The rarity and level filters read columns 'iagd stats' fills in, so an unanalysed
        // collection makes them return nothing — which reads as a bug, not a missing step.
        var unanalysed = store.CountItemsNeedingStats();
        if (unanalysed > 0) {
            Console.WriteLine($"  analysed  {store.CountItems() - unanalysed} of {store.CountItems()}");
            Console.WriteLine("            run:  iagd stats     (rarity and level filters need it)");
        }
        return 0;
    }

    private static int Import() {
        var (_, bridge) = Resolve();
        using var store = new LootStore(LinuxPaths.DatabaseFile);
        var watcher = new LootWatcher(bridge, store);

        var results = watcher.ImportPending();
        if (results.Count == 0) {
            Console.WriteLine("Nothing to import.");
            return 0;
        }

        foreach (var result in results) {
            var name = Path.GetFileName(result.File);
            if (result.Error is not null) {
                Console.WriteLine($"  FAILED    {name}: {result.Error}");
                Console.WriteLine($"            file kept — it is the only copy of that item");
            }
            else if (result.Duplicate) {
                Console.WriteLine($"  duplicate {result.Item!.PlainName}");
            }
            else {
                Console.WriteLine($"  imported  {result.Item!.PlainName}  ({result.Item.Stats.Count} stats)");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{store.CountItems()} item(s) in the collection.");
        return results.Any(r => r.Error is not null) ? 1 : 0;
    }

    private static int Watch(string[] args) {
        var intervalSeconds = ArgValue(args, "--interval") is { } raw && int.TryParse(raw, out var parsed)
            ? parsed : 2;

        var (_, bridge) = Resolve();
        using var store = new LootStore(LinuxPaths.DatabaseFile);
        var watcher = new LootWatcher(bridge, store);

        watcher.OnImported += result => {
            var stamp = DateTime.Now.ToString("HH:mm:ss");
            if (result.Error is not null) {
                Console.WriteLine($"[{stamp}] FAILED    {Path.GetFileName(result.File)}: {result.Error}");
            }
            else if (result.Duplicate) {
                Console.WriteLine($"[{stamp}] duplicate {result.Item!.PlainName}");
            }
            else {
                Console.WriteLine($"[{stamp}] LOOTED    {result.Item!.PlainName}");
            }
        };

        Console.WriteLine($"Watching {bridge.LootIncoming}");
        Console.WriteLine($"Polling every {intervalSeconds}s. Ctrl+C to stop.");
        Console.WriteLine(new string('-', 70));

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

        while (!cancellation.IsCancellationRequested) {
            watcher.ImportPending();
            try { Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellation.Token).Wait(); }
            catch (AggregateException) { break; }
        }

        Console.WriteLine();
        Console.WriteLine($"Stopped. {store.CountItems()} item(s) in the collection.");
        return 0;
    }

    /// <summary>
    /// Reads Grim Dawn's own item definitions into the database. Needed once, and again
    /// after a game patch — a looted item stores only a record path, so without this there
    /// is no name, class or icon for anything the hook did not capture.
    /// </summary>
    private static int Parse() {
        var settings = AppSettings.Load();
        var (paths, _) = Resolve();

        // A configured path wins over discovery: discovery is a heuristic over Steam library
        // folders, and someone with a non-Steam or relocated install needs a way to say so.
        var gameDir = settings.GameDir ?? paths.GameDir;
        if (gameDir is null) {
            Console.Error.WriteLine("error: Grim Dawn installation not found.");
            Console.Error.WriteLine("       set it with:  iagd settings gameDir /path/to/Grim Dawn");
            return 1;
        }
        if (!Directory.Exists(gameDir)) {
            Console.Error.WriteLine($"error: configured gameDir does not exist: {gameDir}");
            return 1;
        }

        // The work itself lives in IAGrim.Core so the client can do it without a terminal,
        // which is how upstream has always worked. This prints what it would have printed.
        GameDataParse.Run(gameDir, LinuxPaths.DatabaseFile, LinuxPaths.BackupDir,
                          settings.Language, Console.WriteLine);

        // Re-parsing reassigns every id_databaseitem, so the game stat rows keyed by them were
        // dropped. Until the analysis pass rebuilds them, the record-driven filters (damage
        // type, pet bonus, mastery, retaliation) match nothing — silently, which is the problem.
        using (var store2 = new LootStore(LinuxPaths.DatabaseFile)) {
            if (store2.CountItems() > 0) {
                Console.WriteLine();
                Console.WriteLine("Now run:  iagd stats     (re-parsing cleared the computed stats)");
                Console.WriteLine("The client does this by itself when it starts.");
            }
        }
        return 0;
    }
    /// </summary>
    private static int Stats() {
        var (paths, _) = Resolve();
        var gameDir = AppSettings.Load().GameDir ?? paths.GameDir;
        if (gameDir is null) {
            Console.Error.WriteLine("error: Grim Dawn installation not found.");
            Console.Error.WriteLine("       set it with:  iagd settings gameDir /path/to/Grim Dawn");
            return 1;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var service = new StatPrecomputeService(LinuxPaths.DatabaseFile, gameDir);
        var result = service.Run(message => Console.WriteLine($"  {message}"));
        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine($"{result.ItemsComputed:N0} of {result.ItemsProcessed:N0} items rolled "
                          + $"({result.StatsWritten:N0} stat values) in {stopwatch.Elapsed.TotalSeconds:F1}s.");
        if (result.Skipped > 0) {
            Console.WriteLine($"{result.Skipped:N0} skipped: no seed, or the item uses rollable fields");
            Console.WriteLine("the engine does not model — approximate numbers would make the filters lie.");
            Console.WriteLine("Those items still get their rarity and level; only the numeric stats are absent.");
        }
        return 0;
    }

    /// <summary>
    /// Items.arc per expansion. Extraction skips anything already present, so re-running is
    /// cheap.
    /// </summary>
    private static int List(string[] args) {
        using var gameData = new GameDataStore(LinuxPaths.DatabaseFile);
        var rows = gameData.Collection(ArgValue(args, "--name")).ToList();

        if (rows.Count == 0) {
            Console.WriteLine("Nothing to show.");
            return 0;
        }

        var haveTemplates = gameData.TemplateCount() > 0;
        foreach (var row in rows) {
            var display = row.LootedName ?? row.TemplateName ?? "<unnamed>";
            Console.WriteLine($"#{row.Id,-4} {display}");

            var facts = new List<string>();
            if (row.Level > 0) facts.Add($"lvl {row.Level}");
            if (row.ItemClass is not null) facts.Add(row.ItemClass);
            if (row.IconFile is not null) facts.Add($"icon {row.IconFile}");
            if (facts.Count > 0) Console.WriteLine($"      {string.Join("  ", facts)}");

            Console.WriteLine($"      {row.BaseRecord}  seed={row.Seed}");
        }

        Console.WriteLine();
        Console.WriteLine($"{rows.Count} item(s).");
        if (!haveTemplates) {
            Console.WriteLine("No game data parsed yet — run 'iagd parse' for names, levels and icons.");
        }
        return 0;
    }

    /// <summary>
    /// Sends an item back into the game.
    ///
    /// This is the one irreversible operation here, so it is built defensively. The hook
    /// gives no direct acknowledgement: it collects the queued file and moves it aside, and
    /// that disappearance is the only evidence the item was created. So the database row is
    /// removed strictly after collection — delete earlier and the item is lost, never delete
    /// and it exists twice.
    /// </summary>
    private static int Transfer(string[] args) {
        if (args.Length < 2 || !long.TryParse(args[1], out var id)) {
            Console.Error.WriteLine("usage: iagd transfer <item-id> [--timeout <seconds>] [--keep]");
            Console.Error.WriteLine("       [--to-mod <name>] [--to-hardcore|--to-softcore]  (needs transferAnyMod)");
            Console.Error.WriteLine("       run 'iagd list' for item ids");
            return 1;
        }

        var timeoutSeconds = ArgValue(args, "--timeout") is { } raw && int.TryParse(raw, out var parsed)
            ? parsed : 120;
        var keep = args.Contains("--keep");

        // Sending to a different branch than the item came from. Gated on the same setting
        // upstream gates its stash picker with, because moving an item from softcore to
        // hardcore is not something the game can undo.
        var settings = AppSettings.Load();
        var targetMod = ArgValue(args, "--to-mod");
        bool? targetHardcore = args.Contains("--to-hardcore") ? true
                             : args.Contains("--to-softcore") ? false
                             : null;

        if ((targetMod is not null || targetHardcore is not null) && !settings.TransferAnyMod) {
            Console.Error.WriteLine("error: choosing a target stash needs transferAnyMod enabled.");
            Console.Error.WriteLine("       enable it with:  iagd settings transferAnyMod true");
            return 1;
        }

        var (_, bridge) = Resolve();
        using var store = new LootStore(LinuxPaths.DatabaseFile);

        var item = store.GetById(id);
        if (item is null) {
            Console.Error.WriteLine($"error: no item with id {id}.");
            return 1;
        }

        var started = GameStartTime();
        if (started is null) {
            Console.Error.WriteLine("error: Grim Dawn is not running. Nothing would collect the transfer.");
            return 1;
        }
        if (!bridge.IsHookLive(started)) {
            Console.Error.WriteLine("error: no hook attached to the running game. Run scripts/attach-gd.sh first.");
            return 1;
        }

        Console.WriteLine($"Transferring: {item.PlainName ?? item.BaseRecord}");
        Console.WriteLine($"  record  {item.BaseRecord}");
        Console.WriteLine($"  seed    {item.Seed}   hardcore={item.IsHardcore}");
        Console.WriteLine();

        var transfer = new TransferService(bridge);
        var queued = transfer.Queue(item, targetHardcore, targetMod);

        if (targetMod is not null || targetHardcore is not null) {
            Console.WriteLine($"  target  {(targetHardcore ?? item.IsHardcore ? "hardcore" : "softcore")}"
                              + $" / {(string.IsNullOrEmpty(targetMod) ? "vanilla" : targetMod)}");
        }
        Console.WriteLine($"Queued at {queued}");
        Console.WriteLine();
        Console.WriteLine("**Open the transfer stash in game.** The hook only deposits while it is open.");
        Console.WriteLine($"Waiting up to {timeoutSeconds}s for the game to take it...");

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline) {
            if (transfer.IsCollected(queued)) {
                Console.WriteLine();
                Console.WriteLine("COLLECTED — the item is in your stash.");

                if (keep) {
                    Console.WriteLine($"Kept in the database (--keep). It now exists in BOTH places;");
                    Console.WriteLine($"remove it with care, or you will duplicate it.");
                    return 0;
                }

                store.Delete(id);
                Console.WriteLine($"Removed from the collection. {store.CountItems()} item(s) left.");
                Console.WriteLine($"A copy of the loot file remains in {LinuxPaths.LootBackupDir}.");
                return 0;
            }
            Thread.Sleep(1000);
        }

        Console.WriteLine();
        Console.WriteLine("NOT collected within the timeout. The item is unchanged in the database.");
        Console.WriteLine("The transfer file is still queued and will be picked up whenever the");
        Console.WriteLine("transfer stash is next opened — so either open it, or cancel:");
        Console.WriteLine($"  rm '{queued}'");
        return 1;
    }

    /// <summary>
    /// Copies the collection, lists copies, or restores one.
    ///
    /// This is the only irreplaceable data here — templates, icons and computed stats all
    /// regenerate from the game files, but a looted item exists nowhere else once its loot file
    /// has been consumed.
    /// </summary>
    private static int Backup(string[] args) {
        var restore = ArgValue(args, "--restore");

        if (restore is not null) {
            // Accept either a full path or just the filename, since that is what 'iagd backup'
            // prints and therefore what people will paste back.
            var path = File.Exists(restore) ? restore : Path.Combine(LinuxPaths.BackupDir, restore);
            if (!File.Exists(path)) {
                Console.Error.WriteLine($"error: no such backup: {restore}");
                return 1;
            }

            var displaced = DatabaseBackup.Restore(path, LinuxPaths.DatabaseFile);
            Console.WriteLine($"Restored {Path.GetFileName(path)}");
            Console.WriteLine($"The database it replaced was kept at:");
            Console.WriteLine($"  {displaced}");

            using var restored = new LootStore(LinuxPaths.DatabaseFile);
            Console.WriteLine($"{restored.CountItems()} item(s) in the restored collection.");
            return 0;
        }

        if (!args.Contains("--list")) {
            var created = DatabaseBackup.Create(LinuxPaths.DatabaseFile, LinuxPaths.BackupDir);
            Console.WriteLine($"Wrote {created.Path} ({created.Bytes / 1024.0:N0} KiB)");
            Console.WriteLine();
        }

        var backups = DatabaseBackup.List(LinuxPaths.BackupDir);
        if (backups.Count == 0) {
            Console.WriteLine("No backups yet.");
            return 0;
        }

        Console.WriteLine($"Backups in {LinuxPaths.BackupDir} (keeping the newest {DatabaseBackup.KeepCount}):");
        foreach (var backup in backups) {
            Console.WriteLine($"  {backup.Created:yyyy-MM-dd HH:mm}  {backup.Bytes / 1024.0,8:N0} KiB  {Path.GetFileName(backup.Path)}");
        }
        Console.WriteLine();
        Console.WriteLine("Restore with:  iagd backup --restore <filename>");
        return 0;
    }

    /// <summary>
    /// Writes the collection in the GD Stash / Mambastash interchange format, which is what the
    /// Grim Dawn tool ecosystem actually shares items in.
    /// </summary>
    private static int Export(string[] args) {
        if (args.Length < 2 || args[1].StartsWith('-')) {
            Console.Error.WriteLine("usage: iagd export <file> [--hardcore | --softcore] [--mod <name>]");
            return 1;
        }

        bool? hardcore = args.Contains("--hardcore") ? true
                       : args.Contains("--softcore") ? false
                       : null;

        var count = ItemExport.Export(LinuxPaths.DatabaseFile, args[1], hardcore, ArgValue(args, "--mod"));
        Console.WriteLine($"Exported {count:N0} item(s) to {args[1]}");
        if (hardcore is null && count > 0) {
            Console.WriteLine("Note: this includes both hardcore and softcore items. Use --hardcore or");
            Console.WriteLine("--softcore to keep them apart — they are separate stashes in game.");
        }
        return 0;
    }

    /// <summary>Reads a GD Stash file into the collection, skipping items already present.</summary>
    private static int ImportFile(string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("usage: iagd import-file <file> [--mod <name>]");
            return 1;
        }
        if (!File.Exists(args[1])) {
            Console.Error.WriteLine($"error: no such file: {args[1]}");
            return 1;
        }

        // Importing adds rows to the one thing that cannot be regenerated, so there is a copy
        // to go back to if the file turns out to hold something unexpected.
        var safety = DatabaseBackup.Create(LinuxPaths.DatabaseFile, LinuxPaths.BackupDir, "before-import");

        try {
            var (imported, skipped, refused) = ItemExport.Import(LinuxPaths.DatabaseFile, args[1],
                                                                 ArgValue(args, "--mod"));
            Console.WriteLine($"Imported {imported:N0} item(s); skipped {skipped:N0} already present.");
            if (refused > 0) {
                Console.WriteLine($"{refused:N0} not collected: components, crafting materials, "
                                  + "quest items and stacks, which Item Assistant has never kept.");
            }
            if (imported > 0) {
                Console.WriteLine("Run 'iagd stats' to compute their rolled values.");
            }
            Console.WriteLine($"A copy of the previous collection is at {Path.GetFileName(safety.Path)}.");
            return 0;
        }
        catch (InvalidDataException ex) {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine("The collection was not modified.");
            return 1;
        }
    }

    /// <summary>
    /// One attach attempt, for when the host is not running or something needs forcing.
    ///
    /// The host does this on its own while it runs; this is the same operation without it.
    /// </summary>
    private static int Attach() {
        var (_, bridge) = Resolve();
        var started = GameStartTime();

        if (started is null) {
            Console.Error.WriteLine("error: Grim Dawn is not running.");
            return 1;
        }
        if (bridge.IsHookLive(started)) {
            Console.WriteLine("Already attached.");
            return 0;
        }

        var attacher = new HookAttacher(bridge);
        if (!attacher.IsAvailable) {
            Console.Error.WriteLine($"error: attach script not found at {attacher.ScriptPath}");
            return 1;
        }

        Console.WriteLine("Attaching...");
        var result = attacher.AttachAsync(started, CancellationToken.None).GetAwaiter().GetResult();

        switch (result.Outcome) {
            case AttachOutcome.Attached:
                Console.WriteLine($"Attached — {result.Detail}.");
                return 0;
            case AttachOutcome.NotReady:
                Console.WriteLine($"Not yet: {result.Detail}.");
                Console.WriteLine("Load a character and try again — or leave the host running, which retries.");
                return 0;
            default:
                Console.Error.WriteLine($"Failed: {result.Detail}");
                return 1;
        }
    }

    /// <summary>
    /// Installs a desktop entry and icon into the user's own data directories.
    ///
    /// This is how an icon actually reaches a Linux taskbar. Setting the window's icon is not
    /// enough on its own: a desktop shell matches a window to an installed <c>.desktop</c> entry
    /// (by <c>StartupWMClass</c>, falling back to the executable name) and takes the icon from
    /// there. Without an entry there is nothing to match, and the shell falls back to a generic
    /// placeholder.
    ///
    /// Writes only into <c>~/.local/share</c> — never system directories — so it needs no
    /// privileges and is trivially reversible.
    /// </summary>
    private static int InstallDesktop(string[] args) {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                       ?? Path.Combine(home, ".local", "share");

        // The icon ships beside whichever executable is running; both the app and this CLI
        // carry a copy in a package build.
        var icon = new[] {
                Path.Combine(AppContext.BaseDirectory, "assets", "iagd.png"),
                Path.Combine(AppContext.BaseDirectory, "..", "app", "assets", "iagd.png"),
            }
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);

        if (icon is null) {
            Console.Error.WriteLine("error: could not find assets/iagd.png next to the executable.");
            return 1;
        }

        // Several sizes rather than one: a panel picks the nearest and a 256px source scaled to
        // 22px by the toolkit looks noticeably worse than one rendered at that size. Sources are
        // generated by packaging/make-icon.sh, so whichever exist are copied.
        var installedIcons = new List<string>();
        foreach (var size in new[] { 32, 48, 64, 128, 256 }) {
            var source = size == 256
                ? icon
                : Path.Combine(Path.GetDirectoryName(icon)!, $"iagd-{size}.png");
            if (!File.Exists(source)) continue;

            var target = Path.Combine(dataHome, "icons", "hicolor", $"{size}x{size}", "apps", "iagd.png");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            installedIcons.Add(target);
        }

        if (installedIcons.Count == 0) {
            Console.Error.WriteLine("error: no icon files to install.");
            return 1;
        }
        var iconTarget = installedIcons[^1];

        // What the menu entry should launch: the desktop app, not this CLI.
        //
        // Both publish an executable named "iagd", so "a sibling called iagd" finds this very
        // binary and produces an entry that opens a terminal tool when clicked. The package
        // launchers therefore say so explicitly, and --exec overrides for anything unusual.
        var explicitExec = ArgValue(args, "--exec")
                           ?? Environment.GetEnvironmentVariable("IAGD_APP_EXEC");

        var executable = Environment.ProcessPath ?? "iagd";
        string? appLauncher = explicitExec;

        if (appLauncher is null) {
            var sibling = Path.Combine(Path.GetDirectoryName(executable) ?? ".", "iagd");
            var isSelf = Path.GetFullPath(sibling) == Path.GetFullPath(executable);
            if (File.Exists(sibling) && !isSelf) appLauncher = sibling;
        }

        if (appLauncher is null) {
            Console.Error.WriteLine("error: could not work out what to launch.");
            Console.Error.WriteLine("       pass it explicitly:  iagd install-desktop --exec /path/to/iagd");
            return 1;
        }
        if (!File.Exists(appLauncher)) {
            Console.Error.WriteLine($"error: no such executable: {appLauncher}");
            return 1;
        }
        appLauncher = Path.GetFullPath(appLauncher);

        var desktopTarget = Path.Combine(dataHome, "applications", "iagd.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(desktopTarget)!);
        File.WriteAllText(desktopTarget, $"""
            [Desktop Entry]
            Type=Application
            Name=Item Assistant for Grim Dawn
            Comment=Manage your Grim Dawn stash
            Exec={appLauncher}
            Icon=iagd
            Categories=Utility;
            Terminal=false
            StartupWMClass=iagd

            """);

        Console.WriteLine("Installed:");
        Console.WriteLine($"  {desktopTarget}");
        Console.WriteLine($"  {iconTarget}  (+{installedIcons.Count - 1} other size(s))");
        Console.WriteLine($"  launches: {appLauncher}");
        Console.WriteLine();
        Console.WriteLine("The menu may take a moment to notice. To remove it, delete those two files.");

        // Refresh the caches if the tools are there; harmless when they are not.
        // kbuildsycoca6 is the one that matters on KDE: Plasma reads desktop entries from its own
        // cache, so without it a new entry is invisible until something else happens to rebuild
        // it — and an unmatched window shows a blank icon.
        foreach (var (tool, toolArgs) in new[] {
                     ("update-desktop-database", Path.Combine(dataHome, "applications")),
                     ("gtk-update-icon-cache", Path.Combine(dataHome, "icons", "hicolor")),
                     ("kbuildsycoca6", ""),
                     ("kbuildsycoca5", ""),
                 }) {
            try {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = tool, Arguments = toolArgs,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                });
                process?.WaitForExit(3000);
            }
            catch (Exception) { /* not installed; the shell will pick it up on its own */ }
        }

        return 0;
    }

    /// <summary>
    /// Asks the running game to render tooltips for items that arrived without one.
    ///
    /// Items the hook captured already have theirs. Items imported from a file — the transfer
    /// stash, or a GD Stash export — do not: those formats record what an item *is*, not how the
    /// game draws it. The hook can produce the real thing, but only while the game is running.
    /// </summary>
    private static int Replica(string[] args) {
        var (_, bridge) = Resolve();
        using var store = new LootStore(LinuxPaths.DatabaseFile);
        var service = new ReplicaService(bridge);

        // Collect first: answers may be waiting from a previous session.
        var completed = service.CollectResults(store);
        if (completed > 0) Console.WriteLine($"Filled in {completed} item(s) from earlier requests.");

        var missing = store.ItemsMissingReplica(int.MaxValue).Count;
        Console.WriteLine($"{missing:N0} item(s) have no tooltip.");
        if (missing == 0) return 0;

        var started = GameStartTime();
        if (started is null || !bridge.IsHookLive(started)) {
            Console.WriteLine();
            Console.WriteLine("Grim Dawn is not running with the hook attached, so nothing can render them.");
            Console.WriteLine("Start the game (and attach the hook), then run this again — or just leave");
            Console.WriteLine("the host running, which does this automatically.");
            return 0;
        }

        var requested = service.RequestMissing(store);
        Console.WriteLine($"Requested {requested} (max {ReplicaService.MaxInFlight} outstanding at a time).");
        Console.WriteLine("The game answers as it renders them; run this again to collect.");
        return 0;
    }

    /// <summary>
    /// Merges another Item Assistant collection into this one.
    /// </summary>
    private static int Merge(string[] args) {
        if (args.Length < 2 || args[1].StartsWith('-')) {
            Console.Error.WriteLine("usage: iagd merge <other-userdata.db> [--dry-run]");
            return 1;
        }

        var source = args[1];
        var dryRun = args.Contains("--dry-run");

        // The destination gains rows that cannot be regenerated, so there is something to go
        // back to. Skipped for a dry run, which writes nothing.
        if (!dryRun) {
            var safety = DatabaseBackup.Create(LinuxPaths.DatabaseFile, LinuxPaths.BackupDir, "before-merge");
            Console.WriteLine($"Backed up to {Path.GetFileName(safety.Path)} first.");
        }

        // A merge of a real collection takes long enough to look hung. Only when a person is
        // watching: redirected output gets the summary and nothing else, so a log stays readable.
        var lastDrawn = DateTime.MinValue;
        Action<MergeProgress>? report = Console.IsOutputRedirected ? null : p => {
            if (p.Done < p.Total && DateTime.UtcNow - lastDrawn < TimeSpan.FromMilliseconds(100)) return;
            lastDrawn = DateTime.UtcNow;
            var percent = p.Total == 0 ? 100 : p.Done * 100 / p.Total;
            Console.Write($"\r  {percent,3}%  {p.Done:N0} of {p.Total:N0} read, {p.Imported:N0} new   ");
        };

        try {
            var result = CollectionMerge.Merge(LinuxPaths.DatabaseFile, source, dryRun, report);
            if (report is not null) Console.Write("\r" + new string(' ', 50) + "\r");

            Console.WriteLine();
            Console.WriteLine($"{(dryRun ? "Would import" : "Imported")} {result.Imported:N0} of "
                              + $"{result.Considered:N0} item(s) from {source}");
            Console.WriteLine($"  {result.Duplicates:N0} already present (identical records, seeds, stack and branch)");
            if (result.Rejected > 0) {
                Console.WriteLine($"  {result.Rejected:N0} skipped: no base record");
            }

            if (!dryRun && result.Imported > 0) {
                Console.WriteLine();
                Console.WriteLine("Run 'iagd stats' to name them and compute their rolled values.");
            }
            if (dryRun) {
                Console.WriteLine();
                Console.WriteLine("Nothing was written. Run without --dry-run to do it.");
            }
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException) {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Reads Grim Dawn's shared transfer stash, and optionally imports what is in it.
    ///
    /// This is the migration path: everything already sitting in the shared stash, without
    /// having to deposit it again item by item. Reading only — nothing writes the transfer file,
    /// here or upstream.
    /// </summary>
    private static int StashCommand(string[] args) {
        var (paths, _) = Resolve();
        if (paths.SavePath is null) {
            Console.Error.WriteLine("error: could not find Grim Dawn's save directory.");
            return 1;
        }

        var files = TransferStash.Find(paths.SavePath, args.Contains("--include-downgrades"));
        if (files.Count == 0) {
            Console.WriteLine($"No transfer stash found under {paths.SavePath}");
            Console.WriteLine("(--include-downgrades also looks for stashes saved with an expansion disabled)");
            return 0;
        }

        var wantImport = args.Contains("--import");
        LootStore? store = null;
        var imported = 0;
        var skipped = 0;
        var refused = 0;

        if (wantImport) {
            // Importing adds to the one thing that cannot be regenerated.
            var safety = DatabaseBackup.Create(LinuxPaths.DatabaseFile, LinuxPaths.BackupDir, "before-stash-import");
            Console.WriteLine($"Backed up the collection to {Path.GetFileName(safety.Path)} first.");
            Console.WriteLine();
            store = new LootStore(LinuxPaths.DatabaseFile);
        }

        try {
            foreach (var file in files) {
                var contents = TransferStash.Read(file.Path, out var error);
                var label = $"{Path.GetFileName(file.Path)}{(file.Mod.Length > 0 ? $" [{file.Mod}]" : "")}";

                if (contents is null) {
                    Console.WriteLine($"  {label,-28} could not read: {error}");
                    continue;
                }

                Console.WriteLine($"  {label,-28} v{contents.Version}  {contents.Tabs.Count} tab(s), "
                                  + $"{contents.ItemCount} item(s){(file.IsHardcore ? "  hardcore" : "")}");

                if (store is null) continue;

                foreach (var item in contents.AllItems) {
                    // The stash records the mod in its own label; the folder name is the
                    // fallback, since a vanilla stash has an empty label.
                    var mod = contents.ModLabel.Length > 0 ? contents.ModLabel : file.Mod;

                    var looted = new LootedItem {
                        Mod = mod,
                        IsHardcore = file.IsHardcore,
                        BaseRecord = item.BaseRecord,
                        PrefixRecord = Blank(item.PrefixRecord),
                        SuffixRecord = Blank(item.SuffixRecord),
                        Seed = item.Seed,
                        RerollsUsed = item.Rerolls,
                        ModifierRecord = Blank(item.ModifierRecord),
                        MateriaRecord = Blank(item.MateriaRecord),
                        RelicCompletionBonusRecord = Blank(item.RelicCompletionBonusRecord),
                        RelicSeed = item.RelicSeed,
                        EnchantmentRecord = Blank(item.EnchantmentRecord),
                        EnchantmentSeed = item.EnchantmentSeed,
                        TransmuteRecord = Blank(item.TransmuteRecord),
                        AscendantAffixNameRecord = Blank(item.AscendantRecord),
                        AscendantAffix2hNameRecord = Blank(item.AscendantRecord2H),
                        StackCount = Math.Max(1, item.StackCount),
                        // No tooltip: the stash file stores what an item *is*, not how the game
                        // renders it. Names come from ItemTemplate, values from 'iagd stats'.
                        Stats = [],
                    };

                    // The same admission rules the hook applies while looting: a stash is full
                    // of components and potions that Item Assistant has never collected.
                    if (!ItemAdmission.IsCollectable(looted.BaseRecord, looted.StackCount)) {
                        refused++;
                        continue;
                    }
                    if (store.Exists(looted)) { skipped++; continue; }
                    store.Insert(looted);
                    imported++;
                }
            }
        }
        finally {
            store?.Dispose();
        }

        if (wantImport) {
            Console.WriteLine();
            Console.WriteLine($"Imported {imported:N0} item(s); skipped {skipped:N0} already in the collection.");
            if (refused > 0) {
                Console.WriteLine($"{refused:N0} not collected: components, crafting materials, "
                                  + "quest items and stacks, which Item Assistant has never kept.");
            }
            if (imported > 0) {
                Console.WriteLine("Run 'iagd stats' to name them and compute their rolled values.");
            }
            Console.WriteLine();
            Console.WriteLine("The stash itself is untouched — these items are in both places now.");
        }
        else {
            Console.WriteLine();
            Console.WriteLine("Add --import to copy these into the collection.");
        }
        return 0;
    }

    private static string? Blank(string value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Shows or changes settings.
    ///
    /// The stash-tab settings are the ones that matter operationally: they reach the hook only
    /// through the bridge file, so this writes both and then reports what the hook will actually
    /// see. Those two can disagree if the prefix was rebuilt, and a silent disagreement means
    /// loot stops being captured for no visible reason.
    /// </summary>
    private static int Settings(string[] args) {
        var settings = AppSettings.Load();

        if (args.Length >= 3) {
            var key = args[1].ToLowerInvariant();
            var value = args[2];

            switch (key) {
                case "stashtolootfrom" when int.TryParse(value, out var from):
                    settings.StashToLootFrom = Math.Max(0, from); break;
                case "stashtodepositto" when int.TryParse(value, out var to):
                    settings.StashToDepositTo = Math.Max(0, to); break;
                case "language":
                    settings.Language = value.ToUpperInvariant(); break;
                case "gamedir":
                    settings.GameDir = value.Length == 0 ? null : value; break;
                case "transferanymod" when bool.TryParse(value, out var any):
                    settings.TransferAnyMod = any; break;
                case "autoattach" when bool.TryParse(value, out var auto):
                    settings.AutoAttach = auto; break;
                case "databasefile":
                    settings.DatabaseFile = value.Length == 0 ? null : value; break;
                default:
                    Console.Error.WriteLine($"error: unknown setting '{args[1]}', or bad value.");
                    Console.Error.WriteLine("       keys: stashToLootFrom, stashToDepositTo, language, gameDir,");
                    Console.Error.WriteLine("             transferAnyMod, autoAttach, databaseFile");
                    return 1;
            }

            settings.Save();
            Console.WriteLine($"Set {args[1]} = {value}");

            if (key is "language") {
                Console.WriteLine("Run 'iagd parse' for this to take effect — names come from the game database.");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Settings ({LinuxPaths.SettingsFile})");
        Console.WriteLine($"  stashToLootFrom   {settings.StashToLootFrom}{(settings.StashToLootFrom == 0 ? "  (last tab)" : "")}");
        Console.WriteLine($"  stashToDepositTo  {settings.StashToDepositTo}{(settings.StashToDepositTo == 0 ? "  (last tab)" : "")}");
        Console.WriteLine($"  language          {settings.Language}");
        Console.WriteLine($"  gameDir           {settings.GameDir ?? "(auto-discovered)"}");
        Console.WriteLine($"  transferAnyMod    {settings.TransferAnyMod}");
        Console.WriteLine($"  autoAttach        {settings.AutoAttach}");
        Console.WriteLine($"  databaseFile      {settings.DatabaseFile ?? "(default location)"}");
        Console.WriteLine($"  in use now        {LinuxPaths.DatabaseFile}");

        // What the hook will read, which is the thing that actually governs behaviour.
        try {
            var (_, bridge) = Resolve();
            var applied = BridgeSettings.Apply(bridge, settings,
                                               GameDataStore.HasParsedItems(LinuxPaths.DatabaseFile));
            var hook = BridgeSettings.Read(bridge);

            Console.WriteLine();
            Console.WriteLine($"Hook ({bridge.SettingsFile})");
            if (applied.Error is not null) {
                Console.WriteLine($"  ERROR   {applied.Error}");
                return 1;
            }
            if (hook is null) {
                Console.WriteLine("  not readable");
                return 1;
            }
            Console.WriteLine($"  wine mode         {(hook.Value.WineMode ? "enabled" : "DISABLED — the hook will not capture loot")}");
            Console.WriteLine($"  stashToLootFrom   {hook.Value.LootFrom}");
            Console.WriteLine($"  stashToDepositTo  {hook.Value.DepositTo}");
            Console.WriteLine($"  grim dawn parsed  {(hook.Value.Parsed ? "yes" : "NO — the hook will reject every item")}");
        }
        catch (DirectoryNotFoundException) {
            Console.WriteLine();
            Console.WriteLine("Hook: no Proton prefix found; settings saved but not applied yet.");
        }

        return 0;
    }

    /// <summary>
    /// Start time of the running game. Under Wine the process name is the loader, so the
    /// executable only appears in the command line — hence scanning /proc.
    /// </summary>
    private static DateTime? GameStartTime() {
        DateTime? earliest = null;
        foreach (var dir in Directory.EnumerateDirectories("/proc")) {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try {
                if (!File.ReadAllText(Path.Combine(dir, "cmdline"))
                         .Contains("Grim Dawn.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var start = System.Diagnostics.Process.GetProcessById(pid).StartTime;
                if (earliest is null || start < earliest) earliest = start;
            }
            catch { /* exited mid-scan */ }
        }
        return earliest;
    }

    private static string? ArgValue(string[] args, string flag) {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Unknown(string command) {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Help();
        return 1;
    }

    private static int Help() {
        Console.WriteLine("""
            iagd — Item Assistant for Grim Dawn on Linux

              iagd status                 environment, hook state, pending loot
              iagd import                 import loot the hook has captured
              iagd watch [--interval 2]   keep importing as you play
              iagd parse                  read Grim Dawn's item database (once, and after patches)
              iagd stats                  roll real stat values for the collection
              iagd list [--name <text>]   show the collection
              iagd transfer <id>          send an item back into the game (irreversible)
              iagd settings [key value]   show or change settings

            Any command takes --database <path> to work on a different collection.
              iagd backup [--restore f]   copy the collection, or put a copy back
              iagd export <file> [--hc]   write a GD Stash file others can read
              iagd import-file <file>     read a GD Stash file into the collection
              iagd merge <db> [--dry-run]  add another collection, skipping exact duplicates
              iagd stash [--import]       read the game's shared stash, optionally importing
              iagd attach                 attach the hook to a running Grim Dawn, once
              iagd replica                ask the running game to render missing tooltips
              iagd install-desktop        add the app to the desktop menu, with its icon
            """);
        return 0;
    }
}
