using System.Globalization;

namespace IAGrim.Platform;

/// <summary>One stat line the hook captured from the item's in-game tooltip.</summary>
/// <param name="TextClass">Grim Dawn's GameTextClass; groups name / type / stat lines.</param>
/// <param name="Text">Display text, still carrying Grim Dawn colour codes such as ^P and ^B.</param>
public sealed record LootStat(int TextClass, string Text);

/// <summary>
/// An item the hook intercepted on its way into the stash.
/// Field order and semantics mirror <c>CsvParsingService.Deserialize</c> upstream.
/// </summary>
public sealed record LootedItem {
    public string Mod { get; init; } = "";
    public bool IsHardcore { get; init; }
    public string BaseRecord { get; init; } = "";
    public string? PrefixRecord { get; init; }
    public string? SuffixRecord { get; init; }
    public long Seed { get; init; }
    public long RerollsUsed { get; init; }
    public string? ModifierRecord { get; init; }
    public string? MateriaRecord { get; init; }
    public string? RelicCompletionBonusRecord { get; init; }
    public long RelicSeed { get; init; }
    public string? EnchantmentRecord { get; init; }
    public long EnchantmentSeed { get; init; }
    public string? TransmuteRecord { get; init; }
    public string? AscendantAffixNameRecord { get; init; }
    public string? AscendantAffix2hNameRecord { get; init; }
    public long AffixRerollsUsed { get; init; }

    /// <summary>
    /// How many are in the stack. One for anything that does not stack, which is almost
    /// everything — but a stash can hold 27 aether clusters as a single entry, and importing
    /// that as one item quietly loses 26 of them.
    ///
    /// The hook's loot CSV has no column for this: it reports items as they are looted, one at
    /// a time. It matters for the file-based imports, which read whole stashes.
    /// </summary>
    public long StackCount { get; init; } = 1;

    public IReadOnlyList<LootStat> Stats { get; init; } = [];

    /// <summary>First stat line, which the game emits as the item's name.</summary>
    public string? Name => Stats.FirstOrDefault()?.Text;

    /// <summary>
    /// Every record the item is composed of, in upstream's order —
    /// <c>PlayerItemDaoImpl.GetRecordsForItem</c>. This is what goes into PlayerItemRecord, and
    /// it is deliberately wider than the three records the seed engine rolls from: a socketed
    /// component and an ascendant affix both carry stats the damage filters must see.
    ///
    /// Note the modifier and transmute records are *not* in it. That is upstream's choice, not
    /// an oversight here — including them would make the filters match items upstream does not.
    /// </summary>
    public IEnumerable<string> Records() {
        if (!string.IsNullOrEmpty(BaseRecord)) yield return BaseRecord;
        if (!string.IsNullOrEmpty(PrefixRecord)) yield return PrefixRecord;
        if (!string.IsNullOrEmpty(SuffixRecord)) yield return SuffixRecord;
        if (!string.IsNullOrEmpty(MateriaRecord)) yield return MateriaRecord;
        if (!string.IsNullOrEmpty(AscendantAffixNameRecord)) yield return AscendantAffixNameRecord;
        if (!string.IsNullOrEmpty(AscendantAffix2hNameRecord)) yield return AscendantAffix2hNameRecord;
    }

    /// <summary>Name with Grim Dawn's colour codes stripped, for logs and search.</summary>
    public string? PlainName => Name is null ? null : StripColourCodes(Name);

    /// <summary>
    /// Grim Dawn marks up text with ^ followed by a single letter (^P mythical, ^B epic,
    /// ^L a damage type, ^S resets). They are display hints, not part of the name.
    /// </summary>
    public static string StripColourCodes(string text) {
        var output = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++) {
            if (text[i] == '^' && i + 1 < text.Length) {
                i++;              // skip the marker and the letter that follows
                continue;
            }
            output.Append(text[i]);
        }
        return output.ToString().Trim();
    }
}

public static class LootCsv {
    /// <summary>
    /// Column counts upstream accepts, kept identical so files written by either host parse
    /// the same way:
    ///   13 legacy, 14 + rerolls, 16 + ascendants, 17 + affix rerolls (current).
    /// </summary>
    private static readonly int[] AcceptedColumnCounts = [13, 14, 16, 17];

    public static LootedItem? ParseFile(string path, out string? error) {
        string text;
        try {
            text = File.ReadAllText(path, System.Text.Encoding.UTF8);
        }
        catch (IOException ex) {
            error = $"could not read: {ex.Message}";
            return null;
        }
        return Parse(text, out error);
    }

    public static LootedItem? Parse(string content, out string? error) {
        // The hook writes a UTF-8 BOM so readers can detect the encoding
        // (InventorySack_AddItem::Persist).
        content = content.TrimStart('﻿');

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.TrimEnd('\r'))
                           .ToArray();
        if (lines.Length == 0) {
            error = "file is empty";
            return null;
        }

        var columns = lines[0].Split(';');
        if (!AcceptedColumnCounts.Contains(columns.Length)) {
            error = $"expected {string.Join('/', AcceptedColumnCounts)} columns, got {columns.Length}";
            return null;
        }

        var hasRerolls      = columns.Length >= 14;
        var hasAscendants   = columns.Length >= 16;
        var hasAffixRerolls = columns.Length >= 17;

        var n = 0;
        var item = new LootedItem {
            Mod                        = columns[n++],
            IsHardcore                 = columns[n++] == "1",
            BaseRecord                 = columns[n++].Trim(),
            PrefixRecord               = Nullable(columns[n++]),
            SuffixRecord               = Nullable(columns[n++]),
            Seed                       = ToLong(columns[n++]),
            RerollsUsed                = hasRerolls ? ToLong(columns[n++]) : 0,
            ModifierRecord             = Nullable(columns[n++]),
            MateriaRecord              = Nullable(columns[n++]),
            RelicCompletionBonusRecord = Nullable(columns[n++]),
            RelicSeed                  = ToLong(columns[n++]),
            EnchantmentRecord          = Nullable(columns[n++]),
            EnchantmentSeed            = ToLong(columns[n++]),
            TransmuteRecord            = Nullable(columns[n++]),
            AscendantAffixNameRecord   = hasAscendants ? Nullable(columns[n++]) : null,
            AscendantAffix2hNameRecord = hasAscendants ? Nullable(columns[n++]) : null,
            AffixRerollsUsed           = hasAffixRerolls ? ToLong(columns[n++]) : 0,
            Stats                      = ParseStats(lines.Skip(1)),
        };

        if (string.IsNullOrWhiteSpace(item.BaseRecord)) {
            error = "row has no base record";
            return null;
        }

        error = null;
        return item;
    }

    /// <summary>
    /// Stat lines are "textClass;text". The text may itself contain semicolons, so only the
    /// first separator is significant.
    /// </summary>
    private static List<LootStat> ParseStats(IEnumerable<string> lines) {
        var stats = new List<LootStat>();
        foreach (var line in lines) {
            var split = line.IndexOf(';');
            if (split <= 0) continue;

            if (!int.TryParse(line.AsSpan(0, split), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out var textClass)) {
                continue;
            }
            stats.Add(new LootStat(textClass, line[(split + 1)..]));
        }
        return stats;
    }

    /// <summary>
    /// Writes the 17-column row the hook expects when materialising an item back into the
    /// game (GAME::Deserialize in GrimTypes.cpp). Column order is the inverse of Parse and
    /// must stay that way — the hook indexes positionally and a shifted column silently
    /// creates the wrong item.
    ///
    /// Only the replica row is written. Stat lines are display data the game regenerates.
    /// </summary>
    public static string SerializeForGame(LootedItem item) {
        var columns = new[] {
            item.Mod ?? "",
            item.IsHardcore ? "1" : "0",
            item.BaseRecord.Trim(),
            item.PrefixRecord?.Trim() ?? "",
            item.SuffixRecord?.Trim() ?? "",
            item.Seed.ToString(CultureInfo.InvariantCulture),
            item.RerollsUsed.ToString(CultureInfo.InvariantCulture),
            item.ModifierRecord?.Trim() ?? "",
            item.MateriaRecord?.Trim() ?? "",
            item.RelicCompletionBonusRecord?.Trim() ?? "",
            item.RelicSeed.ToString(CultureInfo.InvariantCulture),
            item.EnchantmentRecord?.Trim() ?? "",
            item.EnchantmentSeed.ToString(CultureInfo.InvariantCulture),
            item.TransmuteRecord?.Trim() ?? "",
            item.AscendantAffixNameRecord?.Trim() ?? "",
            item.AscendantAffix2hNameRecord?.Trim() ?? "",
            item.AffixRerollsUsed.ToString(CultureInfo.InvariantCulture),
        };
        return string.Join(';', columns);
    }

    /// <summary>
    /// Filename the hook will accept: it only picks up files ending in .csv
    /// (InventorySack_AddItem::ThreadMain).
    /// </summary>
    public static string NewTransferFileName() => $"{Guid.NewGuid():N}.csv";

    private static string? Nullable(string value) {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static long ToLong(string value) =>
        long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
}
