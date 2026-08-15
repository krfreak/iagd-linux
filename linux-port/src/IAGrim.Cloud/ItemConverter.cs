using IAGrim.Cloud.Dto;

namespace IAGrim.Cloud;

/// <summary>
/// Between a stored item and the wire — upstream's <c>Backup/Cloud/Util/ItemConverter.cs</c>,
/// field for field.
///
/// This is the file where a mistake mangles a collection, because both directions are lossy in
/// ways that are invisible until much later. Three things upstream does that look like details
/// and are not:
///
///   * <b>records become <c>""</c>, never null</b>, on the way up. The server stores what it is
///     handed and every other machine reads it back.
///   * <b><see cref="CloudItemDto.StackCount"/> is floored at 1</b> going up. The server rejects
///     a batch outright if any item has a non-positive stack count, so one 0-stack row would
///     block every upload behind it forever.
///   * <b>an item coming down is already synchronised.</b> Without that flag it is uploaded
///     straight back, and the copy that returns has a different cloud id.
///
/// What upstream does *not* map is as load-bearing: <c>UNKNOWN</c> and <c>MateriaCombines</c>
/// come back down from the server (<c>unknown</c>, <c>materiaCombines</c>) and are dropped on
/// the floor, <c>Mod</c> and <c>IsHardcore</c> survive, and the local row id is never sent.
/// </summary>
public static class ItemConverter {
    public static CloudItemDto ToUpload(CloudItem item) => new() {
        BaseRecord = item.BaseRecord ?? "",
        EnchantmentRecord = item.EnchantmentRecord ?? "",
        EnchantmentSeed = item.EnchantmentSeed,
        IsHardcore = item.IsHardcore,
        MateriaCombines = item.MateriaCombines,
        MateriaRecord = item.MateriaRecord ?? "",
        Mod = item.Mod,
        ModifierRecord = item.ModifierRecord ?? "",
        PrefixRecord = item.PrefixRecord ?? "",
        RelicCompletionBonusRecord = item.RelicCompletionBonusRecord ?? "",
        RelicSeed = item.RelicSeed,
        Seed = item.Seed,
        StackCount = Math.Max(item.StackCount, 1),
        SuffixRecord = item.SuffixRecord ?? "",
        TransmuteRecord = item.TransmuteRecord ?? "",
        Id = item.CloudId,
        CreatedAt = item.CreationDate ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        // Upstream reads pi.MinimumLevel, which is a float view over the same column, and casts
        // to int. Truncation, not rounding: 94.9 goes up as 94.
        LevelRequirement = (int)(float)item.LevelRequirement,
        Name = item.Name,
        NameLowercase = item.NameLowercase,
        Rarity = item.Rarity,
        PrefixRarity = item.PrefixRarity,
        AscendantAffix2hNameRecord = item.AscendantAffix2hNameRecord ?? "",
        RerollsUsed = item.RerollsUsed,
        AffixRerollsUsed = item.AffixRerollsUsed,
        AscendantAffixNameRecord = item.AscendantAffixNameRecord ?? "",
    };

    public static CloudItem ToPlayerItem(CloudItemDto dto) => new() {
        BaseRecord = dto.BaseRecord,
        EnchantmentRecord = dto.EnchantmentRecord,
        EnchantmentSeed = dto.EnchantmentSeed,
        IsHardcore = dto.IsHardcore,
        MateriaCombines = dto.MateriaCombines,
        MateriaRecord = dto.MateriaRecord,
        Mod = dto.Mod,
        ModifierRecord = dto.ModifierRecord,
        PrefixRecord = dto.PrefixRecord,
        RelicCompletionBonusRecord = dto.RelicCompletionBonusRecord,
        RelicSeed = dto.RelicSeed,
        Seed = dto.Seed,
        StackCount = dto.StackCount,
        SuffixRecord = dto.SuffixRecord,
        TransmuteRecord = dto.TransmuteRecord,
        CloudId = dto.Id,
        IsCloudSynchronized = true,
        CreationDate = dto.CreatedAt,
        LevelRequirement = dto.LevelRequirement,
        Name = dto.Name,
        NameLowercase = dto.NameLowercase,
        Rarity = dto.Rarity,
        AscendantAffix2hNameRecord = dto.AscendantAffix2hNameRecord,
        AscendantAffixNameRecord = dto.AscendantAffixNameRecord,
        RerollsUsed = dto.RerollsUsed,
        AffixRerollsUsed = dto.AffixRerollsUsed,
    };
}
