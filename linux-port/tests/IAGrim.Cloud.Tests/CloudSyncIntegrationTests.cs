using IAGrim.Cloud.Data;
using IAGrim.Cloud.Dto;
using Xunit;

namespace IAGrim.Cloud.Tests;

/// <summary>
/// The protocol, against the implementation that serves it.
///
/// Everything here talks to a real instance of the backup service on loopback. That is the
/// difference between "our JSON looks like upstream's" and "the server accepts it and gives the
/// item back unchanged", and it is the only way to find the rules that live in the server rather
/// than in upstream's client: the 32-character id, the base record of at least six characters,
/// the rejection of a whole batch for one bad item, the timestamp arithmetic on a partial batch.
/// </summary>
[Collection(CloudServerCollection.Name)]
public class CloudSyncIntegrationTests {
    private readonly CloudServerFixture _server;

    public CloudSyncIntegrationTests(CloudServerFixture server) {
        _server = server;
        Skip.IfNot(server.Available, server.SkipReason);
        server.UseUris();
    }

    private static CloudItemDto Item(string? id = null, string name = "Test Revolver") => new() {
        Id = id ?? CloudIdentity.New(),
        BaseRecord = "records/items/gearweapons/guns1h/c030_gun1h.dbr",
        Seed = 1234567,
        StackCount = 1,
        CreatedAt = 1_700_000_000_000,
        Name = name,
        NameLowercase = name.ToLowerInvariant(),
        Rarity = "Blue",
        LevelRequirement = 94,
        Mod = "",
    };

    private RestService Rest() => new(_server.Client());

    [SkippableFact]
    public void An_item_survives_a_round_trip_through_the_server() {
        var sync = new CloudSyncService(Rest());
        var sent = ItemConverter.ToUpload(new CloudItem {
            CloudId = CloudIdentity.New(),
            BaseRecord = "records/items/gearweapons/guns1h/c030_gun1h.dbr",
            PrefixRecord = "records/items/lootaffixes/prefix/a01.dbr",
            SuffixRecord = "records/items/lootaffixes/suffix/b02.dbr",
            MateriaRecord = "records/items/materia/e05.dbr",
            AscendantAffixNameRecord = "records/items/asc/h08.dbr",
            AscendantAffix2hNameRecord = "records/items/asc/i09.dbr",
            Seed = 999, RelicSeed = 888, EnchantmentSeed = 777,
            StackCount = 3, RerollsUsed = 2, AffixRerollsUsed = 1,
            CreationDate = 1_700_000_000_000,
            Name = "Mythical Plagueborne Revolver",
            NameLowercase = "mythical plagueborne revolver",
            Rarity = "Blue",
            LevelRequirement = 94,
            Mod = "grimarillion",
            IsHardcore = true,
        });

        Assert.True(sync.Save([sent]));

        var back = sync.Get(0).Items!.Single(item => item.Id == sent.Id);

        // Everything the client sends and reads back has to be identical, because this is the
        // exact path an item takes to another machine.
        Assert.Equal(sent.BaseRecord, back.BaseRecord);
        Assert.Equal(sent.PrefixRecord, back.PrefixRecord);
        Assert.Equal(sent.SuffixRecord, back.SuffixRecord);
        Assert.Equal(sent.MateriaRecord, back.MateriaRecord);
        Assert.Equal(sent.AscendantAffixNameRecord, back.AscendantAffixNameRecord);
        Assert.Equal(sent.AscendantAffix2hNameRecord, back.AscendantAffix2hNameRecord);
        Assert.Equal(sent.Seed, back.Seed);
        Assert.Equal(sent.RelicSeed, back.RelicSeed);
        Assert.Equal(sent.EnchantmentSeed, back.EnchantmentSeed);
        Assert.Equal(sent.StackCount, back.StackCount);
        Assert.Equal(sent.RerollsUsed, back.RerollsUsed);
        Assert.Equal(sent.AffixRerollsUsed, back.AffixRerollsUsed);
        Assert.Equal(sent.CreatedAt, back.CreatedAt);
        Assert.Equal(sent.Name, back.Name);
        Assert.Equal(sent.NameLowercase, back.NameLowercase);
        Assert.Equal(sent.Rarity, back.Rarity);
        Assert.Equal(sent.LevelRequirement, back.LevelRequirement);
        Assert.Equal(sent.Mod, back.Mod);
        Assert.Equal(sent.IsHardcore, back.IsHardcore);
    }

    /// <summary>
    /// The timestamp is a high-water mark: asking again with the returned value yields nothing,
    /// and an item uploaded afterwards appears. Advancing it wrongly is how a machine silently
    /// stops receiving its own collection.
    /// </summary>
    [SkippableFact]
    public void The_timestamp_returned_by_a_download_excludes_what_it_returned() {
        var sync = new CloudSyncService(Rest());

        Assert.True(sync.Save([Item()]));
        var first = sync.Get(0);
        Assert.NotEmpty(first.Items!);

        var second = sync.Get(first.Timestamp);
        Assert.Empty(second.Items!);

        // The server stamps items with whole seconds, so an upload inside the same second as the
        // last download carries that second's timestamp and is not "after" it. Waiting is what
        // the cooldowns do anyway -- the smallest one is a second.
        Thread.Sleep(1100);

        var later = Item();
        Assert.True(sync.Save([later]));
        Assert.Contains(sync.Get(second.Timestamp).Items!, item => item.Id == later.Id);
    }

    /// <summary>
    /// A deletion comes back in <c>removed</c> rather than as an absence, which is the only way
    /// another machine can learn to delete its copy.
    /// </summary>
    [SkippableFact]
    public void A_deletion_is_announced_to_the_other_machines() {
        var sync = new CloudSyncService(Rest());
        var item = Item();

        Assert.True(sync.Save([item]));
        var afterUpload = sync.Get(0);
        Assert.Contains(afterUpload.Items!, i => i.Id == item.Id);

        Thread.Sleep(1100);
        Assert.True(sync.Delete([new DeleteItemDto { Id = item.Id }]));

        var afterDelete = sync.Get(afterUpload.Timestamp);
        Assert.Contains(afterDelete.Removed!, removed => removed.Id == item.Id);
        Assert.DoesNotContain(afterDelete.Items!, i => i.Id == item.Id);
    }

    /// <summary>
    /// Uploading the same id twice is a no-op rather than a duplicate. The server's
    /// <c>ON CONFLICT(id) DO NOTHING</c> is what lets the live socket and the REST upload both
    /// carry the same item without the pair of them producing two.
    /// </summary>
    [SkippableFact]
    public void Uploading_the_same_item_twice_stores_it_once() {
        var sync = new CloudSyncService(Rest());
        var item = Item(name: "Duplicated Revolver");

        Assert.True(sync.Save([item]));
        Assert.True(sync.Save([item]));

        Assert.Single(sync.Get(0).Items!, stored => stored.Id == item.Id);
    }

    /// <summary>
    /// The server's validation rules, exercised rather than assumed — these are the reasons
    /// <see cref="ItemConverter"/> floors the stack count and mints 32-character ids, and each
    /// one rejects the <b>whole batch</b>, not the offending item.
    /// </summary>
    [SkippableTheory]
    [InlineData("id too short")]
    [InlineData("base record missing")]
    [InlineData("base record too short")]
    [InlineData("non positive stack count")]
    [InlineData("non ascii record")]
    [InlineData("oversized name")]
    public void The_server_rejects_a_batch_containing_a_bad_item(string flaw) {
        var sync = new CloudSyncService(Rest());
        var good = Item();
        var bad = Item();

        switch (flaw) {
            case "id too short": bad.Id = new string('a', 31); break;
            case "base record missing": bad.BaseRecord = ""; break;
            case "base record too short": bad.BaseRecord = "x.dbr"; break;
            case "non positive stack count": bad.StackCount = 0; break;
            // Record paths are DBR file names and must be printable ASCII; item *names* may be
            // localised, and are only length-capped.
            case "non ascii record": bad.PrefixRecord = "records/items/präfix.dbr"; break;
            case "oversized name": bad.Name = new string('x', 256); break;
        }

        Assert.False(sync.Save([good, bad]));

        // And the good item in the same batch did not make it either.
        Assert.DoesNotContain(sync.Get(0).Items!, stored => stored.Id == good.Id);
    }

    /// <summary>A batch over 100 is refused, which is why everything is batched at 100.</summary>
    [SkippableFact]
    public void The_server_refuses_more_than_a_hundred_items_at_once() {
        var sync = new CloudSyncService(Rest());

        var oversized = Enumerable.Range(0, 101).Select(_ => Item()).ToList();
        Assert.False(sync.Save(oversized));

        var exactly = Enumerable.Range(0, 100).Select(_ => Item()).ToList();
        Assert.True(sync.Save(exactly));
    }

    /// <summary>
    /// A localised item name survives. The server length-caps metadata in bytes, so this also
    /// confirms the client is not escaping non-ASCII into something four times longer.
    /// </summary>
    [SkippableFact]
    public void A_localised_name_survives_the_round_trip() {
        var sync = new CloudSyncService(Rest());
        var item = Item(name: "Mythischer Räuber der Bärenklaue");
        Assert.True(sync.Save([item]));

        var back = sync.Get(0).Items!.Single(stored => stored.Id == item.Id);
        Assert.Equal("Mythischer Räuber der Bärenklaue", back.Name);
    }

    /// <summary>The cooldowns are read from the server, not hardcoded here.</summary>
    [SkippableFact]
    public void The_server_hands_out_cooldowns_for_both_modes() {
        var limits = new CloudSyncService(Rest()).GetLimitations();

        Assert.NotNull(limits.Regular);
        Assert.NotNull(limits.MultiUsage);
        Assert.True(limits.Regular!.Upload > 0);
        Assert.True(limits.Regular.Download > 0);
        Assert.True(limits.Regular.Delete > 0);

        // Dual-computer is the faster set; if it were not, enabling it would make sync slower.
        Assert.True(limits.MultiUsage!.Upload <= limits.Regular.Upload);
        Assert.True(limits.MultiUsage.Download <= limits.Regular.Download);
    }

    /// <summary>An unauthenticated request is refused, and the client reads that as a logout.</summary>
    [SkippableFact]
    public void A_bad_token_is_reported_as_unauthorized() {
        Assert.Equal(AuthService.AccessStatus.Unauthorized,
            AuthService.IsTokenValid(_server.Email, Guid.NewGuid().ToString()));

        Assert.Equal(AuthService.AccessStatus.Authorized,
            AuthService.IsTokenValid(_server.Email, _server.Token));
    }

    /// <summary>
    /// A host that is not there is <see cref="AuthService.AccessStatus.Unknown"/>, never
    /// "logged out". Treating an outage as a logout would clear the token and reset every item's
    /// synchronised flag, and the next connection would re-upload the entire collection.
    /// </summary>
    [SkippableFact]
    public void An_unreachable_server_is_not_a_logout() {
        Environment.SetEnvironmentVariable("IAGD_CLOUD_HOST", "http://127.0.0.1:1");
        CloudUris.Initialize(CloudUris.EnvLocalDev);
        try {
            Assert.Equal(AuthService.AccessStatus.Unknown,
                AuthService.IsTokenValid(_server.Email, _server.Token));
        }
        finally {
            _server.UseUris();
        }
    }
}
