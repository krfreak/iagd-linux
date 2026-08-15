using System.Text.Json;
using IAGrim.Cloud.Dto;
using Xunit;

namespace IAGrim.Cloud.Tests;

/// <summary>
/// The bytes on the wire.
///
/// Upstream serialises with Newtonsoft and — the detail this whole file exists for — with
/// *different settings per transport*: the REST upload goes through
/// <c>JsonConvert.SerializeObject(items)</c> with no settings at all, so it carries declared
/// (Pascal) casing and writes nulls; the websocket goes through a camel-case resolver with
/// <c>NullValueHandling.Ignore</c>. This port serialises with System.Text.Json, so "it works
/// against the server" is not enough — the produced text has to be the same text.
/// </summary>
public class WireFormatTests {
    private static CloudItemDto Dto() => new() {
        Id = "aaaaaaaabbbbccccddddeeeeffff0001",
        Mod = null,
        IsHardcore = false,
        BaseRecord = "records/items/gearweapons/guns1h/c030_gun1h.dbr",
        Seed = 1234567,
        StackCount = 1,
        CreatedAt = 1_700_000_000_000,
        Name = "Test Revolver",
        NameLowercase = "test revolver",
        Rarity = "Blue",
        LevelRequirement = 94,
    };

    /// <summary>
    /// The REST body, byte for byte: declared casing, every field present, nulls written as
    /// null, and the properties in upstream's declaration order.
    ///
    /// The order does not matter to the server. It matters here because it is the cheapest way
    /// to notice that upstream added, removed or moved a field.
    /// </summary>
    [Fact]
    public void An_uploaded_item_is_serialised_the_way_upstream_serialises_it() {
        var json = CloudJson.SerializeUpload(Dto());

        Assert.Equal(
            """
            {"Id":"aaaaaaaabbbbccccddddeeeeffff0001","Mod":null,"IsHardcore":false,"BaseRecord":"records/items/gearweapons/guns1h/c030_gun1h.dbr","PrefixRecord":"","SuffixRecord":"","ModifierRecord":"","TransmuteRecord":"","MateriaRecord":"","RelicCompletionBonusRecord":"","EnchantmentRecord":"","AscendantAffixNameRecord":"","AscendantAffix2hNameRecord":"","Seed":1234567,"RelicSeed":0,"EnchantmentSeed":0,"MateriaCombines":0,"StackCount":1,"RerollsUsed":0,"AffixRerollsUsed":0,"CreatedAt":1700000000000,"PrefixRarity":0,"Name":"Test Revolver","NameLowercase":"test revolver","Rarity":"Blue","LevelRequirement":94}
            """,
            json);
    }

    /// <summary>A deletion is a one-field object, and its casing follows the same rule.</summary>
    [Fact]
    public void A_deletion_is_serialised_the_way_upstream_serialises_it() {
        var json = CloudJson.SerializeUpload(new List<DeleteItemDto> {
            new() { Id = "aaaaaaaabbbbccccddddeeeeffff0001" },
        });

        Assert.Equal("""[{"Id":"aaaaaaaabbbbccccddddeeeeffff0001"}]""", json);
    }

    /// <summary>
    /// The live-sync frame is the other shape: camelCase, and null fields dropped rather than
    /// written. Sending the REST shape here would still be understood by the server, but it
    /// would not be what the Windows tool sends, and this port has no way to notice the
    /// difference later except by having asserted it now.
    /// </summary>
    [Fact]
    public void A_websocket_frame_is_camel_cased_and_omits_nulls() {
        var json = CloudJson.SerializeLive(new { type = "item", items = new[] { Dto() } });

        Assert.Contains("\"type\":\"item\"", json);
        Assert.Contains("\"baseRecord\":\"records/items/gearweapons/guns1h/c030_gun1h.dbr\"", json);
        Assert.Contains("\"id\":\"aaaaaaaabbbbccccddddeeeeffff0001\"", json);
        Assert.Contains("\"stackCount\":1", json);
        Assert.Contains("\"levelRequirement\":94", json);

        // Mod is null on this item, so it is not written at all.
        Assert.DoesNotContain("\"mod\"", json);
        Assert.DoesNotContain("\"Mod\"", json);
        Assert.DoesNotContain("\"BaseRecord\"", json);
    }

    /// <summary>
    /// A download response, as the server actually writes it — this is a recorded reply from a
    /// real instance, including the <c>ts</c> and <c>unknown</c> fields the client has no
    /// property for and must ignore rather than choke on.
    /// </summary>
    [Fact]
    public void A_download_response_is_read_the_way_the_server_writes_it() {
        const string body = """
            {"items":[{"id":"aaaaaaaabbbbccccddddeeeeffff0001","ts":1786799959,"mod":"","isHardcore":false,"baseRecord":"records/items/gearweapons/guns1h/c030_gun1h.dbr","prefixRecord":"","suffixRecord":"","modifierRecord":"","transmuteRecord":"","materiaRecord":"","relicCompletionBonusRecord":"","enchantmentRecord":"","ascendantAffixNameRecord":"","ascendantAffix2hNameRecord":"","seed":1234567,"relicSeed":0,"enchantmentSeed":0,"materiaCombines":0,"stackCount":1,"rerollsUsed":0,"affixRerollsUsed":0,"createdAt":1700000000000,"name":"Test Revolver","nameLowercase":"test revolver","rarity":"Blue","levelRequirement":94,"prefixRarity":0,"unknown":0}],"removed":[{"id":"aaaaaaaabbbbccccddddeeeeffff0009"}],"timestamp":1786799959,"isPartial":false}
            """;

        var sync = CloudJson.Deserialize<ItemDownloadDto>(body);

        Assert.NotNull(sync);
        Assert.Equal(1786799959, sync!.Timestamp);
        Assert.False(sync.IsPartial);

        var item = Assert.Single(sync.Items!);
        Assert.Equal("aaaaaaaabbbbccccddddeeeeffff0001", item.Id);
        Assert.Equal("records/items/gearweapons/guns1h/c030_gun1h.dbr", item.BaseRecord);
        Assert.Equal(1234567, item.Seed);
        Assert.Equal(1_700_000_000_000, item.CreatedAt);
        Assert.Equal("Blue", item.Rarity);
        Assert.Equal(94, item.LevelRequirement);
        Assert.Equal("", item.Mod);

        var removed = Assert.Single(sync.Removed!);
        Assert.Equal("aaaaaaaabbbbccccddddeeeeffff0009", removed.Id);
    }

    /// <summary>
    /// An empty collection still deserialises into empty lists rather than nulls, and a
    /// response missing them entirely leaves them null — both of which the sync loop has to
    /// cope with, because the server sends the first and a truncated proxy reply the second.
    /// </summary>
    [Fact]
    public void A_download_response_survives_missing_and_empty_collections() {
        var empty = CloudJson.Deserialize<ItemDownloadDto>(
            """{"items":[],"removed":[],"timestamp":17,"isPartial":true}""");
        Assert.Empty(empty!.Items!);
        Assert.Empty(empty.Removed!);
        Assert.True(empty.IsPartial);

        var sparse = CloudJson.Deserialize<ItemDownloadDto>("""{"timestamp":17}""");
        Assert.Null(sparse!.Items);
        Assert.Null(sparse.Removed);
        Assert.Equal(17, sparse.Timestamp);
    }

    /// <summary>The cooldown reply, in the shape the live service returns it.</summary>
    [Fact]
    public void The_limits_response_is_read_the_way_the_server_writes_it() {
        const string body = """
            {"msg":"Logged in and all that good stuff.","multiUsage":{"delete":10000,"download":10000,"upload":1000},"regular":{"delete":3240000,"download":3240000,"upload":3240000}}
            """;

        var limits = CloudJson.Deserialize<LimitsDto>(body);

        Assert.Equal(3240000, limits!.Regular!.Delete);
        Assert.Equal(3240000, limits.Regular.Download);
        Assert.Equal(3240000, limits.Regular.Upload);
        Assert.Equal(10000, limits.MultiUsage!.Delete);
        Assert.Equal(10000, limits.MultiUsage.Download);
        Assert.Equal(1000, limits.MultiUsage.Upload);
    }

    /// <summary>
    /// Non-ASCII item names travel as UTF-8 rather than as \u escapes. The server length-caps
    /// metadata strings in *bytes* (<c>len(s) &gt; 255</c>), so escaping would inflate a
    /// localised name past a limit it is nowhere near.
    /// </summary>
    [Fact]
    public void Localised_names_are_not_escaped() {
        var json = CloudJson.SerializeUpload(new CloudItemDto {
            Id = new string('a', 32), BaseRecord = "records/x.dbr", Name = "Mythischer Räuber",
        });

        Assert.Contains("\"Name\":\"Mythischer Räuber\"", json);
        Assert.DoesNotContain("\\u", json);
    }

    /// <summary>
    /// Every URL the port will request, listed here so adding one is a visible change. The
    /// paths are upstream's; scripts/verify-cloud-protocol.sh pins them against its Uris.cs.
    /// </summary>
    [Fact]
    public void The_endpoint_list_is_upstreams() {
        CloudUris.Initialize(CloudUris.EnvCloud);

        Assert.Equal("https://api.iagd.evilsoft.net/logincheck", CloudUris.TokenVerificationUri);
        Assert.Equal("https://api.iagd.evilsoft.net/logincheck", CloudUris.FetchLimitationsUrl);
        Assert.Equal("https://api.iagd.evilsoft.net/status", CloudUris.TokenPollUri);
        Assert.Equal("https://api.iagd.evilsoft.net/upload", CloudUris.UploadItemsUrl);
        Assert.Equal("https://api.iagd.evilsoft.net/download", CloudUris.DownloadUrl);
        Assert.Equal("https://api.iagd.evilsoft.net/remove", CloudUris.DeleteItemsUrl);
        Assert.Equal("https://api.iagd.evilsoft.net/delete", CloudUris.DeleteAccountUrl);
        Assert.Equal("https://api.iagd.evilsoft.net/logout", CloudUris.LogoutUrl);
        Assert.Equal("https://api.iagd.evilsoft.net/migrate", CloudUris.MigrateUrl);
        Assert.Equal("https://api.iagd.evilsoft.net/buddyitems", CloudUris.BuddyItemsUrl);
        Assert.Equal("https://api.iagd.evilsoft.net/buddyId", CloudUris.GetBuddyIdUrl);
        Assert.Equal("https://api.iagd.evilsoft.net/character/upload", CloudUris.UploadCharacterUrl);
        Assert.Equal("https://api.iagd.evilsoft.net/character", CloudUris.ListCharacterUrl);
        Assert.Equal("https://api.iagd.evilsoft.net/character/download", CloudUris.DownloadCharacterUrl);
        Assert.Equal("wss://api.iagd.evilsoft.net/ws", CloudUris.WebSocketUrl);
        Assert.Equal("https://iagd.evilsoft.net/login/", CloudUris.LoginPageUrl);
    }

    /// <summary>An unknown environment throws rather than quietly resolving to production.</summary>
    [Fact]
    public void An_unknown_environment_is_refused() {
        Assert.Throws<ArgumentException>(() => CloudUris.Initialize("staging"));
    }

    [Fact]
    public void The_local_development_host_stays_on_loopback() {
        Environment.SetEnvironmentVariable("IAGD_CLOUD_HOST", null);
        CloudUris.Initialize(CloudUris.EnvLocalDev);

        Assert.Equal("http://localhost:8080", CloudUris.Host);
        Assert.Equal("ws://localhost:8080/ws", CloudUris.WebSocketUrl);

        CloudUris.Initialize(CloudUris.EnvCloud);
    }
}

/// <summary>
/// Batching and pacing. Both are protocol constants rather than tuning: 100 is the server's own
/// per-request cap, and the cooldowns are whatever <c>/logincheck</c> hands out.
/// </summary>
public class PacingTests {
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 1)]
    [InlineData(101, 2)]
    [InlineData(200, 2)]
    [InlineData(201, 3)]
    [InlineData(2500, 25)]
    public void Batches_never_exceed_the_servers_limit(int items, int expectedBatches) {
        var batches = BatchUtil.ToBatches(Enumerable.Range(0, items).ToList());

        Assert.Equal(expectedBatches, batches.Count);
        Assert.All(batches, batch => Assert.InRange(batch.Count, 1, BatchUtil.MaxBatchSize));
        Assert.Equal(items, batches.Sum(batch => batch.Count));

        // Order is preserved and nothing is duplicated or lost.
        Assert.Equal(Enumerable.Range(0, items), batches.SelectMany(batch => batch));
    }

    [Fact]
    public void Batching_null_yields_nothing() {
        Assert.Empty(BatchUtil.ToBatches<int>(null));
    }

    /// <summary>
    /// A fresh cooldown fires immediately and only then starts counting. Upstream depends on
    /// that: the upload window is 54 minutes, and starting on cooldown would mean nothing is
    /// backed up for the first 54 minutes of every session.
    /// </summary>
    [Fact]
    public void A_fresh_cooldown_is_ready() {
        var cooldown = new ActionCooldown(60_000);
        Assert.True(cooldown.IsReady);

        var ran = 0;
        cooldown.ExecuteIfReady(() => ran++);
        Assert.Equal(1, ran);

        Assert.False(cooldown.IsReady);
        cooldown.ExecuteIfReady(() => ran++);
        Assert.Equal(1, ran);
    }

    [Fact]
    public void A_cooldown_can_start_already_spent() {
        var cooldown = new ActionCooldown(60_000, startTriggered: true);
        Assert.False(cooldown.IsReady);
        Assert.True(cooldown.IsOnCooldown);
    }

    [Fact]
    public void A_zero_cooldown_is_always_ready() {
        var cooldown = new ActionCooldown(0);
        var ran = 0;
        cooldown.ExecuteIfReady(() => ran++);
        cooldown.ExecuteIfReady(() => ran++);
        Assert.Equal(2, ran);
    }
}
