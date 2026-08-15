using Xunit;

namespace IAGrim.Cloud.Tests;

/// <summary>
/// Live sync, against the real hub.
///
/// The server both persists and relays each frame, and the relay deliberately excludes the
/// sender — so "two clients on one account" is the only configuration in which this feature does
/// anything at all, and the only one worth testing.
/// </summary>
[Collection(CloudServerCollection.Name)]
public class WebSocketSyncTests {
    private readonly CloudServerFixture _server;
    private readonly (string Email, string Token) _account;

    public WebSocketSyncTests(CloudServerFixture server) {
        _server = server;
        Skip.IfNot(server.Available, server.SkipReason);
        server.UseUris();
        _account = server.NewAccount();
    }

    private sealed class Peer : IDisposable {
        public TestCollection Collection { get; } = new();
        public TestSettings Settings { get; }
        public WebSocketSyncService Live { get; }

        public Peer((string Email, string Token) account, bool dualPc = true) {
            Settings = new TestSettings {
                CloudUser = account.Email,
                CloudAuthToken = account.Token,
                UsingDualComputer = dualPc,
            };

            Live = new WebSocketSyncService(
                new AuthenticationProvider(Settings), Settings, Collection.Store);
            Live.Start();
        }

        public bool WaitForConnection(int seconds = 15) {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) {
                if (Live.IsConnected) return true;
                Thread.Sleep(100);
            }
            return Live.IsConnected;
        }

        public bool WaitFor(Func<bool> condition, int seconds = 15) {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) {
                if (condition()) return true;
                Thread.Sleep(100);
            }
            return condition();
        }

        public void Dispose() {
            Live.Dispose();
            Collection.Dispose();
        }
    }

    private static CloudItem Item(string name = "Live Revolver") => new() {
        CloudId = CloudIdentity.New(),
        BaseRecord = "records/items/gearweapons/guns1h/c030_gun1h.dbr",
        PrefixRecord = "", SuffixRecord = "", ModifierRecord = "", TransmuteRecord = "",
        MateriaRecord = "", RelicCompletionBonusRecord = "", EnchantmentRecord = "",
        AscendantAffixNameRecord = "", AscendantAffix2hNameRecord = "",
        Seed = Random.Shared.NextInt64(1, int.MaxValue),
        StackCount = 1,
        CreationDate = 1_700_000_000_000,
        Name = name,
        NameLowercase = name.ToLowerInvariant(),
        Rarity = "Blue",
        LevelRequirement = 94,
        Mod = "",
    };

    [SkippableFact]
    public void A_looted_item_reaches_the_other_machine_immediately() {
        using var first = new Peer(_account);
        using var second = new Peer(_account);

        Assert.True(first.WaitForConnection(), "the first peer never connected");
        Assert.True(second.WaitForConnection(), "the second peer never connected");

        first.Live.SendItems([Item("Instant Revolver")]);

        Assert.True(second.WaitFor(() => second.Collection.CountItems() == 1),
            "the item never arrived over the live socket");

        // It arrived marked synchronised, so the REST loop on the second machine will not
        // upload it straight back under the same id.
        Assert.Empty(second.Collection.Store.GetUnsynchronizedItems());
    }

    /// <summary>
    /// The deletion half. This is what stops an item being transferred into the game twice: the
    /// second machine drops its copy on the next round trip rather than on the next REST window.
    /// </summary>
    [SkippableFact]
    public void An_in_game_transfer_removes_the_item_from_the_other_machine() {
        using var first = new Peer(_account);
        using var second = new Peer(_account);

        Assert.True(first.WaitForConnection());
        Assert.True(second.WaitForConnection());

        var item = Item("Doomed Revolver");
        first.Live.SendItems([item]);
        Assert.True(second.WaitFor(() => second.Collection.CountItems() == 1));

        first.Live.SendDeletions([item.CloudId!]);

        Assert.True(second.WaitFor(() => second.Collection.CountItems() == 0),
            "the deletion never reached the second machine");
    }

    /// <summary>
    /// The sender does not receive its own frame. If it did, every item a machine looted would
    /// come straight back to it and be stored a second time.
    /// </summary>
    [SkippableFact]
    public void A_machine_does_not_receive_its_own_events() {
        using var only = new Peer(_account);
        Assert.True(only.WaitForConnection());

        only.Live.SendItems([Item("Echo Revolver")]);
        Thread.Sleep(2000);

        Assert.Equal(0, only.Collection.CountItems());
    }

    /// <summary>
    /// Nothing connects unless the user asked for it. A socket held open for a single-PC user is
    /// a connection somebody else's server pays for and nothing reads.
    /// </summary>
    [SkippableFact]
    public void Live_sync_stays_off_without_the_multiple_pcs_setting() {
        using var peer = new Peer(_account, dualPc: false);

        Thread.Sleep(3000);
        Assert.False(peer.Live.IsConnected);

        // And it comes up when the setting is turned on, without a restart.
        peer.Settings.UsingDualComputer = true;
        Assert.True(peer.WaitForConnection(), "enabling the setting did not bring the socket up");
    }

    [SkippableFact]
    public void Live_sync_stays_off_without_a_token() {
        using var peer = new Peer((_account.Email, ""), dualPc: true);

        Thread.Sleep(3000);
        Assert.False(peer.Live.IsConnected);
    }

    /// <summary>
    /// Turning the setting off mid-session disconnects, rather than leaving the socket up until
    /// it happens to error.
    /// </summary>
    [SkippableFact]
    public void Turning_the_setting_off_disconnects() {
        using var peer = new Peer(_account);
        Assert.True(peer.WaitForConnection());

        peer.Settings.UsingDualComputer = false;

        Assert.True(peer.WaitFor(() => !peer.Live.IsConnected, seconds: 10),
            "the socket stayed up after the setting was turned off");
    }

    /// <summary>
    /// Sending while disconnected is a no-op, not a queue and not a crash — the REST sync is
    /// what guarantees delivery, and this path is only ever an accelerator.
    /// </summary>
    [SkippableFact]
    public void Sending_while_disconnected_does_nothing() {
        using var peer = new Peer(_account, dualPc: false);

        peer.Live.SendItems([Item()]);
        peer.Live.SendDeletions([CloudIdentity.New()]);

        Assert.Equal(0, peer.Collection.CountItems());
    }

    /// <summary>
    /// A frame for an item this machine already holds is ignored. The same item arrives twice by
    /// design — once live, once over REST — so the deduplication is what keeps the pair of them
    /// from producing two rows.
    /// </summary>
    [SkippableFact]
    public void An_item_already_held_is_not_stored_twice() {
        using var peer = new Peer(_account, dualPc: false);

        var item = Item("Twice Revolver");
        var frame = CloudJson.SerializeLive(new {
            type = "item",
            items = new[] { ItemConverter.ToUpload(item) },
        });

        peer.Live.HandleMessage(frame);
        Assert.Equal(1, peer.Collection.CountItems());

        peer.Live.HandleMessage(frame);
        Assert.Equal(1, peer.Collection.CountItems());
    }

    /// <summary>An item deleted here and not yet reported is not restored by a live frame.</summary>
    [SkippableFact]
    public void A_locally_deleted_item_is_not_restored_by_a_live_frame() {
        using var peer = new Peer(_account, dualPc: false);

        var item = Item("Rejected Revolver");
        var frame = CloudJson.SerializeLive(new {
            type = "item",
            items = new[] { ItemConverter.ToUpload(item) },
        });

        peer.Live.HandleMessage(frame);
        var id = peer.Collection.Store.GetUnsynchronizedItems().Count;   // zero: it arrived synced
        Assert.Equal(0, id);

        // Transfer it into the game, which leaves a tombstone.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={peer.Collection.Path}")) {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM PlayerItem WHERE cloudid = $id;";
            command.Parameters.AddWithValue("$id", item.CloudId!);
            peer.Collection.TransferAway((long)command.ExecuteScalar()!);
        }
        Assert.Equal(0, peer.Collection.CountItems());

        peer.Live.HandleMessage(frame);
        Assert.Equal(0, peer.Collection.CountItems());
    }

    /// <summary>A malformed or unknown frame is dropped rather than throwing on the receive thread.</summary>
    [SkippableTheory]
    [InlineData("not json at all")]
    [InlineData("""{"type":"something-else","items":[]}""")]
    [InlineData("""{"type":"item"}""")]
    [InlineData("""{"type":"item","items":[]}""")]
    [InlineData("""{"type":"delete","removed":[{"id":""}]}""")]
    [InlineData("{}")]
    public void A_malformed_frame_is_ignored(string frame) {
        using var peer = new Peer(_account, dualPc: false);

        peer.Live.HandleMessage(frame);

        Assert.Equal(0, peer.Collection.CountItems());
    }
}
