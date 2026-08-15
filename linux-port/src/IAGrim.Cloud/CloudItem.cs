namespace IAGrim.Cloud;

/// <summary>
/// A collection item as the sync path sees it — the cloud-relevant surface of upstream's
/// <c>PlayerItem</c>.
///
/// Not <see cref="IAGrim.Platform.LootedItem"/>, which describes an item arriving *from the
/// game* and stops at the fields the hook sends. Sync also needs the computed ones (name,
/// rarity, level requirement) because the server stores them for its own web view, and the two
/// identity columns that only exist for this feature: <see cref="CloudId"/> and
/// <see cref="IsCloudSynchronized"/>.
/// </summary>
public sealed class CloudItem {
    /// <summary>The local row id, <c>PlayerItem.Id</c>. Never leaves this machine.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The cloud identity, <c>PlayerItem.cloudid</c>.
    ///
    /// Assigned when the item is created rather than at upload time — see
    /// <c>CloudIdentity.New</c> — so it is stable and persisted before the item can be pushed
    /// over the live socket. An item pushed without one, then uploaded with one, is the same
    /// item twice on the receiving machine.
    /// </summary>
    public string? CloudId { get; set; }

    /// <summary>
    /// <c>PlayerItem.cloud_hassync</c>. Upstream stores 0/1/NULL and treats anything non-zero as
    /// synchronised; NULL and 0 both mean "still to upload".
    /// </summary>
    public bool IsCloudSynchronized { get; set; }

    public string? BaseRecord { get; set; }
    public string? PrefixRecord { get; set; }
    public string? SuffixRecord { get; set; }
    public string? ModifierRecord { get; set; }
    public string? TransmuteRecord { get; set; }
    public string? MateriaRecord { get; set; }
    public string? RelicCompletionBonusRecord { get; set; }
    public string? EnchantmentRecord { get; set; }
    public string? AscendantAffixNameRecord { get; set; }
    public string? AscendantAffix2hNameRecord { get; set; }

    public long Seed { get; set; }
    public long RelicSeed { get; set; }
    public long EnchantmentSeed { get; set; }
    public long MateriaCombines { get; set; }
    public long StackCount { get; set; }
    public long RerollsUsed { get; set; }
    public long AffixRerollsUsed { get; set; }

    /// <summary>Milliseconds since the epoch. Null on rows written before the column existed.</summary>
    public long? CreationDate { get; set; }

    public long PrefixRarity { get; set; }
    public string? Name { get; set; }
    public string? NameLowercase { get; set; }
    public string? Rarity { get; set; }

    /// <summary>
    /// <c>PlayerItem.levelrequirement</c>. Stored as a REAL and read as one, because upstream's
    /// column is a double and truncating it on read would lose a level requirement of 94.5 that
    /// the Windows tool wrote.
    /// </summary>
    public double LevelRequirement { get; set; }

    public string? Mod { get; set; }
    public bool IsHardcore { get; set; }
}
