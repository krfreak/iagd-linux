using System.Net;
using IAGrim.Cloud.Data;
using IAGrim.Cloud.Dto;
using IAGrim.Platform;

namespace IAGrim.Cloud;

/// <summary>
/// Following friends' collections — upstream's <c>BuddyShare/BuddyItemsService.cs</c>.
///
/// A buddy id is a six-digit number the service hands out per account. Handing it to someone
/// lets them read your items; there is no approval step and nothing is shared in return, so a
/// subscription is one-directional and entirely the subscriber's business.
///
/// The loop is deliberately unhurried: each buddy has <b>its own</b> cooldown, three minutes by
/// default, so adding a friend fetches their collection promptly without the others resetting.
/// Buddy downloads are pure reads — nothing here ever writes to the player's own collection, and
/// no request tells the server anything.
/// </summary>
public sealed class BuddyItemsService : IDisposable {
    /// <summary>
    /// Ids at or below this are from a numbering scheme the service no longer issues. Upstream
    /// skips them rather than asking about them, and so does this.
    /// </summary>
    public const long LegacyIdCeiling = 9999;

    /// <summary>Upstream's interval, from <c>MainWindow</c>: three minutes per buddy.</summary>
    public const long DefaultCooldownMs = 3 * 60 * 1000;

    private readonly BuddyStore _buddyStore;
    private readonly ICloudSettings _settings;
    private readonly AuthService _authService;
    private readonly long _defaultCooldown;
    private readonly Dictionary<long, ActionCooldown> _cooldowns = [];

    private Thread? _thread;
    private volatile bool _running;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised after a buddy's items changed, so the UI can refresh.</summary>
    public event EventHandler? OnItemsChanged;

    public BuddyItemsService(
        BuddyStore buddyStore,
        ICloudSettings settings,
        AuthService authService,
        long cooldown = DefaultCooldownMs) {
        _buddyStore = buddyStore;
        _settings = settings;
        _authService = authService;
        _defaultCooldown = cooldown;
    }

    public void Start() {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "BuddyBackground" };
        _thread.Start();
    }

    private void Loop() {
        FetchOwnBuddyId();

        while (_running && !_cts.IsCancellationRequested) {
            _cts.Token.WaitHandle.WaitOne(5000);
            if (!_running || _cts.IsCancellationRequested) return;

            try { Execute(); }
            catch (Exception) { /* a background loop must not take the process down */ }
        }
    }

    /// <summary>
    /// One pass. Public so the tests — and a manual "sync now" — can run it without the thread.
    /// </summary>
    public void Execute() {
        // Names come from the parsed game data and arrive later than the items do, so this runs
        // whether or not there is a network.
        _buddyStore.UpdateNames(_buddyStore.ListItemsWithMissingName());

        // Opting out stops buddy sync entirely. Upstream returns out of its worker here, ending
        // the thread; this leaves the loop alive so turning the setting back off resumes without
        // a restart, which is the same intent without a one-way door.
        if (_settings.OptOutOfBackups) return;

        if (_authService.GetRestService() is null) {
            // Not logged in. Clearing the cooldowns means a login is followed by a prompt fetch
            // rather than by up to three minutes of nothing.
            _cooldowns.Clear();
            return;
        }

        foreach (var subscription in _buddyStore.ListSubscriptions()) {
            if (subscription.Id <= LegacyIdCeiling) continue;

            if (!_cooldowns.TryGetValue(subscription.Id, out var cooldown)) {
                cooldown = new ActionCooldown(_defaultCooldown);
                _cooldowns[subscription.Id] = cooldown;
            }

            if (!cooldown.IsReady) continue;

            SyncDown(subscription);
            cooldown.Reset();
        }

        var buddyId = _settings.BuddySyncUserIdV3;
        if (!buddyId.HasValue || buddyId <= 0) {
            FetchOwnBuddyId();
        }
    }

    /// <summary>
    /// Fetches one buddy's new items and applies their deletions.
    ///
    /// The timestamp is stored per subscription and only after the batch has been written, so a
    /// failure mid-sync re-fetches the same window instead of skipping it.
    /// </summary>
    public void SyncDown(BuddySubscription subscription) {
        try {
            var known = _buddyStore.GetOnlineIds(subscription).ToHashSet(StringComparer.Ordinal);
            var sync = Get(subscription);
            if (sync is null) return;

            var items = (sync.Items ?? [])
                .Where(item => !known.Contains(item.Id ?? ""))
                .Select(item => ToBuddyItem(subscription, item))
                .ToList();

            foreach (var batch in BatchUtil.ToBatches(items)) {
                _buddyStore.Save(subscription, batch);
            }

            _buddyStore.UpdateNames(items);

            if (sync.Removed is { Count: > 0 }) {
                _buddyStore.Delete(subscription, sync.Removed);
            }

            subscription.LastSyncTimestamp = sync.Timestamp;
            _buddyStore.SaveOrUpdate(subscription);

            if (items.Count > 0 || (sync.Removed?.Count ?? 0) > 0) {
                OnItemsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception) {
            // Unreachable, or a buddy who deleted their account. Retried on the next cooldown.
        }
    }

    private ItemDownloadDto? Get(BuddySubscription subscription) =>
        _authService.GetRestService()?
            .Get<ItemDownloadDto>($"{CloudUris.BuddyItemsUrl}?id={subscription.Id}&ts={subscription.LastSyncTimestamp}");

    /// <summary>
    /// Whether a buddy id exists, before it is subscribed to. Upstream's <c>AddEditBuddy.Verify</c>:
    /// a download request with an absurd timestamp, checked for a 200 and nothing else.
    /// </summary>
    public bool Verify(long buddyId) {
        var rest = _authService.GetRestService();
        if (rest is null) return false;

        return rest.VerifyGet($"{CloudUris.BuddyItemsUrl}?id={buddyId}&ts=900000000000") == HttpStatusCode.OK;
    }

    /// <summary>
    /// This account's own buddy id, to hand to a friend. Stored in settings; the endpoint mints
    /// one on first request.
    /// </summary>
    public long? FetchOwnBuddyId() {
        try {
            var id = _authService.GetRestService()?.Get<BuddyIdResult>(CloudUris.GetBuddyIdUrl!);
            if (id is null) return null;

            _settings.BuddySyncUserIdV3 = id.Id;
            _settings.Save();
            return id.Id;
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// The wire item to a buddy item.
    ///
    /// <b>Four fields are dropped, and that is upstream's mapping.</b> The enchantment record and
    /// both ascendant affix records have no column in <c>buddyitems_v6</c>, and the name is not
    /// taken from the server at all — it is recomputed locally from the game data, so that a
    /// buddy's item is captioned in the reader's language rather than the owner's.
    /// </summary>
    internal static BuddyItem ToBuddyItem(BuddySubscription subscription, CloudItemDto dto) => new() {
        BaseRecord = dto.BaseRecord,
        IsHardcore = dto.IsHardcore,
        MateriaRecord = dto.MateriaRecord,
        Mod = dto.Mod,
        ModifierRecord = dto.ModifierRecord,
        PrefixRecord = dto.PrefixRecord,
        StackCount = dto.StackCount,
        SuffixRecord = dto.SuffixRecord,
        TransmuteRecord = dto.TransmuteRecord,
        RemoteItemId = dto.Id,
        CreationDate = dto.CreatedAt,
        MinimumLevel = dto.LevelRequirement,
        Rarity = dto.Rarity,
        BuddyId = subscription.Id,
        PrefixRarity = dto.PrefixRarity,
        Seed = dto.Seed,
        RelicSeed = dto.RelicSeed,
        EnchantmentSeed = dto.EnchantmentSeed,
        RerollsUsed = dto.RerollsUsed,
        AffixRerollsUsed = dto.AffixRerollsUsed,
    };

    public void Dispose() {
        _running = false;
        try { _cts.Cancel(); }
        catch (Exception) { /* already disposed */ }
    }

    internal sealed class BuddyIdResult {
        public long Id { get; set; }
    }
}
