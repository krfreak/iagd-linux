namespace IAGrim.DbDoctor;

/// <summary>
/// The predicates the diagnosis counts and the repair acts on — one copy, so that what gets
/// reported and what gets deleted can never drift apart.
/// </summary>
internal static class Sql {
    /// <summary>
    /// Upstream's item equality, from <c>PlayerItemDaoImpl.Exists</c> — base record, both
    /// affixes, the socketed component, the modifier and the seed. Copied from
    /// <c>LootStore.Exists</c> rather than invented, because "the same item" has to mean here
    /// exactly what it means to the importer that refuses a duplicate.
    /// </summary>
    public const string IdentityKey = """
        baserecord, IFNULL(PrefixRecord,''), IFNULL(SuffixRecord,''),
        IFNULL(MateriaRecord,''), IFNULL(ModifierRecord,''), Seed
        """;

    /// <summary>
    /// The copies beyond the first: every row that is not the oldest of its identity group.
    ///
    /// Oldest rather than newest deliberately. The lowest Id is the row that has been here
    /// longest, so it is the one the server is most likely to already know under the id it
    /// still holds, and the one whose tooltip and computed stats are most likely to be filled
    /// in. Keeping it means the deletions we send are for the copies that arrived later.
    /// </summary>
    public const string RedundantRows =
        $"Id NOT IN (SELECT MIN(Id) FROM PlayerItem GROUP BY {IdentityKey})";

    /// <summary>The part of a record path before the first slash: `records` for the base game.</summary>
    public const string RecordRoot =
        "CASE WHEN instr(baserecord,'/') > 0 THEN substr(baserecord,1,instr(baserecord,'/')-1) ELSE baserecord END";

    /// <summary>An item that came from somewhere other than the base game but is not tagged so.</summary>
    public const string UntaggedModItem =
        $"(Mod IS NULL OR Mod = '') AND {RecordRoot} <> 'records'";

    /// <summary>
    /// Upstream writes milliseconds. A value small enough to be a plausible seconds-since-epoch
    /// date was written by something that used the wrong unit; it renders as 1970.
    /// </summary>
    public const string SecondsScaleTimestamp = "created_at BETWEEN 1 AND 100000000000";

    /// <summary>
    /// The server's array limit, and so the size of one deletion request — <c>BatchUtil</c>'s
    /// constant, repeated here rather than referenced because this tool does not depend on the
    /// cloud assembly.
    /// </summary>
    public const int BatchSize = 100;
}
