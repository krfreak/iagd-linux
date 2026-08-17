using System.Reflection;

namespace IAGrim.Cloud;

/// <summary>
/// One place where every client this port points at the sync service is built, so they all
/// identify themselves the same way.
///
/// Upstream sends no User-Agent on these calls — it is the only client the service has ever had,
/// so there was nothing to distinguish. This traffic is not upstream's. When a Linux client shows
/// up in someone else's logs it should be possible to tell, whether the conclusion is "that is
/// the port" or "that is the port misbehaving".
///
/// It cannot change how a request is handled. The service reads exactly two headers,
/// <c>Authorization</c> and <c>X-Api-User</c> (marius00/IAGD-Onlinesync,
/// <c>Server/internal/routing/auth.go</c>), and nothing anywhere in it inspects a user agent or a
/// client version. Whether the identifier is ever *seen* depends on what fronts the service:
/// its own logging is gin's default, which does not record one.
/// </summary>
public static class CloudHttp {
    /// <summary>
    /// This installation's identifier, set once at startup from the persisted settings. Null
    /// until then — the tests and the CLI never set it, and an unidentified request is better
    /// than one carrying an id invented per process, which would be noise pretending to be data.
    /// </summary>
    public static string? ClientId { get; set; }

    /// <summary>
    /// What every request announces, e.g.
    /// <c>IAGD-Linux/2026.08.17.2 (2c4cee6107db4307b1f44b76cd3f568c)</c>.
    ///
    /// The version comes from the assembly, which the packaged build stamps from the release
    /// tag. A build from a working tree says 1.0.0, which is honest: it is not a release.
    /// </summary>
    public static string UserAgent =>
        ClientId is null ? $"IAGD-Linux/{ClientVersion}"
                         : $"IAGD-Linux/{ClientVersion} ({ClientId})";

    /// <summary>The version alone, without the platform.</summary>
    public static string ClientVersion => ResolveVersion();

    /// <summary>
    /// A client for the sync service. The timeout is the caller's, because upstream's differ by
    /// two orders of magnitude between a login check and a character upload, and that is a
    /// deliberate part of how much of a stall each one tolerates.
    /// </summary>
    public static HttpClient Create(TimeSpan timeout, HttpMessageHandler? handler = null) {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = timeout;
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    private static string ResolveVersion() {
        // InformationalVersion carries what -p:Version was set to, with "+<commit sha>" appended
        // by the SDK's source-link defaults. The suffix is noise in a user agent and would make
        // every development build look like a distinct release.
        var informational = typeof(CloudHttp).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var version = string.IsNullOrWhiteSpace(informational)
            ? typeof(CloudHttp).Assembly.GetName().Version?.ToString()
            : informational;

        if (string.IsNullOrWhiteSpace(version)) return "0";

        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
