using IAGrim.Parser.Stash;
using IAGrim.StashFile;

namespace IAGrim.Core.Stash;

/// <summary>One item sitting in the shared stash.</summary>
public sealed record StashItem {
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
    public string AscendantRecord { get; init; } = "";
    public string AscendantRecord2H { get; init; } = "";
    public uint Unknown { get; init; }
    public uint EnchantmentSeed { get; init; }
    public uint MateriaCombines { get; init; }
    public uint StackCount { get; init; }
    public uint Rerolls { get; init; }
}

/// <summary>A transfer stash file, as read.</summary>
public sealed record StashContents(
    uint Version, string ModLabel, bool IsExpansion1, IReadOnlyList<IReadOnlyList<StashItem>> Tabs) {
    public IEnumerable<StashItem> AllItems => Tabs.SelectMany(t => t);
    public int ItemCount => Tabs.Sum(t => t.Count);
}

/// <summary>A transfer stash file on disk, and what its name says about it.</summary>
public sealed record TransferStashFile(string Path, bool IsHardcore, string Mod, string Downgrade);

/// <summary>
/// Reads Grim Dawn's shared "transfer" stash.
///
/// **Reading only.** An earlier note in BACKLOG.md described writing this file as a second
/// deposit path alongside the hook — that was wrong. Upstream's <c>TransferStashService.Deposit</c>
/// writes CSV files for the hook to collect, exactly as this port already does, and nothing in
/// upstream ever writes <c>transfer.gst</c>. There is no write path to port, which is a relief:
/// a malformed transfer file loses items.
///
/// What reading buys is migration — importing a stash the player already has, without replaying
/// it — and detecting which mod a transfer file belongs to.
///
/// **This handles a version upstream cannot.** Upstream's <c>Stash.Read</c> accepts versions 4,
/// 5, 8 and 9. The game on this machine writes **version 11**, so upstream's reader rejects the
/// file outright; its most recent commit predates the game build. Rather than wait, the field
/// walk is done here, using upstream's own crypto and block-framing primitives
/// (<c>GDCryptoDataBuffer</c>, <c>Block</c>) — which are the genuinely hard part — with a
/// version-driven item layout on top.
///
/// The v11 delta was established empirically and is narrow: **one extra uint per item**, after
/// <c>Rerolls</c> and before the grid offsets. Evidence, against the real 29 KB file: with the
/// extra field, all 10 tabs and 222 items parse, every one of the 222 base records is a
/// well-formed <c>records/….dbr</c> path, and every block terminator lands exactly where the
/// framing says it should. Without it, or with two, the first tab's terminator is already
/// misaligned. A one-field desync cannot produce 222 valid record strings by luck.
/// </summary>
public static class TransferStash {
    /// <summary>Versions whose item layout is known.</summary>
    private static readonly uint[] KnownVersions = [4, 5, 8, 9, 11];

    /// <summary>
    /// Transfer files upstream looks for. Hardcore is decided by the extension, and the
    /// "downgrade" variants are stashes saved with an expansion disabled — a player who owns
    /// Forgotten Gods but is playing without it has a separate stash, and merging the two would
    /// be wrong.
    /// </summary>
    private static readonly (string File, string Downgrade)[] TransferFileNames = [
        ("transfer.gst", ""),               // softcore
        ("transfer.gsh", ""),               // hardcore
        ("transfer.bst", "NoExpansions"),   // softcore, AoM and FG disabled
        ("transfer.bsh", "NoExpansions"),   // hardcore, AoM and FG disabled
        ("transfer.cst", "AoM"),            // softcore, FG disabled
        ("transfer.csh", "AoM"),            // hardcore, FG disabled
        ("transfer.dst", "Asterkarn"),      // softcore, Asterkarn disabled
        ("transfer.dsh", "Asterkarn"),      // hardcore, Asterkarn disabled
    ];

    /// <summary>
    /// Locates transfer files under the save directory. Mods keep theirs in a subdirectory named
    /// after the mod, which is where the mod name comes from.
    /// </summary>
    /// <param name="includeDowngrades">
    /// Whether to include stashes saved with an expansion disabled. Off by default: they are a
    /// separate stash the player is not currently using, and importing them silently mixes
    /// collections.
    /// </param>
    public static IReadOnlyList<TransferStashFile> Find(string savePath, bool includeDowngrades = false) {
        if (!Directory.Exists(savePath)) return [];

        var found = new List<TransferStashFile>();

        void Consider(string directory, string mod) {
            foreach (var (name, downgrade) in TransferFileNames) {
                if (!includeDowngrades && downgrade.Length > 0) continue;

                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;

                // Upstream's rule: hardcore is the trailing 'h'.
                var hardcore = name.EndsWith("h", StringComparison.OrdinalIgnoreCase);
                found.Add(new TransferStashFile(path, hardcore, mod, downgrade));
            }
        }

        Consider(savePath, "");
        foreach (var directory in Directory.GetDirectories(savePath)) {
            var name = Path.GetFileName(directory);
            // "main" holds character saves, not a mod's stash.
            if (string.Equals(name, "main", StringComparison.OrdinalIgnoreCase)) continue;
            Consider(directory, name);
        }

        return found;
    }

    /// <summary>
    /// Reads a transfer stash, or returns null when the file cannot be understood.
    ///
    /// Null rather than an exception for an unknown version: a new Grim Dawn release changing
    /// the format is expected, not exceptional, and the caller should say so plainly rather
    /// than crash.
    /// </summary>
    public static StashContents? Read(string path, out string? error) {
        error = null;

        GDCryptoDataBuffer crypto;
        try {
            crypto = new GDCryptoDataBuffer(DataBuffer.ReadBytesFromDisk(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            error = ex.Message;
            return null;
        }

        // Header. This is upstream's Stash.Read, minus its version gate.
        if (!crypto.ReadCryptoKey()) { error = "not a Grim Dawn save file (no crypto key)"; return null; }

        if (!crypto.ReadCryptoUInt(out var unknown1) || unknown1 != 2u) {
            error = $"expected the file to start with 2, got {unknown1}; possibly corrupted";
            return null;
        }

        if (!Block.ReadStart(out var outer, crypto) || outer.Result != 18u) {
            error = "unexpected block header";
            return null;
        }

        if (!crypto.ReadCryptoUInt(out var version) || !KnownVersions.Contains(version)) {
            error = $"stash file version {version} is not supported "
                    + $"(known: {string.Join(", ", KnownVersions)})";
            return null;
        }

        if (!crypto.ReadNextCryptoUInt(out var unknown2) || unknown2 != 0u) {
            error = "unexpected header field";
            return null;
        }

        if (!crypto.ReadCryptoString(out var modLabel)) { error = "could not read the mod label"; return null; }

        var isExpansion1 = false;
        if (version >= 5 && !crypto.ReadCryptoBool(out isExpansion1)) {
            error = "could not read the expansion flag";
            return null;
        }

        if (!crypto.ReadCryptoUInt(out var tabCount) || tabCount > 100) {
            error = $"implausible stash tab count ({tabCount})";
            return null;
        }

        var tabs = new List<IReadOnlyList<StashItem>>();
        for (var t = 0; t < tabCount; t++) {
            var tab = ReadTab(crypto, version, t, ref error);
            if (tab is null) return null;
            tabs.Add(tab);
        }

        // The outer terminator is the proof that every field above was consumed at the right
        // width. It is checked rather than ignored precisely because a layout mistake shows up
        // here and nowhere else.
        if (!outer.ReadEnd(crypto)) {
            error = "the file did not end where its structure said it should; "
                    + "the layout for this version may have changed";
            return null;
        }

        return new StashContents(version, modLabel ?? "", isExpansion1, tabs);
    }

    private static List<StashItem>? ReadTab(
        GDCryptoDataBuffer crypto, uint version, int index, ref string? error) {

        if (!Block.ReadStart(out var block, crypto)) {
            error = $"could not read stash tab {index}";
            return null;
        }

        if (!crypto.ReadCryptoUInt(out _) ||          // width
            !crypto.ReadCryptoUInt(out _) ||          // height
            !crypto.ReadCryptoUInt(out var itemCount) || itemCount > 1000) {
            error = $"stash tab {index} has an implausible size";
            return null;
        }

        var items = new List<StashItem>((int)itemCount);
        for (var i = 0; i < itemCount; i++) {
            var item = ReadItem(crypto, version);
            if (item is null) {
                error = $"could not read item {i} of stash tab {index}";
                return null;
            }
            items.Add(item);
        }

        // Tab decoration — border, symbol, colours and the player's name for the tab.
        if (version >= 9) {
            crypto.ReadCryptoUInt(out _);
            crypto.ReadCryptoUInt(out _);
            crypto.ReadCryptoUInt(out _);
            crypto.ReadCryptoUInt(out _);
            crypto.ReadCryptoWString(out _);
        }

        if (!block.ReadEnd(crypto)) {
            error = $"stash tab {index} did not end where its structure said it should";
            return null;
        }

        return items;
    }

    private static StashItem? ReadItem(GDCryptoDataBuffer crypto, uint version) {
        var ok = true;

        string S() {
            ok &= crypto.ReadCryptoString(out var value);
            return value ?? "";
        }
        uint U() {
            ok &= crypto.ReadCryptoUInt(out var value);
            return value;
        }

        var item = new StashItem {
            BaseRecord = S(),
            PrefixRecord = S(),
            SuffixRecord = S(),
            ModifierRecord = S(),
            TransmuteRecord = S(),
            Seed = U(),
            MateriaRecord = S(),
            RelicCompletionBonusRecord = S(),
            RelicSeed = U(),
            EnchantmentRecord = S(),
            // Ascendant affixes arrived with Fangs of Asterkarn, in version 8.
            AscendantRecord = version >= 8 ? S() : "",
            AscendantRecord2H = version >= 8 ? S() : "",
            Unknown = U(),
            EnchantmentSeed = U(),
            MateriaCombines = U(),
            StackCount = U(),
            Rerolls = version >= 8 ? U() : 0,
        };

        // The v11 addition. Its meaning is unknown — it is zero on every item in the sample
        // available — so it is consumed and discarded rather than guessed at.
        if (version >= 11) U();

        U();   // grid X offset, a float in disguise
        U();   // grid Y offset

        return ok ? item : null;
    }
}
