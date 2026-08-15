using IAGrim.Cloud.Data;
using IAGrim.Cloud.Dto;
using IAGrim.Platform;

namespace IAGrim.Cloud;

/// <summary>
/// The backup loop — upstream's <c>Backup/Cloud/Service/BackupService.cs</c>.
///
/// <see cref="Execute"/> is called about once a second and does nothing almost every time. What
/// it actually does is decided by three gates, and every one of them exists to keep this port
/// from being a burden on a service somebody runs for free:
///
///   1. <b>the cooldowns</b>, which the server hands out at <c>/logincheck</c>. Fifty-four
///      minutes per operation on one machine; ten seconds, and one second for uploads, for a
///      user who has said they play on two.
///   2. <b>the idle freeze</b>. Downloads stop entirely once nothing has been searched for in
///      31 minutes, so a client left open overnight goes quiet.
///   3. <b>download once</b>, unless in dual-computer mode. There is no second machine adding
///      items, so there is nothing to poll for.
///
/// Order matters: deletions, then uploads, then downloads. Uploading before deleting would push
/// an item that is already marked for removal.
/// </summary>
public sealed class BackupService {
    /// <summary>Downloads stop when the user has not searched for this long.</summary>
    private const int SyncFreezeMinutes = 31;

    private readonly AuthService _authService;
    private readonly ICloudItemStore _playerItemDao;
    private readonly ICloudSettings _settings;
    private readonly Action<IReadOnlyList<long>>? _onItemsUploaded;

    private CloudSyncService? _cloudSyncService;
    private Limitations? _cooldowns;
    private bool _hasSyncedDownOnce;
    private DateTimeOffset _lastSearchDt = DateTimeOffset.UtcNow;

    public BackupService(
        AuthService authService,
        ICloudItemStore playerItemDao,
        ICloudSettings settings,
        Action<IReadOnlyList<long>>? onItemsUploaded = null) {
        _authService = authService;
        _playerItemDao = playerItemDao;
        _settings = settings;
        _onItemsUploaded = onItemsUploaded;
    }

    /// <summary>Raised after a download changed the collection, so the UI can refresh.</summary>
    public event EventHandler? OnItemsChanged;

    public void Logout() => _authService.UnAuthenticate();

    /// <summary>Resets the idle timer. Wired to the search endpoint, as upstream wires it to its search box.</summary>
    public void OnSearch() => _lastSearchDt = DateTimeOffset.UtcNow;

    public void Execute() {
        if (_authService.CheckAuthentication() != AuthService.AccessStatus.Authorized) {
            return;
        }

        _cloudSyncService ??= new CloudSyncService(_authService.GetRestService()!);

        if (_cooldowns is null) {
            var limits = _cloudSyncService.GetLimitations();
            if (limits?.Regular is null || limits.MultiUsage is null) {
                // No limits, no requests. Asking again next tick is cheaper than guessing.
                return;
            }

            _cooldowns = new Limitations(
                new LimitationSet(limits.Regular.Delete, limits.Regular.Upload, limits.Regular.Download),
                new LimitationSet(limits.MultiUsage.Delete, limits.MultiUsage.Upload, limits.MultiUsage.Download));
        }

        var isDualPc = _settings.UsingDualComputer;
        var limitations = isDualPc ? _cooldowns.MultiUsage : _cooldowns.Regular;

        limitations.DeletionCooldown.ExecuteIfReady(SyncDeletions);
        limitations.UploadCooldown.ExecuteIfReady(SyncUp);

        var canSyncDown = isDualPc || !_hasSyncedDownOnce;
        if (canSyncDown && (DateTimeOffset.UtcNow - _lastSearchDt).TotalMinutes < SyncFreezeMinutes) {
            if (!limitations.DownloadCooldown.IsReady) return;

            // SyncDown answers "there is more waiting". Only once it says no is the one-shot
            // download considered done, so a collection larger than one batch still arrives in
            // full on a single-PC setup.
            if (!SyncDown()) {
                _hasSyncedDownOnce = true;
            }

            limitations.DownloadCooldown.Reset();
        }
    }

    /// <summary>
    /// Tells the server about items deleted here.
    ///
    /// The tombstones are cleared only on a successful batch, and the whole pass gives up on the
    /// first failure — leaving the remaining tombstones in place. A deletion that is dropped
    /// instead of retried means the item comes back down on the next download.
    /// </summary>
    private void SyncDeletions() {
        var items = _playerItemDao.GetItemsMarkedForOnlineDeletion();
        if (items.Count <= 0) return;

        var dtos = items.Select(item => new DeleteItemDto { Id = item.Id }).ToList();

        foreach (var batch in BatchUtil.ToBatches(dtos)) {
            // Re-checked per batch: the user may have logged out mid-sync, and continuing would
            // be sending requests with a credential they have just revoked.
            if (_authService.CheckAuthentication() != AuthService.AccessStatus.Authorized) return;

            if (!_cloudSyncService!.Delete(batch)) return;

            _playerItemDao.ClearItemsMarkedForOnlineDeletion();
        }
    }

    /// <summary>
    /// Uploads everything not yet uploaded, in batches of 100, marking each batch synchronised
    /// only after the server has accepted it.
    /// </summary>
    private void SyncUp() {
        var items = _playerItemDao.GetUnsynchronizedItems();
        if (items.Count == 0) return;

        EnsureCloudIds(items);

        foreach (var batch in BatchUtil.ToBatches(items)) {
            if (_authService.CheckAuthentication() != AuthService.AccessStatus.Authorized) return;

            try {
                if (!_cloudSyncService!.Save(batch.Select(ItemConverter.ToUpload).ToList())) {
                    // A rejected batch stays unsynchronised and is retried next window. It is
                    // not skipped: one bad item must not silently drop the other 99.
                    continue;
                }

                _playerItemDao.SetAsSynchronized(batch);
                _onItemsUploaded?.Invoke(batch.Select(item => item.Id).ToList());
            }
            catch (Exception) {
                // Network trouble mid-run: stop, keep everything unsynchronised, try again later.
                return;
            }
        }
    }

    /// <summary>
    /// Gives any item without a cloud id one.
    ///
    /// Items looted by this port already have one — it is assigned at loot time so the live
    /// socket can push the item straight away. This covers rows written before that was true,
    /// and rows merged in from another collection.
    /// </summary>
    private static void EnsureCloudIds(IList<CloudItem> items) {
        foreach (var item in items.Where(item => string.IsNullOrEmpty(item.CloudId))) {
            item.CloudId = CloudIdentity.New();
        }
    }

    /// <summary>
    /// Pulls down everything new. Returns true when the server says there is more waiting.
    /// </summary>
    private bool SyncDown() {
        try {
            // Knowing what is already here is what makes logging in on a machine that already
            // holds the collection cheap instead of catastrophic: without it, ten thousand items
            // come down and are stored a second time.
            var knownItems = new HashSet<string>(_playerItemDao.GetOnlineIds(), StringComparer.Ordinal);
            var deletedItems = _playerItemDao.GetItemsMarkedForOnlineDeletion()
                .Select(item => item.Id)
                .Where(id => id is not null)
                .ToHashSet(StringComparer.Ordinal)!;

            var sync = _cloudSyncService!.Get(_settings.CloudUploadTimestamp);

            // Items deleted here but not yet reported are skipped, or the deletion is undone by
            // the very next download.
            var items = (sync.Items ?? [])
                .Where(item => !deletedItems.Contains(item.Id ?? ""))
                .Where(item => !knownItems.Contains(item.Id ?? ""))
                .Select(ItemConverter.ToPlayerItem)
                .ToList();

            foreach (var batch in BatchUtil.ToBatches(items)) {
                _playerItemDao.Save(batch);
            }

            foreach (var batch in BatchUtil.ToBatches(sync.Removed ?? [])) {
                _playerItemDao.Delete(batch);
            }

            // Advanced only after everything above stored cleanly. An exception on the way here
            // leaves the old timestamp, so the same window is fetched again rather than skipped.
            _settings.CloudUploadTimestamp = sync.Timestamp;
            _settings.Save();

            if (items.Count > 0 || (sync.Removed?.Count ?? 0) > 0) {
                OnItemsChanged?.Invoke(this, EventArgs.Empty);
            }

            return sync.IsPartial;
        }
        catch (Exception) {
            return false;
        }
    }

    private sealed class LimitationSet {
        public LimitationSet(long cooldownDeletion, long cooldownUpload, long cooldownDownload) {
            DeletionCooldown = new ActionCooldown(cooldownDeletion);
            UploadCooldown = new ActionCooldown(cooldownUpload);
            DownloadCooldown = new ActionCooldown(cooldownDownload);
        }

        public ActionCooldown DeletionCooldown { get; }
        public ActionCooldown UploadCooldown { get; }
        public ActionCooldown DownloadCooldown { get; }
    }

    private sealed class Limitations {
        public Limitations(LimitationSet regular, LimitationSet multiUsage) {
            Regular = regular;
            MultiUsage = multiUsage;
        }

        public LimitationSet Regular { get; }
        public LimitationSet MultiUsage { get; }
    }
}
