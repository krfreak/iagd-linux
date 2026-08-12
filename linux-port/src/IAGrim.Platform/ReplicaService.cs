using System.Text.Json;

namespace IAGrim.Platform;

/// <summary>
/// Asks the running game to render the tooltip for items that arrived without one.
///
/// Items captured by the hook come with their tooltip already — the game drew it as the item
/// was looted. Items that arrive from a *file* do not: the transfer stash and the GD Stash
/// interchange format both record what an item **is** (records and seeds), not how the game
/// renders it. Those items get a base name from ItemTemplate and numbers from the seed engine,
/// but none of Grim Dawn's own colour-coded text.
///
/// The hook can fill that in, and already does — <c>OnDemandSeedInfo</c> is ported and running.
/// Until now nothing asked it to. This is the missing half.
///
/// **The protocol**, from the hook's own source rather than from upstream's client:
///
/// - Request: a one-line semicolon-separated CSV in <c>replica/from_ia[/mod]</c>. Fifteen
///   fields (twelve for pre-Asterkarn). The hook deletes the file as it reads it.
/// - Response: JSON in <c>replica/to_ia</c>, carrying <c>playerItemId</c> and a <c>stats</c>
///   array of <c>{text, type}</c> — which is exactly a ReplicaItemRow.
///
/// The hook only reads the folder for the mod currently being played, so a request for a mod
/// the player is not in simply waits.
/// </summary>
public sealed class ReplicaService {
    private readonly PrefixBridge _bridge;

    /// <summary>
    /// How many requests to have outstanding at once.
    ///
    /// The hook processes these on the render thread's schedule and upstream throttles itself
    /// once its queue passes 20. A cap also bounds the damage if something goes wrong: a bug
    /// that requested the whole collection every tick would otherwise write thousands of files
    /// into someone's Wine prefix.
    /// </summary>
    public const int MaxInFlight = 20;

    public ReplicaService(PrefixBridge bridge) {
        _bridge = bridge;
    }

    /// <summary>
    /// Item ids with a request already waiting for the game to pick up.
    ///
    /// **The request file is named after the item id**, which is what makes this work across
    /// processes and restarts: the filesystem is the record of what has been asked. The hook
    /// does not care about the name — it reads every <c>*.csv</c> in the folder and deletes it.
    ///
    /// Without this the same item is re-requested on every pass, because "has no tooltip yet"
    /// stays true until the game answers. At a two-second poll that is a new file every two
    /// seconds per item, which is precisely the behaviour not to have.
    /// </summary>
    public HashSet<long> Outstanding() {
        var ids = new HashSet<long>();
        foreach (var directory in RequestDirectories()) {
            foreach (var file in Directory.EnumerateFiles(directory, "*.csv")) {
                if (long.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>Requests currently waiting for the game to pick them up.</summary>
    public int InFlight() => Outstanding().Count;

    private IEnumerable<string> RequestDirectories() {
        var root = _bridge.StatRequestToGame;
        yield return root;
        foreach (var mod in Directory.EnumerateDirectories(root)) yield return mod;
    }

    /// <summary>
    /// Writes requests for items that have no tooltip yet, up to the in-flight cap.
    /// Returns how many were written.
    /// </summary>
    public int RequestMissing(LootStore store) {
        var outstanding = Outstanding();
        var budget = MaxInFlight - outstanding.Count;
        if (budget <= 0) return 0;

        // Ask for more than the budget so that items already waiting can be skipped without
        // starving the ones behind them.
        var candidates = store.ItemsMissingReplica(budget + outstanding.Count)
            .Where(item => !outstanding.Contains(item.Id))
            .Take(budget);

        var written = 0;
        foreach (var item in candidates) {
            var folder = string.IsNullOrEmpty(item.Mod)
                ? _bridge.StatRequestToGame
                : Ensure(Path.Combine(_bridge.StatRequestToGame, item.Mod));

            // Same temp-then-rename discipline as the transfer queue: the hook scans this
            // directory on a timer and a half-written line parses as a different item, not as
            // an error.
            var path = Path.Combine(folder, $"{item.Id}.csv");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, SerializeRequest(item) + "\n",
                              new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path);
            written++;
        }
        return written;
    }

    /// <summary>
    /// The request line the hook parses in <c>DeserializeReplicaCsv</c>. Fifteen fields, in this
    /// order — a field out of place produces a different item rather than a parse error, so the
    /// order is transcribed from the hook rather than remembered.
    /// </summary>
    internal static string SerializeRequest(ReplicaRequestItem item) => string.Join(';', [
        "1",                              // type: 1 = player item, 2 = buddy item
        item.Id.ToString(),               // echoed back as playerItemId
        Unsigned(item.Seed),
        Unsigned(item.RelicSeed),
        Unsigned(item.EnchantmentSeed),
        Unsigned(item.RerollsUsed),
        item.BaseRecord,
        item.PrefixRecord ?? "",
        item.SuffixRecord ?? "",
        item.ModifierRecord ?? "",
        item.MateriaRecord ?? "",
        item.EnchantmentRecord ?? "",
        item.TransmuteRecord ?? "",
        item.AscendantAffixNameRecord ?? "",
        item.AscendantAffix2hNameRecord ?? "",
    ]);

    /// <summary>
    /// Seeds are stored signed and read by the hook with <c>stoul</c>, which rejects a leading
    /// minus. The bits are what matter, so they are reinterpreted rather than clamped.
    /// </summary>
    private static string Unsigned(long value) => unchecked((uint)value).ToString();

    /// <summary>
    /// Reads whatever the game has answered, attaching the tooltips to their items.
    /// Returns how many items were completed.
    /// </summary>
    public int CollectResults(LootStore store) {
        var directory = _bridge.StatResultFromGame;
        if (!Directory.Exists(directory)) return 0;

        var completed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").ToList()) {
            try {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;

                if (!root.TryGetProperty("playerItemId", out var idElement)) {
                    File.Delete(file);   // a buddy-item reply; nothing here owns those yet
                    continue;
                }
                var id = idElement.GetInt64();

                var stats = new List<LootStat>();
                if (root.TryGetProperty("stats", out var statsElement)) {
                    foreach (var stat in statsElement.EnumerateArray()) {
                        var text = stat.TryGetProperty("text", out var t) ? t.GetString() : null;
                        var type = stat.TryGetProperty("type", out var ty) ? ty.GetInt32() : 0;
                        if (!string.IsNullOrEmpty(text)) stats.Add(new LootStat(type, text));
                    }
                }

                // An empty reply means the game could not build the item — usually a record from
                // a mod that is not loaded. Consuming the file anyway stops it being retried
                // forever; the item keeps its template name.
                if (stats.Count > 0 && store.AttachReplica(id, stats)) completed++;
                File.Delete(file);
            }
            catch (Exception ex) when (ex is JsonException or IOException) {
                // A half-written file: the hook renames into place, so this should not happen,
                // but leaving it for the next pass is safer than deleting evidence.
            }
        }
        return completed;
    }

    private static string Ensure(string path) {
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>An item needing a tooltip, in the fields the hook's request line carries.</summary>
public sealed record ReplicaRequestItem(
    long Id, string Mod, string BaseRecord, string? PrefixRecord, string? SuffixRecord,
    string? ModifierRecord, string? MateriaRecord, string? EnchantmentRecord,
    string? TransmuteRecord, string? AscendantAffixNameRecord, string? AscendantAffix2hNameRecord,
    long Seed, long RelicSeed, long EnchantmentSeed, long RerollsUsed);
