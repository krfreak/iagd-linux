using System.Diagnostics;
using System.Net;
using System.Text.Json;
using IAGrim.Cloud.Data;
using IAGrim.Platform;

namespace IAGrim.Cloud;

/// <summary>
/// The stored credential. Upstream's <c>AuthenticationProvider</c>.
/// </summary>
public sealed class AuthenticationProvider {
    private readonly ICloudSettings _settings;

    public AuthenticationProvider(ICloudSettings settings) => _settings = settings;

    public string GetToken() => _settings.CloudAuthToken ?? string.Empty;

    public string GetUser() => _settings.CloudUser ?? string.Empty;

    public bool HasToken() => !string.IsNullOrWhiteSpace(_settings.CloudAuthToken);

    public void SetToken(string user, string token) {
        _settings.CloudUser = user;
        _settings.CloudAuthToken = token;
        _settings.Save();
    }
}

/// <summary>
/// Logging in, staying logged in, and handing out an authenticated client — upstream's
/// <c>Backup/Cloud/Service/AuthService.cs</c>.
///
/// The login is a browser flow with no callback: the client mints a polling id, opens
/// <c>iagd.evilsoft.net/login/?token=&lt;id&gt;</c>, and then asks <c>POST /status</c> every two
/// seconds for up to eight minutes until the server reports the e-mail and token. Nothing
/// listens on a local port and no credential is typed into this application.
///
/// <b>The result is cached for a day</b>, process-wide, exactly as upstream caches it. That is
/// what keeps a background loop running every second from turning into a <c>/logincheck</c>
/// every second. It also means logging out somewhere else is not noticed here until the cache
/// expires or something clears it — upstream's behaviour, reproduced rather than improved,
/// because "improving" it means more requests against somebody else's server.
/// </summary>
public sealed class AuthService : IDisposable {
    public enum AccessStatus {
        Authorized,
        Unauthorized,
        Unknown,
    }

    private const string CacheKey = "IAGDIsCloudAuthenticated";

    private readonly AuthenticationProvider _authenticationProvider;
    private readonly ICloudItemStore _playerItemDao;
    private volatile bool _isDisposing;
    private Thread? _pollingThread;
    private string? _pollingId;

    /// <summary>Raised on the polling thread once a login completes.</summary>
    public event EventHandler<AuthResult>? OnAuthCompletion;

    public AuthService(AuthenticationProvider authenticationProvider, ICloudItemStore playerItemDao) {
        _authenticationProvider = authenticationProvider;
        _playerItemDao = playerItemDao;
    }

    /// <summary>
    /// Asks the server whether a token is good. Five second timeout, and a server error is
    /// <see cref="AccessStatus.Unknown"/> rather than "logged out" — treating a 500 as a logout
    /// would wipe the token every time the service hiccups.
    /// </summary>
    public static AccessStatus IsTokenValid(string user, string token) {
        try {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-User", user);

            var status = client.GetAsync(CloudUris.TokenVerificationUri).GetAwaiter().GetResult().StatusCode;

            return status switch {
                HttpStatusCode.OK => AccessStatus.Authorized,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AccessStatus.Unauthorized,
                _ => AccessStatus.Unknown,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AggregateException or WebException) {
            // Offline, DNS failure, timeout: not a statement about the token.
            return AccessStatus.Unknown;
        }
    }

    /// <summary>
    /// The cached answer, refreshed at most daily.
    ///
    /// An <see cref="AccessStatus.Unauthorized"/> answer from the server also clears the stored
    /// token and resets every item's synchronised flag — the account is gone or the token was
    /// revoked, so the whole collection has to be offered again to whatever account is used next.
    /// </summary>
    public AccessStatus CheckAuthentication() {
        if (AuthCache.Get(CacheKey) is AccessStatus cached) {
            return cached;
        }

        if (!_authenticationProvider.HasToken()) {
            // Upstream caches the *boolean* false here, which never satisfies the AccessStatus
            // read above, so the no-token path is effectively uncached. Reproduced: it costs
            // nothing (there is no request to make without a token) and diverging would change
            // when the UI notices a login.
            AuthCache.Set(CacheKey, false, TimeSpan.FromDays(1));
            return AccessStatus.Unauthorized;
        }

        var result = IsTokenValid(_authenticationProvider.GetUser(), _authenticationProvider.GetToken());
        AuthCache.Set(CacheKey, result, TimeSpan.FromDays(1));

        if (result == AccessStatus.Unauthorized) {
            _authenticationProvider.SetToken(string.Empty, string.Empty);
            _playerItemDao.ResetOnlineSyncState();
        }

        return result;
    }

    /// <summary>
    /// Starts a login. Returns the polling id, which is also the token in the login URL.
    /// </summary>
    /// <param name="embedded">
    /// True when the caller is showing the login page itself. False opens the user's browser,
    /// which is what this port does — there is no CEF here to host the page in.
    /// </param>
    public string Authenticate(bool embedded) {
        _pollingId = Guid.NewGuid().ToString();

        if (!embedded) {
            OpenLoginPage(_pollingId);
        }

        _pollingThread = new Thread(PollForAccessTokenStatus) { IsBackground = true, Name = "PollForAccessTokenStatus" };
        _pollingThread.Start();
        return _pollingId;
    }

    /// <summary>The address the user has to visit. Exposed so a headless host can print it.</summary>
    public static string LoginUrl(string pollingId) => $"{CloudUris.LoginPageUrl}?token={pollingId}";

    private static void OpenLoginPage(string pollingId) {
        // A failure needs no handling: the caller still has the URL and the polling thread is
        // already running, so a login started in any browser still completes.
        DesktopBrowser.Open(LoginUrl(pollingId), out _);
    }

    // Every two seconds for up to ~8 minutes, or until five errors in a row.
    private void PollForAccessTokenStatus() {
        var numErrors = 0;
        var numRuns = 0;

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try {
            while (!_isDisposing && numRuns++ < 240) {
                Thread.Sleep(2000);

                var content = new FormUrlEncodedContent([
                    new KeyValuePair<string, string>("token", _pollingId!),
                ]);

                var result = client.PostAsync(CloudUris.TokenPollUri, content).GetAwaiter().GetResult();
                if (result.StatusCode != HttpStatusCode.OK) {
                    if (numErrors++ > 5) return;
                    continue;
                }

                var body = result.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!TryReadCompletedLogin(body, out var email, out var token)) {
                    continue; // still pending
                }

                _authenticationProvider.SetToken(email, token);
                AuthCache.Set(CacheKey, AccessStatus.Authorized, TimeSpan.FromDays(1));
                OnAuthCompletion?.Invoke(this, new AuthResult(email, token, true));
                return;
            }
        }
        catch (Exception) {
            // A dead network during a login is not worth a crash on a background thread; the
            // user retries.
        }
    }

    /// <summary>
    /// Reads a <c>/status</c> reply. Pending logins answer <c>{"status":"CREATED","token":null,
    /// "email":null}</c>, so a completed one is the only shape that yields a credential.
    /// </summary>
    internal static bool TryReadCompletedLogin(string body, out string email, out string token) {
        email = string.Empty;
        token = string.Empty;

        try {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.GetString() != "COMPLETED") {
                return false;
            }

            if (!root.TryGetProperty("email", out var emailElement) ||
                !root.TryGetProperty("token", out var tokenElement)) {
                return false;
            }

            var readEmail = emailElement.ValueKind == JsonValueKind.String ? emailElement.GetString() : null;
            var readToken = tokenElement.ValueKind == JsonValueKind.String ? tokenElement.GetString() : null;

            // Upstream bails out here too: a COMPLETED status with either field missing is a
            // reply it does not know how to store, and storing half of it would leave the client
            // sending an empty Authorization header on every request from then on.
            if (readEmail is null || readToken is null) return false;

            email = readEmail;
            token = readToken;
            return true;
        }
        catch (JsonException) {
            return false;
        }
    }

    /// <summary>
    /// Tells the server to forget this token, then forgets it locally.
    ///
    /// <b>The request does not work, and that is upstream's behaviour.</b> Upstream issues a GET;
    /// the service routes <c>/logout</c> as a POST, so the call comes back 404 and the token
    /// stays valid server-side until it expires. Upstream ignores the status and clears the
    /// token locally regardless, which is why nobody has noticed.
    ///
    /// Reproduced as-is rather than "fixed" to a POST: this port must not take an action against
    /// somebody's account that the Windows tool does not take. It is written down in PORTING.md
    /// so the choice is visible and can be reversed deliberately.
    /// </summary>
    public void Logout() {
        try {
            if (!_authenticationProvider.HasToken()) return;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", _authenticationProvider.GetToken());
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-User", _authenticationProvider.GetUser());
            client.GetAsync(CloudUris.LogoutUrl).GetAwaiter().GetResult();
        }
        catch (Exception) {
            // Logging out must succeed locally whatever the network says.
        }
        finally {
            UnAuthenticate();
        }
    }

    /// <summary>Drops the stored credential and marks the cached status unauthorized.</summary>
    public void UnAuthenticate() {
        _authenticationProvider.SetToken(string.Empty, string.Empty);
        AuthCache.Set(CacheKey, AccessStatus.Unauthorized, TimeSpan.FromDays(1));
    }

    /// <summary>The credential, or null when not logged in.</summary>
    public AuthenticationProvider? GetAuthProvider() =>
        CheckAuthentication() == AccessStatus.Authorized ? _authenticationProvider : null;

    /// <summary>
    /// An authenticated client, or null when not logged in. Callers treat null as "skip this
    /// pass", which is how every loop here stops doing anything after a logout.
    /// </summary>
    public RestService? GetRestService() {
        if (CheckAuthentication() != AccessStatus.Authorized) return null;

        var handler = new HttpClientHandler {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", _authenticationProvider.GetToken());
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-User", _authenticationProvider.GetUser());
        return new RestService(client);
    }

    /// <summary>Clears the day-long cache. Used by the tests, and after an explicit login.</summary>
    public static void InvalidateCache() => AuthCache.Remove(CacheKey);

    public void Dispose() {
        _isDisposing = true;
        _pollingThread = null;
    }
}

/// <summary>The outcome of a login attempt. Upstream's <c>AuthResultEvent</c>.</summary>
public sealed record AuthResult(string Email, string Token, bool IsAuthorized);

/// <summary>
/// The process-wide cache behind <see cref="AuthService.CheckAuthentication"/>.
///
/// Upstream uses <c>System.Runtime.Caching.MemoryCache.Default</c>, which is shared by every
/// <c>AuthService</c> it constructs — and it constructs them freely, one per UI event. The
/// sharing is the point: without it every button press is another <c>/logincheck</c>. Values are
/// stored as <c>object</c> because upstream stores two different types under the same key.
/// </summary>
internal static class AuthCache {
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (object Value, DateTimeOffset Expires)> Entries = [];

    public static object? Get(string key) {
        lock (Gate) {
            if (!Entries.TryGetValue(key, out var entry)) return null;
            if (entry.Expires <= DateTimeOffset.UtcNow) {
                Entries.Remove(key);
                return null;
            }
            return entry.Value;
        }
    }

    public static void Set(string key, object value, TimeSpan lifetime) {
        lock (Gate) {
            Entries[key] = (value, DateTimeOffset.UtcNow + lifetime);
        }
    }

    public static void Remove(string key) {
        lock (Gate) { Entries.Remove(key); }
    }
}
