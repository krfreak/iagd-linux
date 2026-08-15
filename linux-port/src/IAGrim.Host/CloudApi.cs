using IAGrim.Cloud;
using IAGrim.Cloud.Data;

namespace IAGrim.Host;

/// <summary>
/// What the online-sync panel in the UI can ask for — upstream's <c>OnlineSettings</c> tab,
/// expressed as JSON instead of WinForms.
///
/// It is a thin layer on <see cref="CloudWorker"/> on purpose. Everything with consequences —
/// what gets uploaded, when, and what a logout throws away — lives in the services, so that the
/// same behaviour applies whether it was triggered from the window, from a browser pointed at
/// the headless host, or by a timer with nobody watching.
/// </summary>
public sealed class CloudApi {
    private readonly CloudWorker _worker;
    private readonly Platform.AppSettings _settings;

    /// <summary>
    /// The login in progress, if any. A login is a browser round trip with no callback, so the
    /// polling id has to outlive the request that started it.
    /// </summary>
    private string? _pendingLoginUrl;

    public CloudApi(CloudWorker worker, Platform.AppSettings settings) {
        _worker = worker;
        _settings = settings;
    }

    /// <summary>Everything the panel shows in one call.</summary>
    public object Status() {
        var status = _worker.Auth.CheckAuthentication();

        return new {
            // "unknown" is a real state and the UI has to say so rather than showing "logged
            // out": it means the service could not be reached, and offering a login button there
            // invites someone to re-authenticate over a problem that will pass.
            state = status switch {
                AuthService.AccessStatus.Authorized => "authorized",
                AuthService.AccessStatus.Unauthorized => "unauthorized",
                _ => "unknown",
            },
            user = _settings.CloudUser,
            buddyId = _settings.BuddySyncUserIdV3,
            usingDualComputer = _settings.UsingDualComputer,
            optOutOfBackups = _settings.OptOutOfBackups,
            liveSyncConnected = _worker.LiveSync.IsConnected,
            pendingLoginUrl = _pendingLoginUrl,

            // What is left to send. The number people actually want when they ask "is my
            // collection backed up".
            pendingUploads = _worker.Items.GetUnsynchronizedItems().Count,
            pendingDeletions = _worker.Items.GetItemsMarkedForOnlineDeletion().Count,

            // Which server this build talks to, so a development build cannot be mistaken for
            // one syncing a real collection.
            environment = CloudUris.Environment_,
            host = CloudUris.Host,
        };
    }

    /// <summary>
    /// Starts a login and returns the address to open. The polling thread does the rest; the UI
    /// finds out by watching <see cref="Status"/>.
    /// </summary>
    public object Login() {
        if (_settings.OptOutOfBackups) {
            return new { error = "Online features are switched off in settings." };
        }

        if (_worker.Auth.CheckAuthentication() == AuthService.AccessStatus.Authorized) {
            return new { error = "Already logged in.", user = _settings.CloudUser };
        }

        // false: open the user's own browser. There is no embedded one here to host the page in.
        var pollingId = _worker.Auth.Authenticate(embedded: false);
        _pendingLoginUrl = AuthService.LoginUrl(pollingId);

        _worker.Auth.OnAuthCompletion += (_, _) => {
            _pendingLoginUrl = null;
            // The cache is a day long, so without clearing it the panel would keep saying
            // "logged out" until tomorrow.
            AuthService.InvalidateCache();
            _worker.Buddies.FetchOwnBuddyId();
        };

        return new { loginUrl = _pendingLoginUrl };
    }

    public object Logout() {
        _worker.Logout();
        _pendingLoginUrl = null;
        return new { message = "Logged out. Your items stay in this collection." };
    }

    public object DeleteAccount() =>
        _worker.DeleteAccount()
            ? new { message = "Your online backup was deleted. Your items stay in this collection." }
            : new { error = "The backup service refused the deletion. Nothing was changed." };

    /// <summary>
    /// The two switches. <c>optOut</c> stops everything; <c>usingDualComputer</c> selects the
    /// faster cooldowns and turns on live sync, and is only correct for someone who really does
    /// play on two machines — it multiplies this client's request rate.
    /// </summary>
    public object UpdateSettings(bool? usingDualComputer, bool? optOutOfBackups) {
        if (usingDualComputer.HasValue) _settings.UsingDualComputer = usingDualComputer.Value;
        if (optOutOfBackups.HasValue) _settings.OptOutOfBackups = optOutOfBackups.Value;
        _settings.Save();

        return Status();
    }

    // ---------------------------------------------------------------------- buddies

    public object Buddies() =>
        _worker.BuddyStore.ListSubscriptions()
            .Select(subscription => new {
                subscription.Id,
                subscription.Nickname,
                subscription.IsHidden,
                items = _worker.BuddyStore.GetNumItems(subscription.Id),
                lastSync = subscription.LastSyncTimestamp,
            })
            .ToList();

    /// <summary>
    /// Subscribes to a buddy. The id is checked against the service first, as upstream's dialog
    /// does, so a mistyped number becomes an error rather than a subscription that silently
    /// never returns anything.
    /// </summary>
    public object AddBuddy(long id, string? nickname) {
        if (id <= BuddyItemsService.LegacyIdCeiling) {
            return new { error = "That is not a buddy id. They are six digits." };
        }

        if (id == _settings.BuddySyncUserIdV3) {
            return new { error = "That is your own buddy id." };
        }

        if (!_worker.Buddies.Verify(id)) {
            return new { error = "No account with that buddy id, or you are not logged in." };
        }

        // An existing subscription keeps its position, so editing a nickname does not re-download
        // the buddy's whole collection.
        var existing = _worker.BuddyStore.GetSubscription(id);
        _worker.BuddyStore.SaveOrUpdate(new BuddySubscription {
            Id = id,
            Nickname = nickname,
            IsHidden = existing?.IsHidden ?? false,
            LastSyncTimestamp = existing?.LastSyncTimestamp ?? 0,
        });

        return new { added = id };
    }

    public object UpdateBuddy(long id, string? nickname, bool? isHidden) {
        var subscription = _worker.BuddyStore.GetSubscription(id);
        if (subscription is null) return new { error = "No such buddy." };

        if (nickname is not null) subscription.Nickname = nickname;
        if (isHidden.HasValue) subscription.IsHidden = isHidden.Value;
        _worker.BuddyStore.SaveOrUpdate(subscription);

        return new { updated = id };
    }

    /// <summary>Unsubscribes, and removes their items. They were never this collection's.</summary>
    public object RemoveBuddy(long id) {
        _worker.BuddyStore.RemoveBuddy(id);
        return new { removed = id };
    }

    // ------------------------------------------------------------------- characters

    /// <summary>
    /// What has been backed up, plus the state of the backup itself — whether a pass is running,
    /// whether it is suspended because the game is open, and what the last one did.
    ///
    /// Both in one response because the panel shows them together, and because a list with no
    /// explanation of why it is empty is the thing people file bugs about.
    /// </summary>
    public object Characters() {
        var state = _worker.CharacterState;

        return new {
            characters = (_worker.Characters?.ListBackedUpCharacters() ?? [])
                .Select(character => new { character.Name, character.CreatedAt, character.UpdatedAt })
                .ToList(),
            backup = new {
                state.Available,
                state.Running,
                state.PausedForGame,
                state.LastRunUtc,
                state.Message,
                state.Failed,
            },
        };
    }

    /// <summary>
    /// Runs a backup pass now instead of waiting out the ten-minute cooldown. Returns
    /// immediately; the panel follows the outcome through <see cref="Characters"/>.
    /// </summary>
    public object BackupCharactersNow() {
        if (_worker.Characters is null) {
            return new { error = "No Grim Dawn save folder was found, so there is nothing to back up." };
        }

        if (_worker.CharacterState.PausedForGame) {
            return new { error = "Grim Dawn is running. Close it first: a save being written cannot be archived safely." };
        }

        return _worker.BackupCharactersNow()
            ? new { started = true }
            : new { error = "A backup is already running." };
    }

    /// <summary>
    /// A short-lived download link for one character backup. The archive goes straight from
    /// storage to the browser; it never passes through here.
    ///
    /// The link expires in five minutes, which is why it is fetched when the button is pressed
    /// rather than alongside the list.
    ///
    /// The host opens it, as upstream's client does — the app window is a WebKitGTK view that
    /// ignores <c>window.open</c>, so a page opening the link itself is how this silently did
    /// nothing at all. <c>opened</c> says whether that worked; the link is returned either way,
    /// because a page reached through a real browser can still open it and needs to be told.
    /// </summary>
    public object CharacterUrl(string name) {
        if (_worker.Characters is null) {
            return new { error = "Online sync has no save folder configured." };
        }

        if (_worker.Auth.CheckAuthentication() != AuthService.AccessStatus.Authorized) {
            return new { error = "You are not signed in." };
        }

        var download = _worker.Characters.RequestDownload(name);
        if (download.Url is null) {
            return new { error = download.Error ?? $"There is no backup of {name} on the server." };
        }

        return new { url = download.Url, opened = Platform.DesktopBrowser.Open(download.Url, out _) };
    }
}
