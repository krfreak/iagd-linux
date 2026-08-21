using IAGrim.Platform;
using Microsoft.Data.Sqlite;

namespace IAGrim.DbDoctor;

/// <summary>
/// `iagd-dbdoctor` — reads a collection, reports what is wrong with it, and repairs it on
/// request.
///
/// Written for the two complaints that arrive together and are usually the same incident: items
/// the game will not take back, and items that come back after being deleted. It answers both
/// out of the database alone, without a server, a game install or a signed-in session, because
/// the database is usually all anyone can send you.
///
/// Reporting never writes. Repairing writes only what was named on the command line, only after
/// taking a backup, and prints what it is about to do first.
/// </summary>
internal static class Program {
    /// <summary>The repairs <c>--fix all</c> covers: everything that needs no further input.</summary>
    private static readonly string[] Unattended = ["duplicates", "orphans", "timestamps"];

    private static readonly string[] Known = [.. Unattended, "mod-tags"];

    private static int Main(string[] args) {
        if (args.Contains("--help") || args.Contains("-h")) {
            Help();
            return 0;
        }

        var path = ValueOf(args, "--db")
            ?? args.FirstOrDefault(arg => !arg.StartsWith('-'))
            ?? LinuxPaths.DatabaseFile;

        if (!File.Exists(path)) {
            Console.Error.WriteLine($"error: no database at {path}");
            Console.Error.WriteLine("       pass one as an argument, or with --db <path>.");
            return 1;
        }

        var limit = int.TryParse(ValueOf(args, "--limit"), out var parsed) ? parsed : 5;
        var json = args.Contains("--json");

        try {
            Report report;
            using (var diagnosis = new Diagnosis(path, limit)) {
                report = diagnosis.Run();

                if (json) report.WriteJson(Console.Out);
                else report.WriteText(Console.Out);

                if (ValueOf(args, "--ids") is { } idsFile) {
                    var ids = diagnosis.RedundantDuplicateCloudIds();
                    File.WriteAllLines(idsFile, ids);
                    Console.Error.WriteLine($"{ids.Count} duplicate cloud id(s) written to {idsFile}");
                }
            }

            var requested = ValueOf(args, "--fix");
            if (requested is null) {
                if (!json) Suggest(report);
                return report.HasProblems ? 2 : 0;
            }

            return Fix(path, args, requested, limit, json);
        }
        catch (SqliteException ex) {
            Console.Error.WriteLine($"error: {path} could not be read: {ex.Message}");
            Console.Error.WriteLine("       if the app is running, the file may be mid-write; try again.");
            return 1;
        }
    }

    /// <summary>
    /// Runs the named repairs. The plan is printed before anything is written and, unless the
    /// caller has already said yes on the command line, waits for them to agree to it.
    /// </summary>
    private static int Fix(string path, string[] args, string requested, int limit, bool json) {
        var repairs = requested == "all"
            ? Unattended
            : requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var unknown = repairs.Where(name => !Known.Contains(name)).ToList();
        if (unknown.Count > 0) {
            Console.Error.WriteLine($"error: no such repair: {string.Join(", ", unknown)}");
            Console.Error.WriteLine($"       known repairs: {string.Join(", ", Known)}");
            return 1;
        }

        var mapping = ModMapping(args, out var mappingError);
        if (mappingError is not null) {
            Console.Error.WriteLine($"error: {mappingError}");
            return 1;
        }

        if (repairs.Contains("mod-tags") && mapping.Count == 0) {
            Console.Error.WriteLine("error: mod-tags needs to be told which mod each record root belongs to.");
            Console.Error.WriteLine("       e.g. --set-mod grimleague=grimleague");
            Console.Error.WriteLine("       The name must match the folder under the game's mods/ directory;");
            Console.Error.WriteLine("       it is not derivable from the record path.");
            return 1;
        }

        Repair repair;
        try {
            repair = new Repair(path);
        }
        catch (SqliteException ex) {
            // Almost always the app holding the collection open. Repairing underneath a running
            // client would race its own writes, so this is a refusal, not something to retry
            // harder at.
            Console.Error.WriteLine($"error: cannot open {path} for writing: {ex.Message}");
            Console.Error.WriteLine("       close Item Assistant and try again. Nothing was written.");
            return 1;
        }
        catch (InvalidOperationException ex) {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        using var _ = repair;

        Console.WriteLine();
        Console.WriteLine("Repair plan");
        var plan = repairs.Select(name => Apply(repair, name, mapping, commit: false)).ToList();

        foreach (var outcome in plan) Print(outcome);

        if (plan.All(outcome => outcome.Rows == 0)) {
            Console.WriteLine();
            Console.WriteLine("  Nothing to do.");
            return 0;
        }

        if (!args.Contains("--yes")) {
            Console.WriteLine();
            Console.Write("Apply? This rewrites the collection. [y/N] ");
            var answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine("Nothing written.");
                return 0;
            }
        }

        if (!args.Contains("--no-backup")) {
            // A backup that failed is a repair that must not start. The copy is the whole reason
            // this is safe to run on someone's only collection, and it is a plausible failure —
            // the file is often hundreds of megabytes and the disk is often nearly full.
            try {
                Console.WriteLine();
                Console.WriteLine($"  backup   {repair.Backup(path)}");
            }
            catch (Exception ex) {
                Console.Error.WriteLine($"error: could not write the backup: {ex.Message}");
                Console.Error.WriteLine("       nothing was repaired. Free some space, or pass");
                Console.Error.WriteLine("       --no-backup if you have your own copy.");
                return 1;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Repaired");

        // Each repair is its own transaction, so one that throws leaves the ones before it
        // applied and the ones after it untouched. Say exactly that rather than unwinding a
        // stack trace over the user's collection: the backup taken above is the way back, and
        // they need to be told it is the way back.
        foreach (var name in repairs) {
            try {
                Print(Apply(repair, name, mapping, commit: true));
            }
            catch (Exception ex) {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"error: the '{name}' repair failed: {ex.Message}");
                Console.Error.WriteLine("       Repairs before this one were applied and committed;");
                Console.Error.WriteLine("       this one and any after it were not.");
                if (!args.Contains("--no-backup")) {
                    Console.Error.WriteLine("       To undo everything, restore the backup printed above.");
                }
                return 1;
            }
        }

        // Say it worked by measuring it, not by asserting it: the whole diagnosis runs again and
        // the new verdict is what the user is left looking at.
        Console.WriteLine();
        Console.WriteLine("Re-checking");
        Console.WriteLine();

        using var after = new Diagnosis(path, limit);
        var report = after.Run();
        if (json) report.WriteJson(Console.Out);
        else report.WriteText(Console.Out);

        return report.HasProblems ? 2 : 0;
    }

    private static RepairOutcome Apply(
        Repair repair, string name, IReadOnlyDictionary<string, string> mapping, bool commit) =>
        name switch {
            "duplicates" => repair.Duplicates(commit),
            "orphans" => repair.Orphans(commit),
            "timestamps" => repair.Timestamps(commit),
            "mod-tags" => repair.ModTags(mapping, commit),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown repair"),
        };

    private static void Print(RepairOutcome outcome) {
        Console.WriteLine();
        Console.WriteLine($"  {outcome.Id,-12} {outcome.Summary}");
        foreach (var note in outcome.Notes ?? []) Console.WriteLine($"               {note}");
    }

    /// <summary>Parses `--set-mod <record root>=<mod folder name>`, which may be repeated.</summary>
    private static Dictionary<string, string> ModMapping(string[] args, out string? error) {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;

        for (var i = 0; i < args.Length - 1; i++) {
            if (args[i] != "--set-mod") continue;

            var parts = args[i + 1].Split('=', 2);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) {
                error = $"--set-mod wants <record root>=<mod name>, not '{args[i + 1]}'";
                return mapping;
            }

            mapping[parts[0]] = parts[1];
        }

        return mapping;
    }

    /// <summary>Which repairs the findings justify, so the next command is on screen already.</summary>
    private static void Suggest(Report report) {
        var ids = report.Findings.Select(finding => finding.Id).ToHashSet(StringComparer.Ordinal);

        var repairs = new List<string>();
        if (ids.Contains("duplicate-items")) repairs.Add("duplicates");
        if (ids.Overlaps(new[] {
            "orphan-item-records", "orphan-computed-stats", "orphan-tooltips", "orphan-tooltip-rows",
        })) repairs.Add("orphans");
        if (ids.Contains("seconds-scale-created-at")) repairs.Add("timestamps");

        if (repairs.Count > 0) {
            Console.WriteLine("Repair");
            Console.WriteLine($"  iagd-dbdoctor {report.DatabasePath} --fix {string.Join(',', repairs)}");
            Console.WriteLine("  Prints what it will do and asks before writing. Backs up first.");
        }

        if (ids.Contains("untagged-mod-items")) {
            if (repairs.Count > 0) Console.WriteLine();
            else Console.WriteLine("Repair");
            Console.WriteLine("  The mistagged items need the mod's folder name, which is not in the");
            Console.WriteLine("  database and cannot be read off the record path:");
            Console.WriteLine($"  iagd-dbdoctor {report.DatabasePath} --fix mod-tags --set-mod <root>=<mod folder>");
        }
    }

    private static void Help() {
        Console.WriteLine($"""
            iagd-dbdoctor — what is wrong with a collection, and how to fix it

              iagd-dbdoctor [<database>] [options]

              Reads the database and reports on the two things that go wrong with one: items
              the game can no longer read, and items that come back after being deleted.
              Reporting leaves the collection byte-identical.

              --db <path>     The collection to read. Also accepted as a bare argument.
                              Default: {LinuxPaths.DatabaseFile}
              --json          Machine-readable output.
              --limit <n>     Examples to show per finding (default 5).
              --ids <path>    Write the cloud ids of every redundant duplicate row — the
                              deletions the server would need to be told about.

            Repair

              --fix <names>   Comma-separated, or 'all'. Prints the plan, asks, backs the
                              database up, then writes. Close the app first.

                              duplicates  Collapse each duplicated item to its oldest copy,
                                          writing a tombstone for every copy removed so the
                                          server is actually told. Without the tombstone the
                                          copies come straight back.
                              orphans     Drop child rows whose item is gone.
                              timestamps  Loot dates stored in seconds, rewritten as ms.
                              mod-tags    Tag items with the mod they came from. Needs
                                          --set-mod; 'all' does not include it.

              --set-mod <root>=<mod>   Repeatable. Records under <root>/ came from the mod
                                       folder <mod>. Not guessable: the mod name Grim Dawn
                                       reports is a folder name, not the record path.
              --yes           Do not ask before writing.
              --no-backup     Skip the backup. The backup is the reason this is safe.

              Exit code 0 when the collection is sound, 2 when it is not, 1 on failure.
            """);
    }

    private static string? ValueOf(string[] args, string name) {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
