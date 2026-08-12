using Microsoft.Data.Sqlite;
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
public sealed partial class ReplicaService {
    private readonly PrefixBridge _bridge;

    /// <summary>
    /// How many requests to have outstanding at once.
    ///
    /// The hook processes these on the render thread's schedule and upstream throttles itself
    /// once its queue passes 20. A cap also bounds the damage if something goes wrong: a bug
    /// that requested the whole collection every tick would otherwise write thousands of files
    /// into someone's Wine prefix.
    /// </summary>
    /// <summary>
    /// How many requests may be waiting for the game at once. Deliberately small.
    ///
    /// The hook answers these on the **render thread**: OnDemandSeedInfo's hook into
    /// Engine::Render drains up to a hundred queued requests per frame, and answering one means
    /// constructing a real game item and reading its tooltip. Queue a few hundred and the game
    /// stutters for as long as it takes to work through them — measured here after this cap was
    /// briefly raised to 250, which is why it is written down.
    ///
    /// Upstream has no cap because its situation is different: its items arrive by looting,
    /// which captures the tooltip at the same moment, so there is rarely a backlog. A merged
    /// collection is thousands of items at once, and the right answer there is to fill them in
    /// slowly in the background rather than to spend the player's frame budget on it — the
    /// cards are readable meanwhile, since ItemStatText describes them from the game database.
    /// </summary>
    public const int MaxInFlight = 20;

    /// <summary>
    /// Items already asked about during this run.
    ///
    /// **This is what stops an infinite loop, and it is upstream's guard too** — its
    /// ItemReplicaRequesterService keeps a ReplicaCache for exactly this reason, with the
    /// comment "Don't ask for the same item twice ... this would infinitely loop".
    ///
    /// The loop is not hypothetical. The hook deletes each request file the moment it queues it,
    /// so a request stops being visible on disk long before an answer exists. Without this set,
    /// the next pass two seconds later sees the same items still lacking a tooltip and asks
    /// again — and the game rebuilds the same twenty items on its render thread, over and over,
    /// for as long as it runs. Observed in the hook's own log: ids 7463-7482 queued at 18:40:36
    /// and the identical twenty queued again at 18:40:38.
    ///
    /// An item the game never answers for therefore stays unanswered until the next run, which
    /// is the trade upstream makes as well.
    /// </summary>
    private readonly HashSet<long> _asked = [];

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

    /// <summary>
    /// Forgets what has been asked, so the unanswered can be asked again.
    ///
    /// Upstream resets the same cache after parsing the game database, which is the point at
    /// which an item the game could not previously describe might become describable.
    /// </summary>
    public void Reset() => _asked.Clear();

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

        // Ask for more than the budget so that items already asked about can be skipped without
        // starving the ones behind them. The multiplier is what makes progress possible at all
        // once the first few hundred have been asked and not answered.
        var candidates = store.ItemsMissingReplica((budget + _asked.Count + outstanding.Count) * 2)
            .Where(item => !outstanding.Contains(item.Id) && !_asked.Contains(item.Id))
            .Take(budget);

        var written = 0;
        foreach (var item in candidates) {
            // Upstream's one rejection in its own serialiser: a record this long cannot be
            // reproduced, and sending it anyway wastes a round trip.
            if (TooLong(item)) {
                _asked.Add(item.Id);
                continue;
            }

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
            _asked.Add(item.Id);
            written++;
        }
        return written;
    }

    private static bool TooLong(ReplicaRequestItem item) =>
        new[] { item.BaseRecord, item.PrefixRecord, item.SuffixRecord, item.ModifierRecord,
                item.MateriaRecord, item.EnchantmentRecord, item.TransmuteRecord,
                item.AscendantAffixNameRecord, item.AscendantAffix2hNameRecord }
            .Any(record => record?.Length > 255);

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
                        var raw = stat.TryGetProperty("text", out var t) ? t.GetString() : null;
                        var type = stat.TryGetProperty("type", out var ty) ? ty.GetInt32() : 0;

                        // "Tag not found" is the game admitting it has no text for a record,
                        // which upstream drops rather than storing.
                        if (raw is null || raw.TrimStart().StartsWith("Tag not found", StringComparison.Ordinal)) {
                            continue;
                        }

                        var text = Normalise(raw);
                        if (text.Length > 0) stats.Add(new LootStat(type, text));
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

    /// <summary>
    /// Applies <see cref="Normalise"/> to lines captured before this port did so.
    ///
    /// Idempotent rather than one-shot. The first version of this recorded "done" in a settings
    /// row, which was wrong in a way worth remembering: an older build of the port left running
    /// kept storing raw lines *after* the flag was set, and they were then never cleaned. The
    /// query below finds exactly the rows that still need it, so a database heals whatever
    /// wrote it and matches nothing once clean.
    /// </summary>
    public static int NormaliseStoredRows(SqliteConnection connection) {
        var updates = new List<(long Id, string Text)>();

        using (var read = connection.CreateCommand()) {
            // A caret is a colour code; " [" or " (" before the end is the range annotation.
            read.CommandText = """
                SELECT Id, Text FROM ReplicaItemRow
                WHERE Text IS NOT NULL
                  AND (Text LIKE '%^%' OR Text LIKE '% [%]' OR Text LIKE '% (%)');
                """;
            try {
                using var reader = read.ExecuteReader();
                while (reader.Read()) {
                    var text = reader.GetString(1);
                    var normalised = Normalise(text);
                    if (!string.Equals(text, normalised, StringComparison.Ordinal)) {
                        updates.Add((reader.GetInt64(0), normalised));
                    }
                }
            }
            catch (SqliteException) { return 0; }   // table not created yet
        }

        // Retire the flag the one-shot version left behind, whether or not there is work to do.
        using (var clean = connection.CreateCommand()) {
            clean.CommandText = "DELETE FROM settings WHERE setting = 'iagd_linux_replica_normalised';";
            try { clean.ExecuteNonQuery(); } catch (SqliteException) { }
        }

        if (updates.Count == 0) return 0;

        using var transaction = connection.BeginTransaction();
        using (var update = connection.CreateCommand()) {
            update.Transaction = transaction;
            update.CommandText = "UPDATE ReplicaItemRow SET Text = $text WHERE Id = $id;";
            var textParam = update.Parameters.Add("$text", SqliteType.Text);
            var idParam = update.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (id, text) in updates) {
                textParam.Value = text;
                idParam.Value = id;
                update.ExecuteNonQuery();
            }
        }
        transaction.Commit();

        return updates.Count;
    }

    /// <summary>
    /// The two edits upstream makes to every captured line before storing it
    /// (<c>ItemReplicaParser</c>).
    ///
    /// The hook asks the game for its *detailed* tooltip — the one the player sees holding Ctrl
    /// — because that is the view carrying every number. That view annotates each stat with the
    /// range it could have rolled in: "+21% Physical Damage [16-24]". Useful to the game, noise
    /// on a card, and upstream strips it, along with the colour codes.
    ///
    /// The regexes are upstream's, character for character, including the fact that the second
    /// one takes any trailing bracketed group rather than only a numeric range.
    /// </summary>
    internal static string Normalise(string text) {
        var stripped = ColourCodes().Replace(text.Trim(), "");
        return TrailingBracket().Replace(stripped, "");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(\^.?)")]
    private static partial System.Text.RegularExpressions.Regex ColourCodes();

    [System.Text.RegularExpressions.GeneratedRegex(@" (\[|\().+(\]|\))$")]
    private static partial System.Text.RegularExpressions.Regex TrailingBracket();

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
