using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iagd.Probe;

/// <summary>
/// Phase 0 probe: does the hook DLL's Wine file-based IPC actually work under Proton?
///
/// Observes only. Never launches the game and never injects — that is the wrapper
/// script's job. Reads the shared directory and reports what arrives.
/// </summary>
internal static class Program {
    private static int Main(string[] args) {
        if (args.Contains("--help") || args.Contains("-h")) {
            Console.WriteLine("""
                iagd-probe — Phase 0 bridge watcher

                  iagd-probe [--setup] [--bridge <path>] [--record <path>]

                  --setup           Patch settings.json to enable the DLL's Wine mode, then exit.
                                    Run this once before launching the game.
                  --bridge <path>   Override the auto-discovered bridge directory.
                  --record <path>   Baseline JSONL output (default: ./probe-baseline.jsonl)
                """);
            return 0;
        }

        string? bridge;
        SteamPaths? paths = null;

        var bridgeOverride = ValueOf(args, "--bridge");
        if (bridgeOverride is not null) {
            bridge = bridgeOverride;
            Console.WriteLine($"Bridge directory overridden: {bridge}");
        }
        else {
            try {
                paths = SteamPaths.Discover();
            }
            catch (Exception ex) {
                Console.Error.WriteLine($"Discovery failed: {ex.Message}");
                return 1;
            }
            PrintEnvironment(paths);
            bridge = paths.BridgeDir;
        }

        if (bridge is null) {
            Console.Error.WriteLine("""

                Could not locate the Grim Dawn Proton prefix.
                Grim Dawn must have been launched through Proton at least once so Steam
                creates compatdata/219990. Or pass --bridge <path> explicitly.
                """);
            return 1;
        }

        if (args.Contains("--setup")) {
            return Setup(bridge);
        }

        if (!Directory.Exists(bridge)) {
            Console.Error.WriteLine($"""

                Bridge directory does not exist yet:
                  {bridge}

                Run with --setup first, which creates it and enables the DLL's Wine mode.
                """);
            return 1;
        }

        WarnIfWineModeDisabled(bridge);

        var record = ValueOf(args, "--record") ?? "probe-baseline.jsonl";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        new BridgeWatcher(bridge, Path.GetFullPath(record)).RunAsync(cts.Token).GetAwaiter().GetResult();
        return 0;
    }

    private static void PrintEnvironment(SteamPaths p) {
        Console.WriteLine("Resolved environment");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"  Steam root   {p.SteamRoot}");
        Console.WriteLine($"  Libraries    {string.Join(Environment.NewLine + "               ", p.Libraries)}");
        Console.WriteLine($"  Game dir     {p.GameDir ?? "NOT FOUND"}");
        Console.WriteLine($"  Prefix       {p.PrefixDir ?? "NOT FOUND"}");
        Console.WriteLine($"  Save path    {p.SavePath ?? "NOT FOUND"}");
        Console.WriteLine($"               via {p.SaveSource ?? "-"}");
        Console.WriteLine($"  Bridge       {p.BridgeDir ?? "NOT FOUND"}");
        Console.WriteLine();
    }

    /// <summary>
    /// The DLL only switches to file-based IPC when persistent.isRunningInWine is true
    /// (HookDll/Hook/SettingsReader.cpp:91, read at attach in dllmain.cpp:429). Upstream's
    /// host sets this from its own Wine detection, which a native Linux host cannot do —
    /// so we assert it directly. Existing keys are preserved.
    /// </summary>
    private static int Setup(string bridge) {
        Directory.CreateDirectory(bridge);
        Directory.CreateDirectory(Path.Combine(bridge, "linuxhack"));
        Directory.CreateDirectory(Path.Combine(bridge, "itemqueue", "ingoing"));
        Directory.CreateDirectory(Path.Combine(bridge, "replica", "to_ia"));
        Directory.CreateDirectory(Path.Combine(bridge, "replica", "from_ia"));

        var settingsPath = Path.Combine(bridge, "settings.json");

        JsonObject root;
        if (File.Exists(settingsPath)) {
            var backup = settingsPath + ".probe-backup";
            File.Copy(settingsPath, backup, overwrite: true);
            Console.WriteLine($"Existing settings.json backed up to {Path.GetFileName(backup)}");
            try {
                root = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject() ?? [];
            }
            catch (JsonException) {
                Console.WriteLine("Existing settings.json is not valid JSON; starting a fresh one.");
                root = [];
            }
        }
        else {
            root = [];
        }

        if (root["persistent"] is not JsonObject persistent) {
            persistent = [];
            root["persistent"] = persistent;
        }
        if (root["local"] is not JsonObject local) {
            local = [];
            root["local"] = local;
        }

        persistent["isRunningInWine"] = true;
        local["stashToLootFrom"] ??= 0;     // 0 = last stash tab
        local["stashToDepositTo"] ??= 0;

        File.WriteAllText(settingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"""

            Wine mode enabled in {settingsPath}
              persistent.isRunningInWine = true
              local.stashToLootFrom      = {local["stashToLootFrom"]}
              local.stashToDepositTo     = {local["stashToDepositTo"]}

            Next: launch Grim Dawn with the injection wrapper, then run the probe with no
            arguments to watch the bridge.
            """);
        return 0;
    }

    private static void WarnIfWineModeDisabled(string bridge) {
        var settingsPath = Path.Combine(bridge, "settings.json");
        if (!File.Exists(settingsPath)) {
            Console.WriteLine("[warn] No settings.json in the bridge directory — run --setup first.");
            Console.WriteLine();
            return;
        }

        try {
            var root = JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject();
            var wine = root?["persistent"]?["isRunningInWine"]?.GetValue<bool>() ?? false;
            if (!wine) {
                Console.WriteLine("[warn] persistent.isRunningInWine is not true — the DLL will use");
                Console.WriteLine("       WM_COPYDATA instead of files and this probe will see nothing.");
                Console.WriteLine("       Run with --setup to fix.");
                Console.WriteLine();
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"[warn] Could not read settings.json: {ex.Message}");
            Console.WriteLine();
        }
    }

    private static string? ValueOf(string[] args, string flag) {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
