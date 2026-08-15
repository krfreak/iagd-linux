namespace IAGrim.Platform;

/// <summary>
/// The settings online sync owns.
///
/// An interface rather than a direct dependency on <see cref="AppSettings"/> so the sync
/// services can be driven by an in-memory store in tests, and — more to the point — so the
/// handful of keys that carry an account credential are listed in one place instead of being
/// spread through a settings object that is mostly about stash tabs.
///
/// Upstream splits these across <c>PersistentSettings</c> (travels with the user) and
/// <c>LocalSettings</c> (machine-specific). This port has one flat settings file, so the split
/// is recorded per property instead.
/// </summary>
public interface ICloudSettings {
    /// <summary>Upstream <c>PersistentSettings.CloudUser</c> — the account e-mail, sent as <c>X-Api-User</c>.</summary>
    string? CloudUser { get; set; }

    /// <summary>
    /// Upstream <c>PersistentSettings.CloudAuthToken</c> — the bearer-less access token sent as
    /// <c>Authorization</c>. A credential: it is written to this port's own settings file and
    /// never to the settings file inside the Wine prefix, which belongs to a Windows install.
    /// </summary>
    string? CloudAuthToken { get; set; }

    /// <summary>
    /// Upstream <c>PersistentSettings.CloudUploadTimestamp</c> — the high-water mark handed back
    /// by the last download. Sent as <c>?ts=</c>; everything newer comes down.
    ///
    /// Resetting it to 0 re-downloads the whole collection, which is why logging out and
    /// deleting the account both set it back.
    /// </summary>
    long CloudUploadTimestamp { get; set; }

    /// <summary>
    /// Upstream <c>PersistentSettings.UsingDualComputer</c> — "I play on more than one PC".
    ///
    /// Two effects, both of them about how much traffic the service is asked for: it selects the
    /// faster cooldown set from <c>/logincheck</c>, and it is one of the two conditions for the
    /// live websocket sync to connect at all.
    /// </summary>
    bool UsingDualComputer { get; set; }

    /// <summary>
    /// Upstream <c>PersistentSettings.BuddySyncUserIdV3</c> — this account's own six-digit buddy
    /// id, fetched from <c>/buddyId</c>. Shown so it can be handed to a friend.
    /// </summary>
    long? BuddySyncUserIdV3 { get; set; }

    /// <summary>
    /// Upstream <c>LocalSettings.OptOutOfBackups</c> — "I do not want any of this".
    ///
    /// Checked by the buddy loop before it does anything, and by the login prompt. Distinct from
    /// simply not being logged in: it means do not ask again.
    /// </summary>
    bool OptOutOfBackups { get; set; }

    /// <summary>
    /// Upstream <c>LocalSettings.LastCharSyncUtc</c> — the modification time of the newest
    /// character save that has been backed up. Only files newer than this are zipped and sent.
    /// </summary>
    DateTime LastCharSyncUtc { get; set; }

    /// <summary>Persists the settings. Called after anything above is changed.</summary>
    void Save();
}
