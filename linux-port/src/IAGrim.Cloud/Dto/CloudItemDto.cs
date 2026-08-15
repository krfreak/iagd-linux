namespace IAGrim.Cloud.Dto;

/// <summary>
/// One item, as it travels to and from the backup service — upstream's
/// <c>Backup/Cloud/Dto/CloudItemDto.cs</c>.
///
/// **The property order is load-bearing for the tests, not for the server.** Upstream
/// serialises this type with Newtonsoft, which emits properties in declaration order; the
/// parity tests compare the produced JSON against a recorded upstream payload, so keeping the
/// order means a field added upstream shows up as a diff rather than as a silent reordering
/// nobody reads.
///
/// The defaults matter more than they look. Every record is <c>""</c> rather than null because
/// that is what upstream sends, and the server stores what it is given: a null record would come
/// back as a null record on the owner's other machine and on every buddy subscribed to them.
/// </summary>
public sealed class CloudItemDto {
    /// <summary>The cloud identity — <c>PlayerItem.cloudid</c>. The server rejects anything shorter than 32 characters.</summary>
    public string? Id { get; set; }

    public string? Mod { get; set; }
    public bool IsHardcore { get; set; }

    public string BaseRecord { get; set; } = "";
    public string PrefixRecord { get; set; } = "";
    public string SuffixRecord { get; set; } = "";
    public string ModifierRecord { get; set; } = "";
    public string TransmuteRecord { get; set; } = "";
    public string MateriaRecord { get; set; } = "";
    public string RelicCompletionBonusRecord { get; set; } = "";
    public string EnchantmentRecord { get; set; } = "";
    public string AscendantAffixNameRecord { get; set; } = "";
    public string AscendantAffix2hNameRecord { get; set; } = "";

    public long Seed { get; set; }
    public long RelicSeed { get; set; }
    public long EnchantmentSeed { get; set; }
    public long MateriaCombines { get; set; }
    public long StackCount { get; set; } = 1;
    public long RerollsUsed { get; set; }
    public long AffixRerollsUsed { get; set; }

    /// <summary>Milliseconds since the epoch, matching <c>PlayerItem.created_at</c>.</summary>
    public long CreatedAt { get; set; }

    public long PrefixRarity { get; set; }

    public string? Name { get; set; }
    public string? NameLowercase { get; set; }
    public string? Rarity { get; set; }
    public int LevelRequirement { get; set; }
}
