using EvilsoftCommons;
using IAGrim.Platform;
using Microsoft.Data.Sqlite;

namespace IAGrim.Core.Backup;

/// <summary>
/// Import and export in the GD Stash / Mambastash interchange format.
///
/// This is the format the Grim Dawn tool ecosystem actually shares items in, so supporting it
/// is what lets a collection move between this port, the Windows tool, and GD Stash — rather
/// than being trapped in a SQLite file only this program understands.
///
/// The layout is upstream's <c>GDFileExporter</c>, and the reading and writing is done with
/// <c>EvilsoftCommons.IOHelper</c> — the same primitives upstream uses, from the project this
/// port already references. That is deliberate: the format is a positional binary stream with a
/// version header and length-prefixed strings, and re-deriving the field order from the file
/// would be exactly the kind of transcription that goes wrong silently, producing items with
/// their affixes shifted by one.
///
/// **Version 3.** Versions 1 and 2 are readable (upstream's reader handles them and so does
/// this one); writing is always version 3, since that is what upstream writes.
/// </summary>
public static class ItemExport {
    private const int SupportedFileVersion = 3;

    /// <summary>One item, in the fields the interchange format carries.</summary>
    public sealed record ExportedItem {
        public string BaseRecord { get; init; } = "";
        public string PrefixRecord { get; init; } = "";
        public string SuffixRecord { get; init; } = "";
        public string ModifierRecord { get; init; } = "";
        public string TransmuteRecord { get; init; } = "";
        public uint Seed { get; init; }
        public string MateriaRecord { get; init; } = "";
        public string RelicCompletionBonusRecord { get; init; } = "";
        public uint RelicSeed { get; init; }
        public string EnchantmentRecord { get; init; } = "";
        public uint MateriaCombines { get; init; }
        public uint EnchantmentSeed { get; init; }
        public string AscendantAffixNameRecord { get; init; } = "";
        public string AscendantAffix2hNameRecord { get; init; } = "";
        public uint Unknown { get; init; }
        public uint StackCount { get; init; }
        public uint RerollsUsed { get; init; }
        public uint AffixRerollsUsed { get; init; }
        public bool IsHardcore { get; init; }
    }

    // ------------------------------------------------------------------ write

    /// <summary>
    /// Writes the collection, or the subset matching <paramref name="hardcoreOnly"/>.
    ///
    /// Hardcore and softcore are separate stashes in Grim Dawn, and an import that mixed them
    /// would put softcore items into a hardcore character's stash. Upstream keeps them apart by
    /// exporting one mod/difficulty pair at a time; the filter here serves the same purpose.
    /// </summary>
    public static int Export(string databasePath, string outputFile, bool? hardcoreOnly = null,
                             string? mod = null) {
        var items = Read(databasePath, hardcoreOnly, mod);
        Write(outputFile, items);
        return items.Count;
    }

    public static void Write(string outputFile, IReadOnlyList<ExportedItem> items) {
        using var stream = new FileStream(outputFile, FileMode.Create);

        IOHelper.Write(stream, SupportedFileVersion);
        IOHelper.Write(stream, items.Count);

        foreach (var item in items) {
            IOHelper.WriteBytePrefixed(stream, item.BaseRecord);
            IOHelper.WriteBytePrefixed(stream, item.PrefixRecord);
            IOHelper.WriteBytePrefixed(stream, item.SuffixRecord);
            IOHelper.WriteBytePrefixed(stream, item.ModifierRecord);
            IOHelper.WriteBytePrefixed(stream, item.TransmuteRecord);

            IOHelper.Write(stream, item.Seed);
            IOHelper.WriteBytePrefixed(stream, item.MateriaRecord);
            IOHelper.WriteBytePrefixed(stream, item.RelicCompletionBonusRecord);

            IOHelper.Write(stream, item.RelicSeed);
            IOHelper.WriteBytePrefixed(stream, item.EnchantmentRecord);

            IOHelper.Write(stream, item.MateriaCombines);
            IOHelper.Write(stream, item.EnchantmentSeed);

            IOHelper.WriteBytePrefixed(stream, item.AscendantAffixNameRecord);
            IOHelper.WriteBytePrefixed(stream, item.AscendantAffix2hNameRecord);
            IOHelper.Write(stream, item.Unknown);

            IOHelper.Write(stream, item.StackCount);
            IOHelper.Write(stream, item.RerollsUsed);
            IOHelper.Write(stream, item.AffixRerollsUsed);
            IOHelper.Write(stream, item.IsHardcore);
            IOHelper.Write(stream, (byte)0);   // character name, which this port does not track
        }
    }

    // ------------------------------------------------------------------- read

    /// <summary>
    /// Parses an exported file. Field order is upstream's <c>GDFileExporter.Read</c>, including
    /// the version gates — a v1 file has no ascendant affixes and no reroll counts, and reading
    /// it as v3 would consume bytes that are not there.
    /// </summary>
    public static List<ExportedItem> Parse(byte[] bytes) {
        var items = new List<ExportedItem>();
        var position = 0;

        var fileVersion = IOHelper.GetInt(bytes, position); position += 4;
        if (fileVersion > SupportedFileVersion || fileVersion < 1) {
            throw new InvalidDataException(
                $"Unsupported GD Stash file version: expected 1-{SupportedFileVersion}, got {fileVersion}");
        }

        string ReadString() {
            var value = IOHelper.GetBytePrefixedString(bytes, position);
            position += 1 + (value?.Length ?? 0);
            return value ?? "";
        }

        uint ReadUInt() {
            var value = IOHelper.GetUInt(bytes, position); position += 4;
            return value;
        }

        var count = IOHelper.GetInt(bytes, position); position += 4;
        for (var i = 0; i < count; i++) {
            var baseRecord = ReadString();
            var prefix = ReadString();
            var suffix = ReadString();
            var modifier = ReadString();
            var transmute = ReadString();
            var seed = ReadUInt();
            var materia = ReadString();
            var relicBonus = ReadString();
            var relicSeed = ReadUInt();
            var enchantment = ReadString();
            var materiaCombines = ReadUInt();
            var enchantmentSeed = ReadUInt();

            var ascendant1 = "";
            var ascendant2 = "";
            if (fileVersion >= 2) {
                ascendant1 = ReadString();
                ascendant2 = ReadString();
            }

            var unknown = ReadUInt();
            var stackCount = ReadUInt();

            uint rerolls = 0;
            if (fileVersion >= 2) rerolls = ReadUInt();

            uint affixRerolls = 0;
            if (fileVersion >= 3) affixRerolls = ReadUInt();

            var hardcore = bytes[position++] == 1;
            ReadString();   // character name, which this port has nowhere to put

            items.Add(new ExportedItem {
                BaseRecord = baseRecord,
                PrefixRecord = prefix,
                SuffixRecord = suffix,
                ModifierRecord = modifier,
                TransmuteRecord = transmute,
                Seed = seed,
                MateriaRecord = materia,
                RelicCompletionBonusRecord = relicBonus,
                RelicSeed = relicSeed,
                EnchantmentRecord = enchantment,
                MateriaCombines = materiaCombines,
                EnchantmentSeed = enchantmentSeed,
                AscendantAffixNameRecord = ascendant1,
                AscendantAffix2hNameRecord = ascendant2,
                Unknown = unknown,
                StackCount = stackCount,
                RerollsUsed = rerolls,
                AffixRerollsUsed = affixRerolls,
                IsHardcore = hardcore,
            });
        }

        return items;
    }

    /// <summary>
    /// Imports a file into the collection, skipping items already present.
    ///
    /// Duplicate detection is base record + seed, the same identity the loot importer uses and
    /// the same one Grim Dawn uses for a rolled item. Re-importing the same file is therefore a
    /// no-op rather than a way to duplicate a collection.
    /// </summary>
    /// <param name="Refused">
    /// Items the collection does not take at all — components, crafting materials, quest items,
    /// stacks. See <see cref="ItemAdmission"/>.
    /// </param>
    public static (int Imported, int Skipped, int Refused) Import(string databasePath, string file, string? mod = null) {
        var items = Parse(File.ReadAllBytes(file));

        using var store = new LootStore(databasePath);
        var imported = 0;
        var skipped = 0;
        var refused = 0;

        foreach (var item in items) {
            var looted = new LootedItem {
                Mod = mod ?? "",
                IsHardcore = item.IsHardcore,
                BaseRecord = item.BaseRecord,
                PrefixRecord = Empty(item.PrefixRecord),
                SuffixRecord = Empty(item.SuffixRecord),
                Seed = item.Seed,
                RerollsUsed = item.RerollsUsed,
                ModifierRecord = Empty(item.ModifierRecord),
                MateriaRecord = Empty(item.MateriaRecord),
                RelicCompletionBonusRecord = Empty(item.RelicCompletionBonusRecord),
                RelicSeed = item.RelicSeed,
                EnchantmentRecord = Empty(item.EnchantmentRecord),
                EnchantmentSeed = item.EnchantmentSeed,
                TransmuteRecord = Empty(item.TransmuteRecord),
                AscendantAffixNameRecord = Empty(item.AscendantAffixNameRecord),
                AscendantAffix2hNameRecord = Empty(item.AscendantAffix2hNameRecord),
                AffixRerollsUsed = item.AffixRerollsUsed,
                StackCount = Math.Max(1, item.StackCount),
                // No tooltip: the exporting tool never had one to give. Names come from
                // ItemTemplate instead, and 'iagd stats' fills in the rolled values.
                Stats = [],
            };

            // The hook refuses components, crafting materials, quest items and stacks as they
            // are looted; a file full of them must not get in through the side door.
            if (!ItemAdmission.IsCollectable(looted.BaseRecord, looted.StackCount)) { refused++; continue; }
            if (store.Exists(looted)) { skipped++; continue; }
            store.Insert(looted);
            imported++;
        }

        return (imported, skipped, refused);
    }

    private static string? Empty(string value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Reads the collection out of the database in export shape.</summary>
    private static List<ExportedItem> Read(string databasePath, bool? hardcoreOnly, string? mod) {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (hardcoreOnly is not null) conditions.Add(hardcoreOnly.Value ? "IsHardcore" : "NOT IsHardcore");
        if (mod is not null) {
            conditions.Add(mod.Length == 0 ? "(Mod IS NULL OR Mod = '')" : "LOWER(Mod) = LOWER($mod)");
            if (mod.Length > 0) command.Parameters.AddWithValue("$mod", mod);
        }

        command.CommandText = $"""
            SELECT baserecord, PrefixRecord, SuffixRecord, ModifierRecord, TransmuteRecord, Seed,
                   MateriaRecord, RelicCompletionBonusRecord, RelicSeed, EnchantmentRecord,
                   MateriaCombines, EnchantmentSeed, AscendantAffixNameRecord,
                   AscendantAffix2hNameRecord, UNKNOWN, StackCount, RerollsUsed,
                   AffixRerollsUsed, IsHardcore
            FROM PlayerItem
            {(conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "")}
            ORDER BY Id;
            """;

        var items = new List<ExportedItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) {
            string Text(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i);
            uint Number(int i) => reader.IsDBNull(i) ? 0 : unchecked((uint)reader.GetInt64(i));

            items.Add(new ExportedItem {
                BaseRecord = Text(0),
                PrefixRecord = Text(1),
                SuffixRecord = Text(2),
                ModifierRecord = Text(3),
                TransmuteRecord = Text(4),
                Seed = Number(5),
                MateriaRecord = Text(6),
                RelicCompletionBonusRecord = Text(7),
                RelicSeed = Number(8),
                EnchantmentRecord = Text(9),
                MateriaCombines = Number(10),
                EnchantmentSeed = Number(11),
                AscendantAffixNameRecord = Text(12),
                AscendantAffix2hNameRecord = Text(13),
                Unknown = Number(14),
                StackCount = Number(15),
                RerollsUsed = Number(16),
                AffixRerollsUsed = Number(17),
                IsHardcore = !reader.IsDBNull(18) && reader.GetInt64(18) != 0,
            });
        }
        return items;
    }
}
