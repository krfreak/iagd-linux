using IAGrim.Cloud.Dto;
using Xunit;

namespace IAGrim.Cloud.Tests;

/// <summary>
/// The conversion between a stored item and the wire.
///
/// This is the file that decides whether a collection survives a round trip through somebody
/// else's database, so the assertions are per-field rather than "the object looks right": a
/// field silently dropped here is a field the owner loses on their other machine and every
/// buddy loses permanently, and nothing upstream of it complains.
/// </summary>
public class ItemConverterTests {
    /// <summary>An item with every field distinct, so a mix-up cannot pass.</summary>
    private static CloudItem Sample() => new() {
        Id = 4711,
        CloudId = "aaaaaaaabbbbccccddddeeeeffff0001",
        IsCloudSynchronized = false,
        BaseRecord = "records/items/gearweapons/guns1h/c030_gun1h.dbr",
        PrefixRecord = "records/items/lootaffixes/prefix/a01.dbr",
        SuffixRecord = "records/items/lootaffixes/suffix/b02.dbr",
        ModifierRecord = "records/items/lootaffixes/modifier/c03.dbr",
        TransmuteRecord = "records/items/transmute/d04.dbr",
        MateriaRecord = "records/items/materia/e05.dbr",
        RelicCompletionBonusRecord = "records/items/relic/f06.dbr",
        EnchantmentRecord = "records/items/enchant/g07.dbr",
        AscendantAffixNameRecord = "records/items/asc/h08.dbr",
        AscendantAffix2hNameRecord = "records/items/asc/i09.dbr",
        Seed = 111,
        RelicSeed = 222,
        EnchantmentSeed = 333,
        MateriaCombines = 444,
        StackCount = 5,
        RerollsUsed = 6,
        AffixRerollsUsed = 7,
        CreationDate = 1_700_000_000_000,
        PrefixRarity = 3,
        Name = "Mythical Plagueborne Revolver",
        NameLowercase = "mythical plagueborne revolver",
        Rarity = "Blue",
        LevelRequirement = 94,
        Mod = "grimarillion",
        IsHardcore = true,
    };

    [Fact]
    public void ToUpload_carries_every_field() {
        var dto = ItemConverter.ToUpload(Sample());

        Assert.Equal("aaaaaaaabbbbccccddddeeeeffff0001", dto.Id);
        Assert.Equal("grimarillion", dto.Mod);
        Assert.True(dto.IsHardcore);

        Assert.Equal("records/items/gearweapons/guns1h/c030_gun1h.dbr", dto.BaseRecord);
        Assert.Equal("records/items/lootaffixes/prefix/a01.dbr", dto.PrefixRecord);
        Assert.Equal("records/items/lootaffixes/suffix/b02.dbr", dto.SuffixRecord);
        Assert.Equal("records/items/lootaffixes/modifier/c03.dbr", dto.ModifierRecord);
        Assert.Equal("records/items/transmute/d04.dbr", dto.TransmuteRecord);
        Assert.Equal("records/items/materia/e05.dbr", dto.MateriaRecord);
        Assert.Equal("records/items/relic/f06.dbr", dto.RelicCompletionBonusRecord);
        Assert.Equal("records/items/enchant/g07.dbr", dto.EnchantmentRecord);
        Assert.Equal("records/items/asc/h08.dbr", dto.AscendantAffixNameRecord);
        Assert.Equal("records/items/asc/i09.dbr", dto.AscendantAffix2hNameRecord);

        Assert.Equal(111, dto.Seed);
        Assert.Equal(222, dto.RelicSeed);
        Assert.Equal(333, dto.EnchantmentSeed);
        Assert.Equal(444, dto.MateriaCombines);
        Assert.Equal(5, dto.StackCount);
        Assert.Equal(6, dto.RerollsUsed);
        Assert.Equal(7, dto.AffixRerollsUsed);

        Assert.Equal(1_700_000_000_000, dto.CreatedAt);
        Assert.Equal(3, dto.PrefixRarity);
        Assert.Equal("Mythical Plagueborne Revolver", dto.Name);
        Assert.Equal("mythical plagueborne revolver", dto.NameLowercase);
        Assert.Equal("Blue", dto.Rarity);
        Assert.Equal(94, dto.LevelRequirement);
    }

    /// <summary>
    /// Optional records go up as the empty string, never null. Upstream's rows have no NULLs
    /// here and its own SQL depends on that (the Components filter is <c>MateriaRecord = ''</c>),
    /// so a null sent up comes back as a null on the next machine and quietly drops that item
    /// out of a filter.
    /// </summary>
    [Fact]
    public void ToUpload_turns_missing_records_into_empty_strings() {
        var dto = ItemConverter.ToUpload(new CloudItem {
            CloudId = "aaaaaaaabbbbccccddddeeeeffff0002",
            BaseRecord = "records/items/x.dbr",
            StackCount = 1,
        });

        Assert.Equal("", dto.PrefixRecord);
        Assert.Equal("", dto.SuffixRecord);
        Assert.Equal("", dto.ModifierRecord);
        Assert.Equal("", dto.TransmuteRecord);
        Assert.Equal("", dto.MateriaRecord);
        Assert.Equal("", dto.RelicCompletionBonusRecord);
        Assert.Equal("", dto.EnchantmentRecord);
        Assert.Equal("", dto.AscendantAffixNameRecord);
        Assert.Equal("", dto.AscendantAffix2hNameRecord);
    }

    /// <summary>
    /// A base record is the one string that stays null-safe rather than being invented: an item
    /// without one is not uploadable, and the server says so (<c>len(baseRecord) &lt; 6</c>).
    /// </summary>
    [Fact]
    public void ToUpload_leaves_a_missing_base_record_empty() {
        var dto = ItemConverter.ToUpload(new CloudItem { CloudId = new string('a', 32) });
        Assert.Equal("", dto.BaseRecord);
    }

    /// <summary>
    /// The stack count floor. The server rejects a *whole batch* if one item has a non-positive
    /// stack count, so a single 0-stack row would wedge every upload behind it — permanently,
    /// since the batch never succeeds and the items never get marked synchronised.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(12, 12)]
    public void ToUpload_floors_the_stack_count_at_one(long stored, long sent) {
        var dto = ItemConverter.ToUpload(new CloudItem {
            CloudId = new string('a', 32), BaseRecord = "records/x.dbr", StackCount = stored,
        });
        Assert.Equal(sent, dto.StackCount);
    }

    /// <summary>Truncated, not rounded — upstream casts through float to int.</summary>
    [Theory]
    [InlineData(94.0, 94)]
    [InlineData(94.9, 94)]
    [InlineData(0.0, 0)]
    public void ToUpload_truncates_the_level_requirement(double stored, int sent) {
        var dto = ItemConverter.ToUpload(new CloudItem {
            CloudId = new string('a', 32), BaseRecord = "records/x.dbr", LevelRequirement = stored,
        });
        Assert.Equal(sent, dto.LevelRequirement);
    }

    /// <summary>
    /// An item with no creation date gets "now", in milliseconds. Seconds here would read as
    /// January 1970 everywhere else and land inside every "looted recently" window.
    /// </summary>
    [Fact]
    public void ToUpload_substitutes_now_in_milliseconds_for_a_missing_creation_date() {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dto = ItemConverter.ToUpload(new CloudItem {
            CloudId = new string('a', 32), BaseRecord = "records/x.dbr", CreationDate = null,
        });
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.InRange(dto.CreatedAt, before, after);
    }

    /// <summary>
    /// An item arriving from the cloud is already synchronised. Without the flag the next upload
    /// pass sends it straight back, and it returns as a second item under a new cloud id.
    /// </summary>
    [Fact]
    public void ToPlayerItem_marks_the_item_synchronised() {
        var item = ItemConverter.ToPlayerItem(new CloudItemDto { Id = new string('a', 32) });
        Assert.True(item.IsCloudSynchronized);
    }

    /// <summary>
    /// The full round trip. Everything the protocol carries has to come back identical, because
    /// this is exactly what happens to an item that leaves one machine and lands on another.
    /// </summary>
    [Fact]
    public void A_round_trip_through_the_wire_preserves_the_item() {
        var original = Sample();
        var returned = ItemConverter.ToPlayerItem(ItemConverter.ToUpload(original));

        Assert.Equal(original.CloudId, returned.CloudId);
        Assert.Equal(original.BaseRecord, returned.BaseRecord);
        Assert.Equal(original.PrefixRecord, returned.PrefixRecord);
        Assert.Equal(original.SuffixRecord, returned.SuffixRecord);
        Assert.Equal(original.ModifierRecord, returned.ModifierRecord);
        Assert.Equal(original.TransmuteRecord, returned.TransmuteRecord);
        Assert.Equal(original.MateriaRecord, returned.MateriaRecord);
        Assert.Equal(original.RelicCompletionBonusRecord, returned.RelicCompletionBonusRecord);
        Assert.Equal(original.EnchantmentRecord, returned.EnchantmentRecord);
        Assert.Equal(original.AscendantAffixNameRecord, returned.AscendantAffixNameRecord);
        Assert.Equal(original.AscendantAffix2hNameRecord, returned.AscendantAffix2hNameRecord);
        Assert.Equal(original.Seed, returned.Seed);
        Assert.Equal(original.RelicSeed, returned.RelicSeed);
        Assert.Equal(original.EnchantmentSeed, returned.EnchantmentSeed);
        Assert.Equal(original.MateriaCombines, returned.MateriaCombines);
        Assert.Equal(original.StackCount, returned.StackCount);
        Assert.Equal(original.RerollsUsed, returned.RerollsUsed);
        Assert.Equal(original.AffixRerollsUsed, returned.AffixRerollsUsed);
        Assert.Equal(original.CreationDate, returned.CreationDate);
        Assert.Equal(original.Name, returned.Name);
        Assert.Equal(original.NameLowercase, returned.NameLowercase);
        Assert.Equal(original.Rarity, returned.Rarity);
        Assert.Equal(original.LevelRequirement, returned.LevelRequirement);
        Assert.Equal(original.Mod, returned.Mod);
        Assert.Equal(original.IsHardcore, returned.IsHardcore);
    }

    /// <summary>
    /// What the protocol does *not* carry, stated so a later change has to argue with a test.
    /// The local row id is this machine's business, and <c>UNKNOWN</c> is a column upstream
    /// neither uploads nor reads back even though the server round-trips a field of that name.
    /// </summary>
    [Fact]
    public void A_round_trip_does_not_carry_the_local_row_id() {
        var returned = ItemConverter.ToPlayerItem(ItemConverter.ToUpload(Sample()));
        Assert.Equal(0, returned.Id);
    }

    /// <summary>
    /// <b>PrefixRarity is uploaded and never read back.</b> Upstream's <c>ToUpload</c> sets it,
    /// the server stores and returns it, and <c>ToPlayerItem</c> has no line for it — so an item
    /// that arrives from the cloud lands with a prefix rarity of 0 whatever it had when it left.
    ///
    /// That is upstream's behaviour, not a bug this port gets to fix: "at least N rare affixes"
    /// is a filter over a local column, and a port that filled the value in would return items
    /// the Windows tool does not, on the same collection. Asserted so the asymmetry is a
    /// decision on the record rather than an oversight waiting to be "corrected".
    /// </summary>
    [Fact]
    public void PrefixRarity_goes_up_but_never_comes_back_down() {
        var dto = ItemConverter.ToUpload(Sample());
        Assert.Equal(3, dto.PrefixRarity);

        Assert.Equal(0, ItemConverter.ToPlayerItem(dto).PrefixRarity);
    }

    [Fact]
    public void Cloud_ids_are_long_enough_for_the_server_to_accept() {
        var id = CloudIdentity.New();

        Assert.Equal(32, id.Length);
        Assert.DoesNotContain("-", id);
        Assert.True(CloudIdentity.IsAcceptable(id));

        Assert.False(CloudIdentity.IsAcceptable(null));
        Assert.False(CloudIdentity.IsAcceptable(""));
        Assert.False(CloudIdentity.IsAcceptable(new string('a', 31)));
        Assert.True(CloudIdentity.IsAcceptable(Guid.NewGuid().ToString()));
    }
}
