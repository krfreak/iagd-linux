using IAGrim.Cloud.Dto;

namespace IAGrim.Cloud;

/// <summary>
/// The five item calls, and nothing else — upstream's
/// <c>Backup/Cloud/Service/CloudSyncService.cs</c>.
///
/// It is a wrapper thin enough to read in one go on purpose: this is the complete list of ways
/// this port can change what is stored on somebody else's server, and the list being short is
/// the point.
/// </summary>
public sealed class CloudSyncService {
    private readonly RestService _restService;

    public CloudSyncService(RestService restService) => _restService = restService;

    /// <summary>
    /// Removes items from the backup — typically because they were transferred back into the
    /// game here. Swallows every error and answers false, so the caller keeps its tombstones and
    /// tries again rather than losing track of the deletion.
    /// </summary>
    public bool Delete(List<DeleteItemDto> items) {
        try {
            return _restService.Post(CloudUris.DeleteItemsUrl!, CloudJson.SerializeUpload(items));
        }
        catch (Exception) {
            return false;
        }
    }

    /// <summary>
    /// Everything added or removed since <paramref name="lastTimestamp"/>.
    ///
    /// Errors are *not* swallowed here, unlike <see cref="Delete"/> — the caller has to
    /// distinguish "nothing new" from "the request failed", because it advances the stored
    /// timestamp on success and must not advance it past items it never received.
    /// </summary>
    public ItemDownloadDto Get(long lastTimestamp) =>
        _restService.Get<ItemDownloadDto>($"{CloudUris.DownloadUrl}?ts={lastTimestamp}");

    /// <summary>Deletes the account and everything in it. Irreversible, and the UI says so.</summary>
    public bool DeleteAccount() => _restService.Delete(CloudUris.DeleteAccountUrl!, "{}");

    /// <summary>
    /// Uploads a batch. At most 100 items, each with a cloud id of 32 characters or more, a base
    /// record, and a positive stack count — the server rejects the whole batch otherwise.
    /// </summary>
    public bool Save(List<CloudItemDto> items) =>
        _restService.Post(CloudUris.UploadItemsUrl!, CloudJson.SerializeUpload(items));

    /// <summary>
    /// How often this client may call each endpoint. Served by <c>/logincheck</c>, which is also
    /// the token check — one request answers both questions.
    /// </summary>
    public LimitsDto GetLimitations() => _restService.Get<LimitsDto>(CloudUris.FetchLimitationsUrl!);
}
