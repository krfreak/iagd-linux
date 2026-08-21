using System.Text.Json;
using System.Text.Json.Nodes;

namespace IAGrim.Platform;

/// <summary>
/// The four keys the hook DLL reads out of <c>settings.json</c> in the bridge directory.
///
/// **This is not optional configuration — it is what makes the hook work at all.** The DLL only
/// switches to file-based IPC when <c>persistent.isRunningInWine</c> is true; without it, it
/// falls back to the shared-memory path that does not work under Proton, and the failure is
/// silent. Loot is simply never written.
///
/// From <c>HookDll/Hook/SettingsReader.cpp</c>:
///
/// | Key | Purpose |
/// |---|---|
/// | <c>persistent.isRunningInWine</c> | Must be true; switches the DLL to file IPC |
/// | <c>local.stashToLootFrom</c> | Stash tab index to take from, 0 = last |
/// | <c>local.stashToDepositTo</c> | Stash tab index to place into, 0 = last |
/// | <c>local.isGrimDawnParsed</c> | Must be true, or the hook rejects every item |
///
/// That last one is as load-bearing as Wine mode and fails just as quietly. Missing means false
/// (<c>SettingsReader.cpp</c> defaults that way), and the hook's first check on every item is
/// <c>InventorySack_AddItem::IsRelevant</c>, which pops "Item not looted / Grim Dawn not parsed"
/// over the game and drops the item on the floor. A prefix that has also run the Windows tool
/// already has the key, which is exactly why this was easy to miss.
///
/// Everything else in the file is left exactly as found. That matters: on a machine that has
/// run the Windows tool, this file holds its cloud credentials and window geometry, and
/// rewriting it wholesale would log someone out of a service this port does not even implement.
/// </summary>
public static class BridgeSettings {
    /// <summary>What <see cref="Apply"/> did, so the caller can report it meaningfully.</summary>
    public sealed record Result(bool Changed, bool Created, string Path, string? Error) {
        public static Result Failed(string path, string error) => new(false, false, path, error);
    }

    /// <summary>
    /// Merges the hook's keys into the bridge settings file, creating it if absent.
    ///
    /// Safe to call on every startup: it rewrites only when a value actually differs, so it does
    /// not churn a file the Windows tool may also be watching.
    /// </summary>
    /// <param name="isGrimDawnParsed">
    /// Whether this client has read Grim Dawn's data. Call again when that becomes true — the
    /// hook re-reads the key for every item it rejects (<c>InventorySack_AddItem.cpp</c>), so
    /// looting starts working without restarting the game.
    /// </param>
    public static Result Apply(PrefixBridge bridge, AppSettings settings, bool isGrimDawnParsed) {
        var path = bridge.SettingsFile;

        JsonObject root;
        var created = false;
        try {
            if (File.Exists(path)) {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            else {
                root = new JsonObject();
                created = true;
            }
        }
        catch (JsonException) {
            // A malformed file is not something to silently replace: it may be a real install's
            // settings with credentials in it, and the user would rather know.
            return Result.Failed(path, "the existing settings.json is not valid JSON; " +
                                       "move it aside and restart to regenerate it");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return Result.Failed(path, ex.Message);
        }

        var persistent = root["persistent"] as JsonObject;
        if (persistent is null) root["persistent"] = persistent = new JsonObject();

        var local = root["local"] as JsonObject;
        if (local is null) root["local"] = local = new JsonObject();

        var changed = false;
        changed |= SetIfDifferent(persistent, "isRunningInWine", true);
        changed |= SetIfDifferent(local, "stashToLootFrom", settings.StashToLootFrom);
        changed |= SetIfDifferent(local, "stashToDepositTo", settings.StashToDepositTo);

        // Raised to true, never lowered. False is what the hook already assumes when the key is
        // absent, so writing it buys nothing — while a prefix that has also run the Windows tool
        // holds *its* answer here, and stamping false over that would stop the hook looting for
        // a client whose database is perfectly well parsed. The startup order makes that a live
        // risk rather than a theoretical one: the first Apply of a session runs before this
        // client's own parse has had a chance to happen.
        if (isGrimDawnParsed) changed |= SetIfDifferent(local, "isGrimDawnParsed", true);

        if (!changed) return new Result(false, false, path, null);

        try {
            // The bridge directory is the hook DLL's to create, and on a prefix where the hook
            // has never run it does not exist yet — nor after someone deletes the EvilSoft
            // folder to start clean, which is the first thing anyone tries. Every other path on
            // PrefixBridge creates itself on the way out; this one did not, so the write failed
            // with "Could not find a part of the path ...\settings.json.tmp" and the hook was
            // left unconfigured. It repaired itself only by accident, once some other call in
            // the same session created the directory underneath it.
            Directory.CreateDirectory(bridge.Root);

            // Temp-and-rename: the game may be running with the hook attached, and a truncated
            // read of this file is a hook that silently stops using file IPC.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return Result.Failed(path, ex.Message);
        }

        return new Result(true, created, path, null);
    }

    /// <summary>
    /// Reads back what the hook will actually see, for diagnostics. Deliberately narrow: the
    /// rest of this file is none of our business and some of it is credentials.
    /// </summary>
    public static (bool WineMode, int LootFrom, int DepositTo, bool Parsed)? Read(PrefixBridge bridge) {
        try {
            if (!File.Exists(bridge.SettingsFile)) return null;
            var root = JsonNode.Parse(File.ReadAllText(bridge.SettingsFile)) as JsonObject;
            if (root is null) return null;

            return (
                root["persistent"]?["isRunningInWine"]?.GetValue<bool>() ?? false,
                root["local"]?["stashToLootFrom"]?.GetValue<int>() ?? 0,
                root["local"]?["stashToDepositTo"]?.GetValue<int>() ?? 0,
                root["local"]?["isGrimDawnParsed"]?.GetValue<bool>() ?? false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException
                                          or FormatException) {
            return null;
        }
    }

    private static bool SetIfDifferent(JsonObject target, string key, bool value) {
        if (target[key] is JsonValue existing && existing.TryGetValue<bool>(out var current)
            && current == value) {
            return false;
        }
        target[key] = value;
        return true;
    }

    private static bool SetIfDifferent(JsonObject target, string key, int value) {
        if (target[key] is JsonValue existing && existing.TryGetValue<int>(out var current)
            && current == value) {
            return false;
        }
        target[key] = value;
        return true;
    }
}
