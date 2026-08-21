using IAGrim.Platform;
using Microsoft.Data.Sqlite;

namespace IAGrim.DbDoctor;

/// <summary>
/// Every check the doctor runs, over a database it opens read-only and never writes to.
///
/// The checks fall into two halves. One asks whether an item can still be read — whether the
/// record it names exists in the parsed game data, whether it has a name, whether it has the
/// tooltip rows the UI and the transfer path expect. The other asks whether online sync can
/// still tell this collection apart from the server's copy: cloud ids, the tombstone table, and
/// the duplicates that appear when a deletion never reaches the server.
///
/// The second half is the one worth explaining. A deletion here writes a row into
/// <c>deletedplayeritem_v3</c>, and that row is the only record that the item is gone. Upstream
/// clears the whole table after every accepted batch (BackupService.SyncDeletions), so a batch
/// that fails, or a logout mid-pass, returns with every unsent tombstone already erased. The
/// server never hears about those deletions, hands the items back on the next download, and they
/// arrive under the server's cloud id while the local row (if it survived) keeps its own. What
/// that leaves behind is measurable: items identical by upstream's own equality key, sitting
/// under two or more different cloud ids, with nothing pending in the tombstone table.
/// </summary>
public sealed class Diagnosis : IDisposable {
    private readonly SqliteConnection _connection;
    private readonly string _path;
    private readonly int _sampleLimit;
    private readonly HashSet<string> _tables;

    public Diagnosis(string path, int sampleLimit) {
        _path = path;
        _sampleLimit = sampleLimit;

        // Read-only for real, not by convention: a diagnostic that repairs a WAL or applies a
        // migration has changed the evidence before reporting on it. This also means the tool is
        // safe to point at the live collection while the app is running.
        //
        // The database file is left byte-identical. SQLite may still create the usual `-wal` and
        // `-shm` sidecars alongside it — a WAL database needs the shared-memory index even to be
        // read — so "does not write" means the collection, not the directory.
        var connectionString = new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        _tables = new HashSet<string>(
            Strings("SELECT name FROM sqlite_master WHERE type = 'table';"),
            StringComparer.OrdinalIgnoreCase);
    }

    public Report Run() {
        var sections = new List<Section> {
            DatabaseSection(),
            CollectionSection(),
            ReadabilitySection(),
            ModTaggingSection(),
            SyncSection(),
            DuplicateSection(),
            OrphanSection(),
            TimestampSection(),
        };

        var size = new FileInfo(_path).Length;
        return new Report(_path, size, sections, Verdict(sections));
    }

    // ---- the sections -------------------------------------------------------------------

    private Section DatabaseSection() {
        var facts = new List<(string, string)>();
        var findings = new List<Finding>();

        var integrity = Strings("PRAGMA quick_check;").FirstOrDefault() ?? "unknown";
        facts.Add(("integrity", integrity));
        facts.Add(("pages", $"{Number("PRAGMA page_count;")} ({Number("PRAGMA freelist_count;")} free)"));

        if (!string.Equals(integrity, "ok", StringComparison.Ordinal)) {
            findings.Add(new Finding(Severity.Problem, "database-corrupt",
                "SQLite reports the file as damaged",
                Meaning: "Nothing below can be trusted while this is true. Restore the most "
                       + "recent backup before reading anything else in this report.",
                Detail: Strings("PRAGMA quick_check;").Take(_sampleLimit).ToList()));
        }

        var missing = Schema.TableNames
            .Where(table => !_tables.Contains(table))
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToList();

        if (missing.Count > 0) {
            findings.Add(new Finding(Severity.Problem, "schema-incomplete",
                $"{missing.Count} table(s) the tool expects are not here", missing.Count,
                "A collection missing tables was written by something other than IAGD, or a "
              + "migration did not finish. The app creates them on next start, but a table that "
              + "should have held data and does not is data already lost.",
                missing));
        }

        return new Section("Database", facts, findings);
    }

    private Section CollectionSection() {
        var facts = new List<(string, string)> {
            ("items", $"{Count("PlayerItem")}"),
            ("item records", $"{Count("PlayerItemRecord")}"),
            ("tooltips", $"{Count("ReplicaItem2")} items, {Count("ReplicaItemRow")} rows"),
            ("computed stats", $"{Count("ComputedItemStat")}"),
            ("known records", $"{Count("DatabaseItem_v2")} in the parsed game data"),
            ("buddy items", $"{Count("buddyitems_v6")}"),
        };

        if (Has("PlayerItem")) {
            // Milliseconds, which is what upstream writes; the seconds-scale rows the timestamp
            // check finds would render as 1970 here, so they are excluded from the range.
            var range = Strings("""
                SELECT MIN(created_at), MAX(created_at) FROM PlayerItem
                WHERE created_at > 100000000000;
                """, columns: 2).FirstOrDefault();

            if (range is not null && !range.StartsWith('\t')) {
                var parts = range.Split('\t');
                facts.Add(("looted", $"{Moment(parts[0])} .. {Moment(parts[1])}"));
            }
        }

        return new Section("Collection", facts, []);
    }

    /// <summary>
    /// Can each item still be read? An item whose record is not in the parsed game data has no
    /// name, no stats, no icon and no tooltip, and the game will not accept it back either — it
    /// is a row of numbers referring to something that, as far as this installation is
    /// concerned, does not exist.
    /// </summary>
    private Section ReadabilitySection() {
        if (!Has("PlayerItem") || !Has("DatabaseItem_v2")) return new Section("Readability", [], []);

        var findings = new List<Finding>();

        var unknown = Number($"""
            SELECT COUNT(*) FROM PlayerItem p
            WHERE NOT EXISTS (SELECT 1 FROM DatabaseItem_v2 d WHERE d.baserecord = p.baserecord);
            """);

        if (unknown > 0) {
            var byRoot = Strings($"""
                SELECT {Sql.RecordRoot} || '/' || '…', COUNT(*) FROM PlayerItem p
                WHERE NOT EXISTS (SELECT 1 FROM DatabaseItem_v2 d WHERE d.baserecord = p.baserecord)
                GROUP BY 1 ORDER BY 2 DESC LIMIT {_sampleLimit};
                """, columns: 2).Select(Pair).ToList();

            findings.Add(new Finding(Severity.Problem, "unknown-baserecord",
                $"{unknown} item(s) name a record the parsed game data does not have", unknown,
                "These are the unreadable ones. Either the game data was parsed without the mod "
              + "that defines these records, or it was parsed against a different game version. "
              + "Grim Dawn will not take them back, and the UI cannot draw them.",
                byRoot));
        }

        var nameless = Number("SELECT COUNT(*) FROM PlayerItem WHERE Name IS NULL OR TRIM(Name) = '';");
        if (nameless > 0) {
            findings.Add(new Finding(Severity.Problem, "nameless-items",
                $"{nameless} item(s) have no name at all", nameless,
                "The name is written at loot time from the game data. A blank one means the "
              + "lookup failed then, and it is failing now for the same reason.",
                Strings($"""
                    SELECT baserecord, Id FROM PlayerItem
                    WHERE Name IS NULL OR TRIM(Name) = '' LIMIT {_sampleLimit};
                    """, columns: 2).Select(Pair).ToList()));
        }

        var unrated = Number("""
            SELECT COUNT(*) FROM PlayerItem
            WHERE Rarity IS NULL OR TRIM(Rarity) = '' OR Rarity = 'Unknown';
            """);

        if (unrated > 0) {
            findings.Add(new Finding(Severity.Note, "unknown-rarity",
                $"{unrated} item(s) have no rarity", unrated,
                "'Unknown' is what the parser stores when it could not classify the item. The "
              + "rarity filter cannot match these."));
        }

        // Not a fault on its own. ReplicaItem2 holds the tooltip the *game* drew, which only
        // items captured by the hook arrive with; anything that came from a file or down from
        // the cloud never had one, and the collection reads it through a LEFT OUTER JOIN
        // (CollectionService) and falls back to computed stats. Reported because it is worth
        // knowing how much of a collection is showing computed numbers rather than the game's.
        if (Has("ReplicaItem2")) {
            var tooltipless = Number("""
                SELECT COUNT(*) FROM PlayerItem p
                WHERE NOT EXISTS (SELECT 1 FROM ReplicaItem2 r WHERE r.playeritemid = p.Id);
                """);

            if (tooltipless > 0) {
                findings.Add(new Finding(Severity.Note, "missing-tooltip",
                    $"{tooltipless} item(s) show computed stats rather than the game's own tooltip",
                    tooltipless,
                    "Normal for items that arrived from a file or from online sync — only the "
                  + "hook captures a real tooltip, and the app asks the game to fill the rest in "
                  + "while it is running. Worth noting rather than fixing."));
            }
        }

        return new Section("Readability", [], findings);
    }

    /// <summary>
    /// <c>PlayerItem.Mod</c> holds the mod folder the item came from, and the empty string means
    /// the base game. Every lookup — name, template, icon, level requirement — is keyed on it,
    /// so a modded item stored with a blank Mod is looked up in the base game's data, where its
    /// record does not exist. That is a different failure from a missing parse and it is the one
    /// that survives re-parsing, because the row itself is wrong.
    /// </summary>
    private Section ModTaggingSection() {
        if (!Has("PlayerItem")) return new Section("Mod tagging", [], []);

        var facts = Strings($"""
            SELECT CASE WHEN Mod IS NULL OR Mod = '' THEN '(base game)' ELSE Mod END, COUNT(*)
            FROM PlayerItem GROUP BY 1 ORDER BY 2 DESC LIMIT {_sampleLimit};
            """, columns: 2)
            .Select(row => row.Split('\t'))
            .Select(parts => (parts[0], parts[1]))
            .ToList();

        var findings = new List<Finding>();

        var untagged = Number($"""
            SELECT COUNT(*) FROM PlayerItem
            WHERE (Mod IS NULL OR Mod = '') AND {Sql.RecordRoot} <> 'records';
            """);

        if (untagged > 0) {
            var byRoot = Strings($"""
                SELECT {Sql.RecordRoot}, COUNT(*) FROM PlayerItem
                WHERE (Mod IS NULL OR Mod = '') AND {Sql.RecordRoot} <> 'records'
                GROUP BY 1 ORDER BY 2 DESC LIMIT {_sampleLimit};
                """, columns: 2).Select(Pair).ToList();

            findings.Add(new Finding(Severity.Problem, "untagged-mod-items",
                $"{untagged} item(s) come from outside the base game but are tagged as base game",
                untagged,
                "Their records live under the roots listed below rather than 'records', but Mod "
              + "is blank, so every lookup for them goes to the base game's data and finds "
              + "nothing. Re-parsing will not fix these: the rows themselves say base game.",
                byRoot));
        }

        return new Section("Mod tagging", facts, findings);
    }

    private Section SyncSection() {
        if (!Has("PlayerItem")) return new Section("Online sync", [], []);

        var tombstones = Count("deletedplayeritem_v3");
        var facts = new List<(string, string)> {
            ("uploaded", $"{Number("SELECT COUNT(*) FROM PlayerItem WHERE cloud_hassync = 1;")}"),
            ("pending upload",
                $"{Number("SELECT COUNT(*) FROM PlayerItem WHERE cloud_hassync IS NULL OR cloud_hassync = 0;")}"),
            ("with a cloud id",
                $"{Number("SELECT COUNT(*) FROM PlayerItem WHERE cloudid IS NOT NULL AND cloudid <> '';")}"),
            ("tombstones", $"{tombstones} deletion(s) waiting to be sent"),
        };

        var findings = new List<Finding>();

        var shared = Number("""
            SELECT COUNT(*) FROM (
                SELECT cloudid FROM PlayerItem
                WHERE cloudid IS NOT NULL AND cloudid <> ''
                GROUP BY cloudid HAVING COUNT(*) > 1);
            """);

        if (shared > 0) {
            findings.Add(new Finding(Severity.Problem, "shared-cloud-id",
                $"{shared} cloud id(s) are claimed by more than one row", shared,
                "A cloud id is meant to identify one item. Two rows sharing one means a deletion "
              + "of either removes both, and an upload of either overwrites the other."));
        }

        if (Has("deletedplayeritem_v3")) {
            var contradictory = Number("""
                SELECT COUNT(*) FROM deletedplayeritem_v3 d
                WHERE EXISTS (SELECT 1 FROM PlayerItem p WHERE p.cloudid = d.id);
                """);

            if (contradictory > 0) {
                findings.Add(new Finding(Severity.Problem, "tombstone-for-live-item",
                    $"{contradictory} tombstone(s) name an item that is still in the collection",
                    contradictory,
                    "The item was deleted and then came back down before the deletion was sent. "
                  + "Whichever of the two wins is decided by timing, and the download skips items "
                  + "with a pending tombstone — so this item is stuck out of sync either way."));
            }

            if (tombstones > 0) {
                findings.Add(new Finding(Severity.Note, "tombstones-pending",
                    $"{tombstones} deletion(s) have not been sent to the server yet", tombstones,
                    "This is normal for a while: deletions go out on a cooldown, 10 seconds in "
                  + "dual-computer mode and 54 minutes otherwise, and only while signed in. It is "
                  + "a problem only if the count never falls."));
            }
        }

        return new Section("Online sync", facts, findings);
    }

    /// <summary>
    /// Duplicates, and the specific shape of duplicate that means a deletion was lost rather
    /// than an import run twice.
    /// </summary>
    private Section DuplicateSection() {
        if (!Has("PlayerItem")) return new Section("Duplicates", [], []);

        var totals = Strings($"""
            WITH groups AS (
                SELECT COUNT(*) AS rows, COUNT(DISTINCT cloudid) AS ids
                FROM PlayerItem GROUP BY {Sql.IdentityKey} HAVING COUNT(*) > 1)
            SELECT COUNT(*), IFNULL(SUM(rows), 0), IFNULL(SUM(rows), 0) - COUNT(*),
                   IFNULL(SUM(CASE WHEN ids > 1 THEN 1 ELSE 0 END), 0)
            FROM groups;
            """, columns: 4).First().Split('\t');

        var (groups, rows, redundant, multiId) =
            (long.Parse(totals[0]), long.Parse(totals[1]), long.Parse(totals[2]), long.Parse(totals[3]));

        var facts = new List<(string, string)> {
            ("duplicated items", $"{groups}"),
            ("rows they occupy", $"{rows} ({redundant} more than the items themselves)"),
        };

        var findings = new List<Finding>();

        if (groups > 0) {
            findings.Add(new Finding(Severity.Problem, "duplicate-items",
                $"{groups} item(s) are stored more than once, costing {redundant} extra row(s)",
                redundant,
                "Identical by the key the loot importer itself uses: base record, both affixes, "
              + "the socketed component, the modifier and the seed. The importer refuses an item "
              + "the collection already holds, so anything that round-trips through the game "
              + "while a duplicate is present is silently dropped.",
                Strings($"""
                    SELECT IFNULL(NULLIF(Name, ''), baserecord), COUNT(*) || ' ×'
                    FROM PlayerItem GROUP BY {Sql.IdentityKey}
                    HAVING COUNT(*) > 1 ORDER BY COUNT(*) DESC LIMIT {_sampleLimit};
                    """, columns: 2).Select(Pair).ToList()));
        }

        if (multiId > 0) {
            findings.Add(new Finding(Severity.Problem, "duplicates-under-distinct-cloud-ids",
                $"{multiId} of those {groups} carry more than one cloud id", multiId,
                "This is the signature of a deletion that never reached the server. A duplicate "
              + "made locally — an import run twice, a merge — reuses nothing and gets one id per "
              + "row on upload; a duplicate that came back down arrives under the id the server "
              + "still has for an item this machine had already deleted. Two ids for one item "
              + "means the server was never told."));
        }

        return new Section("Duplicates", facts, findings);
    }

    /// <summary>
    /// Rows whose parent is gone. These matter more than they look: PlayerItem and ReplicaItem2
    /// both key on an INTEGER primary key, which SQLite makes a rowid alias and *reuses* after a
    /// delete — so a leftover child row is inherited by the next item to take that id, and shows
    /// another item's stats. <c>Schema.RemoveOrphanedRows</c> sweeps all four on start.
    ///
    /// Which table they are left in says where the deletion came from, because the delete paths
    /// clean up different sets. A deletion arriving from the server clears ComputedItemStat
    /// (upstream's PlayerItemDaoImpl line 477, this port's CloudItemStore.Delete); a deletion
    /// made here does not — it clears ReplicaItem2, ReplicaItemRow and PlayerItemRecord and
    /// leaves the computed stats behind. So orphans in ComputedItemStat alone are the residue of
    /// items deleted *on this machine*, and counting their distinct parents counts them.
    /// </summary>
    private Section OrphanSection() {
        var findings = new List<Finding>();
        var facts = new List<(string, string)>();

        (string Id, string Summary, string Sql)[] checks = [
            ("orphan-item-records", "item record(s) belong to an item that is gone", """
                SELECT COUNT(*) FROM PlayerItemRecord r
                WHERE NOT EXISTS (SELECT 1 FROM PlayerItem p WHERE p.Id = r.PlayerItemId);
                """),
            ("orphan-computed-stats", "computed stat(s) belong to an item that is gone", """
                SELECT COUNT(*) FROM ComputedItemStat c
                WHERE NOT EXISTS (SELECT 1 FROM PlayerItem p WHERE p.Id = c.playeritemid);
                """),
            ("orphan-tooltips", "tooltip(s) belong to an item that is gone", """
                SELECT COUNT(*) FROM ReplicaItem2 r
                WHERE r.playeritemid IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM PlayerItem p WHERE p.Id = r.playeritemid);
                """),
            ("orphan-tooltip-rows", "tooltip row(s) belong to a tooltip that is gone", """
                SELECT COUNT(*) FROM ReplicaItemRow r
                WHERE NOT EXISTS (SELECT 1 FROM ReplicaItem2 i WHERE i.Id = r.replicaitemid);
                """),
        ];

        foreach (var (id, summary, sql) in checks) {
            if (!TablesIn(sql).All(Has)) continue;

            var count = Number(sql);
            if (count == 0) continue;

            findings.Add(new Finding(Severity.Problem, id, $"{count} {summary}", count,
                "SQLite reuses the row ids of deleted items, so these are not merely wasted "
              + "space — the next item to take one of those ids inherits them, and shows another "
              + "item's stats. Starting the app sweeps them up."));
        }

        // The count of items deleted on this machine, which is the other half of the deletion
        // story: how many were deleted here, against how many the server was told about.
        if (Has("ComputedItemStat") && Has("PlayerItem")) {
            var deletedHere = Number("""
                SELECT COUNT(DISTINCT c.playeritemid) FROM ComputedItemStat c
                WHERE NOT EXISTS (SELECT 1 FROM PlayerItem p WHERE p.Id = c.playeritemid);
                """);

            if (deletedHere > 0) {
                facts.Add(("deleted here", $"{deletedHere} item(s), by the stat rows they left behind"));

                findings.Add(new Finding(Severity.Note, "local-deletion-residue",
                    $"{deletedHere} item(s) were deleted on this machine at some point", deletedHere,
                    "Only a deletion made here leaves computed stats behind; one arriving from "
                  + "the server clears them. This is a floor, not a total — the app sweeps these "
                  + "on start, so anything deleted before the last sweep is not counted."));
            }
        }

        return new Section("Orphaned rows", facts, findings);
    }

    private Section TimestampSection() {
        if (!Has("PlayerItem")) return new Section("Timestamps", [], []);

        var findings = new List<Finding>();

        var missing = Number("SELECT COUNT(*) FROM PlayerItem WHERE created_at IS NULL OR created_at = 0;");
        if (missing > 0) {
            findings.Add(new Finding(Severity.Note, "missing-created-at",
                $"{missing} item(s) have no loot date", missing,
                "They sort to the beginning of the collection and cannot be filtered by age."));
        }

        // Upstream stores milliseconds. A value small enough to be a plausible seconds-since-epoch
        // date is one an importer wrote in the wrong unit; it renders as 1970.
        var seconds = Number("SELECT COUNT(*) FROM PlayerItem WHERE created_at BETWEEN 1 AND 100000000000;");
        if (seconds > 0) {
            findings.Add(new Finding(Severity.Note, "seconds-scale-created-at",
                $"{seconds} item(s) have a loot date in seconds rather than milliseconds", seconds,
                "Written by something that used the wrong unit. They show as 1970 and sort to the "
              + "front, but nothing else depends on the value."));
        }

        var future = Number(
            $"SELECT COUNT(*) FROM PlayerItem WHERE created_at > {DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds()};");
        if (future > 0) {
            findings.Add(new Finding(Severity.Note, "future-created-at",
                $"{future} item(s) claim to have been looted in the future", future,
                "A clock that was wrong when they were imported, or another unit mix-up."));
        }

        return new Section("Timestamps", [], findings);
    }

    // ---- the verdict --------------------------------------------------------------------

    /// <summary>
    /// What the numbers add up to, in the order someone would have to act on them. Only claims
    /// that the findings actually support: each paragraph is gated on the findings it explains.
    /// </summary>
    private IReadOnlyList<string> Verdict(IReadOnlyList<Section> sections) {
        var found = sections.SelectMany(section => section.Findings)
            .ToDictionary(finding => finding.Id, finding => finding);

        var verdict = new List<string>();

        if (found.ContainsKey("database-corrupt")) {
            verdict.Add("The file itself is damaged. Restore a backup; everything below is read "
                      + "out of a file SQLite does not consider intact.");
            return verdict;
        }

        var resurrection = found.TryGetValue("duplicates-under-distinct-cloud-ids", out var multi);
        var pending = found.TryGetValue("tombstones-pending", out var tombstones);

        if (resurrection && !pending) {
            // The lost-tombstone bug needs a second batch to bite — the first batch's ids are
            // cleared after the server has accepted them, so a deletion pass that fits in one
            // request loses nothing. A hundred is the server's own array limit
            // (BatchUtil.MaxBatchSize), which makes it the line between "this explains it" and
            // "this is leftovers from something that has since been fixed".
            var atScale = multi!.Count >= Sql.BatchSize;

            if (atScale) {
                verdict.Add(
                    $"Deleted items are coming back and the tombstone table is empty. {multi.Count} "
                  + "item(s) are here under two or more cloud ids, which only happens when the "
                  + "server still holds an item this machine deleted — and with nothing pending, "
                  + "the client has nothing left to tell it. The deletions were dropped rather "
                  + "than sent. Upstream's SyncDeletions clears the whole tombstone table after "
                  + "each accepted batch, so one failed request or a logout part-way through "
                  + "erases every deletion still queued. It takes more than "
                  + $"{Sql.BatchSize} deletions at once to reach a second batch, which is why this "
                  + "shows up after a big clear-out and never during normal play.");

                if (found.TryGetValue("local-deletion-residue", out var residue)) {
                    verdict.Add(
                        $"The scale fits: at least {residue.Count} item(s) have been deleted on "
                      + "this machine, against a tombstone table holding none. Deletions on that "
                      + "scale are exactly what it takes to reach the batch where they are lost.");
                }

                verdict.Add(
                    "Deleting them again will not stick until the deletions actually go out. Sign "
                  + "in, delete a small number, and re-run this tool: if the tombstone count rises "
                  + "and then falls to zero, deletions are getting through and the rest can be "
                  + "cleared in batches. If it falls to zero without the duplicates going away, "
                  + "they are still being dropped and the collection has to be cleaned on the "
                  + "server side instead.");
            }
            else {
                verdict.Add(
                    $"{multi.Count} item(s) are here under more than one cloud id, with nothing "
                  + "pending in the tombstone table. That is the shape a lost deletion leaves, but "
                  + $"at this scale it does not say sync is broken now: fewer than {Sql.BatchSize} "
                  + "deletions fit in a single request, and a single request either succeeds or "
                  + "leaves its tombstones in place to retry. Read it as residue from an earlier "
                  + "clear-out rather than an active fault, and confirm by deleting one item and "
                  + "re-running: the tombstone count should rise and then fall to zero.");
            }
        }
        else if (resurrection && pending) {
            verdict.Add(
                $"{multi!.Count} item(s) are here under more than one cloud id, and {tombstones!.Count} "
              + "deletion(s) are still queued. That combination is consistent with sync simply "
              + "being behind rather than broken — deletions go out on a cooldown and only while "
              + "signed in. Re-run this after a signed-in session: the tombstone count should "
              + "reach zero and stay there.");
        }
        else if (found.ContainsKey("duplicate-items")) {
            verdict.Add(
                "The collection holds duplicates, but each duplicate has its own cloud id, so "
              + "nothing here says a deletion was lost. This is the shape a local import run "
              + "twice, or a merge from a collection the account had already uploaded, leaves "
              + "behind.");
        }

        var unreadable = found.TryGetValue("unknown-baserecord", out var unknown);
        var untagged = found.TryGetValue("untagged-mod-items", out var mistagged);

        if (unreadable && untagged && unknown!.Count == mistagged!.Count) {
            verdict.Add(
                $"The {unknown.Count} unreadable item(s) and the {mistagged.Count} mistagged one(s) "
              + "are the same items. They came from a mod, but their rows say base game, so the "
              + "name, stats and icon are looked up in game data that has never contained their "
              + "records. Grim Dawn cannot take them back either. Re-parsing with the mod "
              + "installed fixes the lookup for items tagged with that mod — it does not fix "
              + "these, because the Mod column on these rows is blank.");
        }
        else if (unreadable) {
            verdict.Add(
                $"{unknown!.Count} item(s) name records the parsed game data does not have. If the "
              + "game has been patched, or a mod was uninstalled, re-parse and re-run this: the "
              + "count should drop. What remains after that is genuinely unreadable.");
        }

        if (found.ContainsKey("nameless-items") && !unreadable) {
            verdict.Add("Some items have no name even though their records are known, which points "
                      + "at the parse rather than the collection. Re-parse the game data.");
        }

        if (verdict.Count == 0) {
            verdict.Add("Nothing here explains items coming back or failing to load. The "
                      + "collection is internally consistent, every item resolves against the "
                      + "parsed game data, and online sync has nothing queued.");
        }

        return verdict;
    }

    // ---- the duplicate id list ----------------------------------------------------------

    /// <summary>
    /// The cloud ids of every redundant duplicate row — one per copy beyond the first, keeping
    /// the oldest row of each group.
    ///
    /// This is a report, not a repair: it is the list of ids the server would have to be told
    /// about for the collection to end up holding each item once. Written out so it can be
    /// checked against the server's own copy before anything acts on it.
    /// </summary>
    public IReadOnlyList<string> RedundantDuplicateCloudIds() {
        if (!Has("PlayerItem")) return [];

        return Strings($"""
            SELECT cloudid FROM PlayerItem
            WHERE cloudid IS NOT NULL AND cloudid <> ''
              AND Id NOT IN (SELECT MIN(Id) FROM PlayerItem GROUP BY {Sql.IdentityKey})
            ORDER BY Id;
            """).ToList();
    }

    // ---- plumbing -----------------------------------------------------------------------

    private bool Has(string table) => _tables.Contains(table);

    /// <summary>
    /// The tables a check reads — every word following a FROM — so a check over a table this
    /// database does not have is skipped rather than throwing. Good enough for the SQL above,
    /// which is all it is ever given.
    /// </summary>
    private static IEnumerable<string> TablesIn(string sql) {
        var words = sql.Split([' ', '\n', '\r', '\t', '(', ')', ';'], StringSplitOptions.RemoveEmptyEntries);

        return words.Zip(words.Skip(1))
            .Where(pair => pair.First.Equals("FROM", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Second);
    }

    private long Count(string table) => Has(table) ? Number($"SELECT COUNT(*) FROM {table};") : 0;

    private long Number(string sql) {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>Rows as tab-joined text, which is all the report ever needs from them.</summary>
    private IEnumerable<string> Strings(string sql, int columns = 1) {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            var values = Enumerable.Range(0, columns)
                .Select(i => reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "");
            results.Add(string.Join('\t', values));
        }
        return results;
    }

    /// <summary>A two-column row as `value  label`, the shape the report prints samples in.</summary>
    private static string Pair(string row) {
        var parts = row.Split('\t');
        return parts.Length < 2 ? row : $"{parts[1],8}  {parts[0]}";
    }

    private static string Moment(string milliseconds) =>
        long.TryParse(milliseconds, out var value)
            ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("yyyy-MM-dd")
            : "?";

    public void Dispose() => _connection.Dispose();
}
