namespace IAGrim.Cloud;

/// <summary>
/// Every URL this port will ever ask of the backup service — upstream's
/// <c>IAGrim/Backup/Cloud/Uris.cs</c>, path for path.
///
/// It is one class rather than string literals at the call sites so the whole HTTP surface can
/// be read in one screen and pinned against upstream by
/// <c>scripts/verify-cloud-protocol.sh</c>. The service belongs to somebody else and is run for
/// free; a request this port invents is a request its author did not agree to serve.
/// </summary>
public static class CloudUris {
    public const string EnvLocalDev = "localdev";
    public const string EnvCloud = "cloud";

    /// <summary>The production host. Upstream's, verbatim.</summary>
    public const string CloudHost = "https://api.iagd.evilsoft.net";

    /// <summary>
    /// Where <c>localdev</c> points unless <c>IAGD_CLOUD_HOST</c> says otherwise.
    ///
    /// Upstream has this branch too but leaves it commented out, so its <c>localdev</c> falls
    /// through to the production host. Here it resolves, because the tests run a real instance
    /// of the server (github.com/marius00/IAGD-Onlinesync) and pointing them at production
    /// instead would be exactly the thing this port must never do.
    /// </summary>
    public const string LocalDevHost = "http://localhost:8080";

    /// <summary>
    /// Chooses the host. Anything other than the two known environments throws, as upstream's
    /// does — a typo must not silently become a live request.
    /// </summary>
    public static void Initialize(string env) {
        var host = env switch {
            EnvLocalDev => Environment.GetEnvironmentVariable("IAGD_CLOUD_HOST") ?? LocalDevHost,
            EnvCloud => CloudHost,
            _ => throw new ArgumentException($"unknown cloud environment: {env}", nameof(env)),
        };

        Environment_ = env;
        Host = host;

        // The websocket host mirrors the REST host, swapping http(s) for ws(s).
        var wsHost = host.Replace("https://", "wss://").Replace("http://", "ws://");
        WebSocketUrl = $"{wsHost}/ws";

        TokenVerificationUri = $"{host}/logincheck";
        TokenPollUri = $"{host}/status";
        UploadItemsUrl = $"{host}/upload";
        DownloadUrl = $"{host}/download";
        DeleteItemsUrl = $"{host}/remove";
        FetchLimitationsUrl = $"{host}/logincheck";
        DeleteAccountUrl = $"{host}/delete";
        LogoutUrl = $"{host}/logout";
        MigrateUrl = $"{host}/migrate";
        BuddyItemsUrl = $"{host}/buddyitems";
        GetBuddyIdUrl = $"{host}/buddyId";
        UploadCharacterUrl = $"{host}/character/upload";
        ListCharacterUrl = $"{host}/character";
        DownloadCharacterUrl = $"{host}/character/download";

        LoginPageUrl = "https://iagd.evilsoft.net/login/";
    }

    /// <summary>True once <see cref="Initialize"/> has run. Nothing may be requested before.</summary>
    public static bool IsInitialized => Host is not null;

    /// <summary>Which environment is live, for the UI to show. Not upstream's; upstream cannot switch.</summary>
    public static string? Environment_ { get; private set; }

    public static string? Host { get; private set; }

    public static string? LoginPageUrl { get; private set; }
    public static string? WebSocketUrl { get; private set; }
    public static string? DownloadUrl { get; private set; }
    public static string? TokenVerificationUri { get; private set; }
    public static string? TokenPollUri { get; private set; }
    public static string? UploadItemsUrl { get; private set; }
    public static string? DeleteItemsUrl { get; private set; }
    public static string? FetchLimitationsUrl { get; private set; }
    public static string? DeleteAccountUrl { get; private set; }
    public static string? LogoutUrl { get; private set; }
    public static string? MigrateUrl { get; private set; }
    public static string? BuddyItemsUrl { get; private set; }
    public static string? GetBuddyIdUrl { get; private set; }
    public static string? UploadCharacterUrl { get; private set; }
    public static string? ListCharacterUrl { get; private set; }
    public static string? DownloadCharacterUrl { get; private set; }
}
