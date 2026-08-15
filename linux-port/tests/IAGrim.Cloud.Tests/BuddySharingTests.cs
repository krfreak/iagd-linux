using IAGrim.Cloud.Data;
using IAGrim.Cloud.Dto;
using Xunit;

namespace IAGrim.Cloud.Tests;

/// <summary>
/// Buddy sharing, against the real server: two accounts, one of them following the other.
///
/// The distinction that matters throughout is that a buddy's items are <b>not</b> the player's.
/// They live in their own tables, they are never uploaded, and unsubscribing removes them. A
/// buddy item that leaked into <c>PlayerItem</c> would be a permanent, silent addition to
/// somebody's collection, so the tests check the player's side stayed empty as often as they
/// check the buddy's side filled up.
/// </summary>
[Collection(CloudServerCollection.Name)]
public class BuddySharingTests {
    private readonly CloudServerFixture _server;
    private readonly (string Email, string Token) _me;
    private readonly (string Email, string Token) _friend;

    public BuddySharingTests(CloudServerFixture server) {
        _server = server;
        Skip.IfNot(server.Available, server.SkipReason);
        server.UseUris();

        _me = server.NewAccount();
        _friend = server.NewAccount();
    }

    private static TestSettings SettingsFor((string Email, string Token) account) => new() {
        CloudUser = account.Email,
        CloudAuthToken = account.Token,
        UsingDualComputer = true,
    };

    private AuthService AuthFor((string Email, string Token) account, ICloudItemStore store) {
        AuthService.InvalidateCache();
        return new AuthService(new AuthenticationProvider(SettingsFor(account)), store);
    }

    private static CloudItemDto Item(string name = "Shared Revolver") => new() {
        Id = CloudIdentity.New(),
        BaseRecord = "records/items/gearweapons/guns1h/c030_gun1h.dbr",
        PrefixRecord = "records/items/lootaffixes/prefix/a01.dbr",
        Seed = Random.Shared.NextInt64(1, int.MaxValue),
        StackCount = 2,
        CreatedAt = 1_700_000_000_000,
        Name = name,
        NameLowercase = name.ToLowerInvariant(),
        Rarity = "Blue",
        LevelRequirement = 94,
        PrefixRarity = 3,
        Mod = "",
    };

    /// <summary>Uploads items to the friend's account, so there is something to follow.</summary>
    private void FriendUploads(params CloudItemDto[] items) {
        var settings = SettingsFor(_friend);
        using var store = new TestCollection();
        AuthService.InvalidateCache();
        var auth = new AuthService(new AuthenticationProvider(settings), store.Store);
        Assert.True(new CloudSyncService(auth.GetRestService()!).Save(items.ToList()));
    }

    private long FriendBuddyId() {
        using var store = new TestCollection();
        var settings = SettingsFor(_friend);
        AuthService.InvalidateCache();
        var auth = new AuthService(new AuthenticationProvider(settings), store.Store);
        var service = new BuddyItemsService(new BuddyStore(store.Path), settings, auth);
        var id = service.FetchOwnBuddyId();
        Assert.NotNull(id);
        return id!.Value;
    }

    [SkippableFact]
    public void An_account_has_a_six_digit_buddy_id() {
        var id = FriendBuddyId();

        // The UI asks for exactly six digits, so an id outside that range would be unenterable.
        Assert.InRange(id, 100000, 999999);
        Assert.True(id > BuddyItemsService.LegacyIdCeiling);
    }

    [SkippableFact]
    public void A_buddys_items_arrive_in_the_buddy_tables_and_nowhere_else() {
        var item = Item("Followed Revolver");
        FriendUploads(item);
        var buddyId = FriendBuddyId();

        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var settings = SettingsFor(_me);
        var service = new BuddyItemsService(buddies, settings, AuthFor(_me, mine.Store), cooldown: 0);

        buddies.SaveOrUpdate(new BuddySubscription { Id = buddyId, Nickname = "A Friend" });
        service.Execute();

        Assert.Equal(1, buddies.GetNumItems(buddyId));

        // The player's own collection is untouched. This is the assertion that matters: a buddy
        // item written into PlayerItem would be a permanent addition to somebody's collection
        // that they never looted and cannot explain.
        Assert.Equal(0, mine.CountItems());
        Assert.Empty(mine.Store.GetUnsynchronizedItems());
    }

    [SkippableFact]
    public void A_buddy_item_carries_the_fields_the_table_has_room_for() {
        var item = Item("Detailed Revolver");
        FriendUploads(item);
        var buddyId = FriendBuddyId();

        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var service = new BuddyItemsService(buddies, SettingsFor(_me), AuthFor(_me, mine.Store), cooldown: 0);

        buddies.SaveOrUpdate(new BuddySubscription { Id = buddyId, Nickname = "A Friend" });
        service.Execute();

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={mine.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT baserecord, prefixrecord, stackcount, rarity, prefixrarity, levelrequirement,
                   created_at, seed, ishardcore
            FROM buddyitems_v6 WHERE id_item_remote = $id;
            """;
        command.Parameters.AddWithValue("$id", item.Id!);

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(item.BaseRecord, reader.GetString(0));
        Assert.Equal(item.PrefixRecord, reader.GetString(1));
        Assert.Equal(item.StackCount, reader.GetInt64(2));
        Assert.Equal(item.Rarity, reader.GetString(3));

        // PrefixRarity *is* carried for buddy items, unlike the player's own download path.
        Assert.Equal(item.PrefixRarity, reader.GetInt64(4));
        Assert.Equal(item.LevelRequirement, reader.GetDouble(5));
        Assert.Equal(item.CreatedAt, reader.GetInt64(6));
        Assert.Equal(item.Seed, reader.GetInt64(7));
        Assert.False(reader.GetBoolean(8));
    }

    /// <summary>
    /// The records go into the buddy lookup table, which is what the damage-type and pet-bonus
    /// filters search. Without them a buddy's items are in the database but match nothing.
    /// </summary>
    [SkippableFact]
    public void A_buddy_items_records_are_indexed_for_the_filters() {
        var item = Item("Indexed Revolver");
        FriendUploads(item);
        var buddyId = FriendBuddyId();

        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var service = new BuddyItemsService(buddies, SettingsFor(_me), AuthFor(_me, mine.Store), cooldown: 0);

        buddies.SaveOrUpdate(new BuddySubscription { Id = buddyId });
        service.Execute();

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={mine.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record FROM BuddyItemRecord_v2 WHERE id_item = $id ORDER BY record;";
        command.Parameters.AddWithValue("$id", item.Id!);

        var records = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) records.Add(reader.GetString(0));

        Assert.Contains(item.BaseRecord, records);
        Assert.Contains(item.PrefixRecord, records);
    }

    /// <summary>
    /// Syncing twice does not duplicate. The primary key is (remote item id, buddy id) and the
    /// already-held set is checked first, so a re-fetched window is a no-op either way.
    /// </summary>
    [SkippableFact]
    public void Syncing_a_buddy_twice_does_not_duplicate_their_items() {
        FriendUploads(Item("Once Revolver"));
        var buddyId = FriendBuddyId();

        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var service = new BuddyItemsService(buddies, SettingsFor(_me), AuthFor(_me, mine.Store), cooldown: 0);

        buddies.SaveOrUpdate(new BuddySubscription { Id = buddyId });
        service.Execute();
        Assert.Equal(1, buddies.GetNumItems(buddyId));

        // Rewind the per-buddy high-water mark: the same window comes down again.
        var subscription = buddies.GetSubscription(buddyId)!;
        subscription.LastSyncTimestamp = 0;
        buddies.SaveOrUpdate(subscription);

        service.Execute();
        Assert.Equal(1, buddies.GetNumItems(buddyId));
    }

    /// <summary>The per-buddy timestamp advances, so the next pass asks only for what is new.</summary>
    [SkippableFact]
    public void Each_subscription_keeps_its_own_high_water_mark() {
        FriendUploads(Item("Marked Revolver"));
        var buddyId = FriendBuddyId();

        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var service = new BuddyItemsService(buddies, SettingsFor(_me), AuthFor(_me, mine.Store), cooldown: 0);

        buddies.SaveOrUpdate(new BuddySubscription { Id = buddyId });
        Assert.Equal(0, buddies.GetSubscription(buddyId)!.LastSyncTimestamp);

        service.Execute();
        Assert.True(buddies.GetSubscription(buddyId)!.LastSyncTimestamp > 0);
    }

    /// <summary>An item the buddy transfers away disappears here too.</summary>
    [SkippableFact]
    public void A_buddys_deletion_removes_the_item_here() {
        var item = Item("Reclaimed Revolver");
        FriendUploads(item);
        var buddyId = FriendBuddyId();

        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var service = new BuddyItemsService(buddies, SettingsFor(_me), AuthFor(_me, mine.Store), cooldown: 0);

        buddies.SaveOrUpdate(new BuddySubscription { Id = buddyId });
        service.Execute();
        Assert.Equal(1, buddies.GetNumItems(buddyId));

        Thread.Sleep(1100);   // the server stamps in whole seconds

        // The friend transfers it into the game.
        using (var friendCollection = new TestCollection()) {
            AuthService.InvalidateCache();
            var friendAuth = new AuthService(
                new AuthenticationProvider(SettingsFor(_friend)), friendCollection.Store);
            Assert.True(new CloudSyncService(friendAuth.GetRestService()!)
                .Delete([new DeleteItemDto { Id = item.Id }]));
        }

        AuthService.InvalidateCache();
        service.Execute();

        Assert.Equal(0, buddies.GetNumItems(buddyId));
    }

    /// <summary>
    /// Unsubscribing takes the items with it, along with the record rows keyed to them. A buddy
    /// removed but whose items stayed would be items in the search with no one to attribute them
    /// to.
    /// </summary>
    [SkippableFact]
    public void Removing_a_buddy_removes_their_items_and_their_record_rows() {
        var item = Item("Departing Revolver");
        FriendUploads(item);
        var buddyId = FriendBuddyId();

        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var service = new BuddyItemsService(buddies, SettingsFor(_me), AuthFor(_me, mine.Store), cooldown: 0);

        buddies.SaveOrUpdate(new BuddySubscription { Id = buddyId });
        service.Execute();
        Assert.Equal(1, buddies.GetNumItems(buddyId));

        buddies.RemoveBuddy(buddyId);

        Assert.Equal(0, buddies.GetNumItems(buddyId));
        Assert.Empty(buddies.ListSubscriptions());

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={mine.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM BuddyItemRecord_v2 WHERE id_item = $id;";
        command.Parameters.AddWithValue("$id", item.Id!);
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
    }

    /// <summary>
    /// Legacy ids are skipped without a request. They belong to a numbering scheme the service
    /// no longer issues, and asking about them is a guaranteed-useless call.
    /// </summary>
    [SkippableFact]
    public void Legacy_buddy_ids_are_skipped_entirely() {
        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var service = new BuddyItemsService(buddies, SettingsFor(_me), AuthFor(_me, mine.Store), cooldown: 0);

        buddies.SaveOrUpdate(new BuddySubscription { Id = 4711, Nickname = "Ancient" });
        service.Execute();

        // Untouched: no fetch was attempted, so the timestamp never moved.
        Assert.Equal(0, buddies.GetSubscription(4711)!.LastSyncTimestamp);
        Assert.Equal(0, buddies.GetNumItems(4711));
    }

    /// <summary>Opting out stops buddy sync, without unsubscribing anyone.</summary>
    [SkippableFact]
    public void Opting_out_stops_buddy_sync() {
        FriendUploads(Item("Unwanted Revolver"));
        var buddyId = FriendBuddyId();

        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var settings = SettingsFor(_me);
        settings.OptOutOfBackups = true;

        var service = new BuddyItemsService(buddies, settings, AuthFor(_me, mine.Store), cooldown: 0);
        buddies.SaveOrUpdate(new BuddySubscription { Id = buddyId });
        service.Execute();

        Assert.Equal(0, buddies.GetNumItems(buddyId));
        Assert.Single(buddies.ListSubscriptions());

        // And it resumes when the setting is cleared.
        settings.OptOutOfBackups = false;
        service.Execute();
        Assert.Equal(1, buddies.GetNumItems(buddyId));
    }

    /// <summary>A buddy id is checked before it is subscribed to, so a typo does not become a subscription.</summary>
    [SkippableFact]
    public void A_buddy_id_is_verified_before_it_is_added() {
        var buddyId = FriendBuddyId();

        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var service = new BuddyItemsService(buddies, SettingsFor(_me), AuthFor(_me, mine.Store), cooldown: 0);

        Assert.True(service.Verify(buddyId));
        Assert.False(service.Verify(999999999));
    }

    /// <summary>Logging out forgets every buddy: their items were never the user's to keep.</summary>
    [SkippableFact]
    public void Logging_out_forgets_every_buddy() {
        FriendUploads(Item("Borrowed Revolver"));
        var buddyId = FriendBuddyId();

        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var service = new BuddyItemsService(buddies, SettingsFor(_me), AuthFor(_me, mine.Store), cooldown: 0);

        buddies.SaveOrUpdate(new BuddySubscription { Id = buddyId });
        service.Execute();
        Assert.Equal(1, buddies.GetNumItems(buddyId));

        buddies.DeleteAll();

        Assert.Empty(buddies.ListSubscriptions());
        Assert.Equal(0, buddies.GetNumItems(buddyId));
    }

    /// <summary>
    /// Nothing happens without a login — not even for an already-subscribed buddy. Buddy items
    /// are fetched with the *reader's* credentials, so a logged-out client has nothing to ask with.
    /// </summary>
    [SkippableFact]
    public void Buddy_sync_does_nothing_while_logged_out() {
        FriendUploads(Item("Locked Revolver"));
        var buddyId = FriendBuddyId();

        using var mine = new TestCollection();
        using var buddies = new BuddyStore(mine.Path);
        var settings = SettingsFor(_me);
        settings.CloudAuthToken = null;
        AuthService.InvalidateCache();

        var service = new BuddyItemsService(
            buddies, settings, new AuthService(new AuthenticationProvider(settings), mine.Store), cooldown: 0);

        buddies.SaveOrUpdate(new BuddySubscription { Id = buddyId });
        service.Execute();

        Assert.Equal(0, buddies.GetNumItems(buddyId));
    }

    /// <summary>
    /// The conversion, stated field by field — including the four the buddy table has no room
    /// for. Losing one silently is how a buddy's items end up subtly different from the owner's.
    /// </summary>
    [Fact]
    public void The_buddy_conversion_drops_exactly_the_fields_the_table_lacks() {
        var subscription = new BuddySubscription { Id = 123456 };
        var dto = new CloudItemDto {
            Id = "aaaaaaaabbbbccccddddeeeeffff0001",
            BaseRecord = "records/base.dbr",
            PrefixRecord = "records/prefix.dbr",
            SuffixRecord = "records/suffix.dbr",
            ModifierRecord = "records/modifier.dbr",
            TransmuteRecord = "records/transmute.dbr",
            MateriaRecord = "records/materia.dbr",
            EnchantmentRecord = "records/enchant.dbr",
            AscendantAffixNameRecord = "records/asc1.dbr",
            AscendantAffix2hNameRecord = "records/asc2.dbr",
            Seed = 11, RelicSeed = 22, EnchantmentSeed = 33,
            StackCount = 4, RerollsUsed = 5, AffixRerollsUsed = 6,
            CreatedAt = 1_700_000_000_000,
            PrefixRarity = 3,
            Name = "Owners Name", NameLowercase = "owners name",
            Rarity = "Blue", LevelRequirement = 94,
            Mod = "grimarillion", IsHardcore = true,
        };

        var item = BuddyItemsService.ToBuddyItem(subscription, dto);

        Assert.Equal(123456, item.BuddyId);
        Assert.Equal(dto.Id, item.RemoteItemId);
        Assert.Equal(dto.BaseRecord, item.BaseRecord);
        Assert.Equal(dto.PrefixRecord, item.PrefixRecord);
        Assert.Equal(dto.SuffixRecord, item.SuffixRecord);
        Assert.Equal(dto.ModifierRecord, item.ModifierRecord);
        Assert.Equal(dto.TransmuteRecord, item.TransmuteRecord);
        Assert.Equal(dto.MateriaRecord, item.MateriaRecord);
        Assert.Equal(dto.Seed, item.Seed);
        Assert.Equal(dto.RelicSeed, item.RelicSeed);
        Assert.Equal(dto.EnchantmentSeed, item.EnchantmentSeed);
        Assert.Equal(dto.StackCount, item.StackCount);
        Assert.Equal(dto.RerollsUsed, item.RerollsUsed);
        Assert.Equal(dto.AffixRerollsUsed, item.AffixRerollsUsed);
        Assert.Equal(dto.CreatedAt, item.CreationDate);
        Assert.Equal(dto.PrefixRarity, item.PrefixRarity);
        Assert.Equal(dto.Rarity, item.Rarity);
        Assert.Equal(dto.LevelRequirement, item.MinimumLevel);
        Assert.Equal(dto.Mod, item.Mod);
        Assert.True(item.IsHardcore);

        // The name is *not* taken from the owner: it is recomputed here, so the item reads in the
        // reader's language rather than whatever the uploader's client was set to.
        Assert.Null(item.Name);
        Assert.Null(item.NameLowercase);
    }
}
