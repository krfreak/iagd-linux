using System.Text.Json;
using System.Text.Json.Serialization;

namespace IAGrim.DbDoctor;

public enum Severity {
    /// <summary>Worth knowing, nothing wrong.</summary>
    Note,

    /// <summary>The collection is in a state that loses or resurrects items.</summary>
    Problem,
}

/// <summary>
/// One thing found wrong, or worth mentioning.
///
/// <see cref="Id"/> is the stable handle — the text is free to be rewritten, the id is what a
/// script greps for and what the JSON output keys on.
/// </summary>
public sealed record Finding(
    Severity Severity,
    string Id,
    string Summary,
    long Count = 0,
    string? Meaning = null,
    IReadOnlyList<string>? Detail = null);

/// <summary>A group of findings, plus the plain counts that put them in proportion.</summary>
public sealed record Section(
    string Title,
    IReadOnlyList<(string Label, string Value)> Facts,
    IReadOnlyList<Finding> Findings);

public sealed record Report(
    string DatabasePath,
    long SizeBytes,
    IReadOnlyList<Section> Sections,
    IReadOnlyList<string> Verdict) {

    public IEnumerable<Finding> Findings => Sections.SelectMany(section => section.Findings);

    public bool HasProblems => Findings.Any(finding => finding.Severity == Severity.Problem);

    /// <summary>
    /// The report as a terminal page, in the shape `iagd status` uses: a heading per section,
    /// two-space indented facts, then the findings underneath the numbers that produced them.
    /// </summary>
    public void WriteText(TextWriter output) {
        output.WriteLine($"Database  {DatabasePath}");
        output.WriteLine($"          {SizeBytes / 1024 / 1024} MB");

        foreach (var section in Sections) {
            if (section.Facts.Count == 0 && section.Findings.Count == 0) continue;

            output.WriteLine();
            output.WriteLine(section.Title);

            var width = section.Facts.Count == 0 ? 0 : section.Facts.Max(fact => fact.Label.Length);
            foreach (var (label, value) in section.Facts) {
                output.WriteLine($"  {label.PadRight(width)}  {value}");
            }

            foreach (var finding in section.Findings) {
                output.WriteLine();
                var mark = finding.Severity == Severity.Problem ? "!!" : "--";
                output.WriteLine($"  {mark} {finding.Summary}");

                foreach (var line in finding.Detail ?? []) {
                    output.WriteLine($"        {line}");
                }

                if (finding.Meaning is not null) {
                    foreach (var line in Wrap(finding.Meaning, 84)) {
                        output.WriteLine($"     {line}");
                    }
                }
            }
        }

        if (Verdict.Count == 0) return;

        output.WriteLine();
        output.WriteLine("Verdict");
        foreach (var paragraph in Verdict) {
            foreach (var line in Wrap(paragraph, 86)) output.WriteLine($"  {line}");
            output.WriteLine();
        }
    }

    public void WriteJson(TextWriter output) {
        var document = new {
            database = DatabasePath,
            sizeBytes = SizeBytes,
            hasProblems = HasProblems,
            facts = Sections.ToDictionary(
                section => section.Title,
                section => section.Facts.ToDictionary(fact => fact.Label, fact => fact.Value)),
            findings = Findings.Select(finding => new {
                id = finding.Id,
                severity = finding.Severity.ToString().ToLowerInvariant(),
                summary = finding.Summary,
                count = finding.Count,
                meaning = finding.Meaning,
                detail = finding.Detail,
            }),
            verdict = Verdict,
        };

        output.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Greedy word wrap, so an explanation stays readable in an 80-column terminal.</summary>
    private static IEnumerable<string> Wrap(string text, int width) {
        var line = new List<string>();
        var length = 0;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            if (line.Count > 0 && length + 1 + word.Length > width) {
                yield return string.Join(' ', line);
                line.Clear();
                length = 0;
            }

            line.Add(word);
            length += (length == 0 ? 0 : 1) + word.Length;
        }

        if (line.Count > 0) yield return string.Join(' ', line);
    }
}
