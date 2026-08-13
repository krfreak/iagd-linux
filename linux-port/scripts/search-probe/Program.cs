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

static string Literal(object value) => value switch {
    string text => "'" + text.Replace("'", "''") + "'",
    bool flag => flag ? "1" : "0",
    _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
};
