// Prints the SQL this port's search actually runs, so it can be compared against upstream's.
//
// Reads one case per line from stdin — "name<TAB>{json ItemQuery}" — and writes
// "name<TAB>sql" back. The SQL is the real thing: the FROM comes from CollectionService and the
// WHERE from ItemQueryBuilder, both reached by reflection rather than copied, so a change to
// either shows up here rather than being quietly duplicated.
//
// Parameters are inlined because the comparison is run through the sqlite3 CLI. Every value
// originates in this file's own test cases.

using System.Globalization;
using System.Reflection;
using System.Text.Json;

// "--prepare <db>" brings a database up to the shape the client guarantees for anything it
// opens — including the conversion of NULL records and second-scale timestamps into upstream's
// values. Comparing filters on a database the client would have normalised is the honest test;
// on a raw snapshot, the Components and Duplicates cases match nothing on either side and prove
// nothing.
if (args.Length == 2 && args[0] == "--prepare") {
    using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={args[1]}");
    connection.Open();
    IAGrim.Platform.Schema.Apply(connection);
    return;
}

// "--describe <db>" renders every item's tooltip the way the client does and reports how the
// lines came out. Colours are not stored anywhere — a line is built per request from the game's
// stat rows — so this is the only way to check that a collection renders correctly: render it.
if (args.Length == 2 && args[0] == "--describe") {
    Describe(args[1]);
    return;
}

var assembly = typeof(IAGrim.Host.ItemQuery).Assembly;

var builder = assembly.GetType("IAGrim.Host.ItemQueryBuilder")
    ?? throw new InvalidOperationException("ItemQueryBuilder not found");
var build = builder.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)
    ?? throw new InvalidOperationException("ItemQueryBuilder.Build not found");

var from = (string)(assembly.GetType("IAGrim.Host.CollectionService")!
    .GetField("SearchFrom", BindingFlags.NonPublic | BindingFlags.Static)!
    .GetRawConstantValue()!);

var options = new JsonSerializerOptions {
    PropertyNameCaseInsensitive = true,
    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
};

string? line;
while ((line = Console.ReadLine()) != null) {
    if (string.IsNullOrWhiteSpace(line)) continue;

    var tab = line.IndexOf('\t');
    var name = line[..tab];
    var query = JsonSerializer.Deserialize<IAGrim.Host.ItemQuery>(line[(tab + 1)..], options)!;

    // A ValueTuple carries its parts in fields, not properties.
    var result = build.Invoke(null, [query])!;
    var where = (string)result.GetType().GetField("Item1")!.GetValue(result)!;
    var parameters = (Dictionary<string, object>)result.GetType().GetField("Item2")!.GetValue(result)!;

    // Longest name first: ":min" is a prefix of ":minlevel", and replacing it first would
    // leave "'1'level" behind.
    foreach (var key in parameters.Keys.OrderByDescending(k => k.Length)) {
        where = where.Replace(":" + key, Literal(parameters[key]));
    }

    Console.WriteLine($"{name}\t{$"SELECT p.Id {from} WHERE {where} ORDER BY p.Id".ReplaceLineEndings(" ")}");
}

static void Describe(string databasePath) {
    using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
    connection.Open();

    var ids = new List<long>();
    using (var command = connection.CreateCommand()) {
        command.CommandText = "SELECT Id FROM PlayerItem ORDER BY Id;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
    }

    var text = new IAGrim.Core.ItemStats.ItemStatText(databasePath);
    if (!text.Available) {
        Console.WriteLine("unavailable\tthe game's data has not been parsed");
        return;
    }

    // Items whose tooltip the game itself drew: those are coloured by row type instead, and
    // are counted rather than split.
    var captured = new HashSet<long>();
    using (var command = connection.CreateCommand()) {
        command.CommandText = "SELECT DISTINCT playeritemid FROM ReplicaItem2 WHERE playeritemid IS NOT NULL;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) captured.Add(reader.GetInt64(0));
    }

    long described = 0, undescribed = 0, lines = 0, split = 0, unsplit = 0, headings = 0,
         skills = 0, extras = 0;
    var sections = new SortedDictionary<string, long>(StringComparer.Ordinal);

    foreach (var id in ids) {
        if (captured.Contains(id)) continue;

        var rows = text.Describe(connection, id);
        if (rows.Count == 0) { undescribed++; continue; }
        described++;

        foreach (var row in rows) {
            lines++;
            var section = row.Section?.ToString().ToLowerInvariant() ?? "none";
            sections[section] = sections.TryGetValue(section, out var n) ? n + 1 : 1;

            // "Bonus to All Pets" is a heading rather than a stat: upstream draws it from its
            // own markup, and it is meant to carry a row type instead of being split.
            if (row.TextClass >= 0) headings++;
            // A stat with neither half has nothing to colour: it would be drawn in the
            // container's default, which is what the whole collection looked like before.
            else if (row.Modifier is null && row.Label is null) unsplit++;
            else split++;
            if (row.Skill is not null) skills++;
            if (row.Extras is not null) extras++;
        }
    }

    Console.WriteLine($"captured\t{captured.Count}");
    Console.WriteLine($"described\t{described}");
    Console.WriteLine($"undescribed\t{undescribed}");
    Console.WriteLine($"lines\t{lines}");
    Console.WriteLine($"split\t{split}");
    Console.WriteLine($"unsplit\t{unsplit}");
    Console.WriteLine($"headings\t{headings}");
    Console.WriteLine($"skills\t{skills}");
    Console.WriteLine($"extras\t{extras}");
    foreach (var (section, count) in sections) Console.WriteLine($"section.{section}\t{count}");
}

static string Literal(object value) => value switch {
    string text => "'" + text.Replace("'", "''") + "'",
    bool flag => flag ? "1" : "0",
    _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
};
