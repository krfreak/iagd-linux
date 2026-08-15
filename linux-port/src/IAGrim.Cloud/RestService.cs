using System.Net;
using System.Text;

namespace IAGrim.Cloud;

/// <summary>
/// Thrown for any non-2xx reply. Upstream's <c>Backup/Cloud/Util/HttpException.cs</c>.
///
/// The status code is what the worker loop reads: a 401 means the token died and the client
/// should log itself out rather than keep retrying with a credential the server has forgotten.
/// </summary>
public sealed class CloudHttpException : Exception {
    public CloudHttpException() : base("Error") { }

    public CloudHttpException(int code, string message) : base(message) => Code = code;

    /// <summary>The HTTP status, or 0 when the call site did not record one — as upstream's does not, on POST.</summary>
    public int Code { get; }
}

/// <summary>
/// The thin HTTP wrapper every cloud call goes through — upstream's
/// <c>Utilities/HelperClasses/RestService.cs</c>.
///
/// Two of its quirks are reproduced rather than tidied, because tidying them would change what
/// this port asks of the server:
///
///   * <see cref="Post{T}"/> throws a <see cref="CloudHttpException"/> with <b>no status code</b>
///     on failure, where <see cref="Get{T}"/> carries one. The worker loop keys "log out" off
///     that code, so a failed upload never logs the user out and a failed download can.
///   * <see cref="Delete"/> takes a JSON body and <b>does not send it</b>. The account-deletion
///     endpoint ignores the body anyway, so the argument exists purely to match the call site.
/// </summary>
public sealed class RestService : IDisposable {
    private readonly HttpClient _client;

    public RestService(HttpClient client) {
        _client = client;
        _client.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    }

    public T Get<T>(string url) {
        var result = _client.GetAsync(url).GetAwaiter().GetResult();
        if (!result.IsSuccessStatusCode) {
            throw new CloudHttpException((int)result.StatusCode, "Error");
        }

        var body = result.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return CloudJson.Deserialize<T>(body)!;
    }

    /// <summary>The status code alone. Used to check that a buddy id exists before subscribing.</summary>
    public HttpStatusCode VerifyGet(string url) =>
        _client.GetAsync(url).GetAwaiter().GetResult().StatusCode;

    public T Post<T>(string url, string json) {
        var result = _client
            .PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"))
            .GetAwaiter().GetResult();

        if (!result.IsSuccessStatusCode) {
            throw new CloudHttpException();
        }

        var body = result.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return CloudJson.Deserialize<T>(body)!;
    }

    /// <summary>Whether the POST succeeded. Nothing is read from the reply.</summary>
    public bool Post(string url, string json) =>
        _client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"))
            .GetAwaiter().GetResult()
            .IsSuccessStatusCode;

    /// <summary>
    /// A DELETE. <paramref name="json"/> is accepted and discarded, as upstream's is — the
    /// request goes out with no body.
    /// </summary>
    public bool Delete(string url, string json) =>
        _client.DeleteAsync(url).GetAwaiter().GetResult().IsSuccessStatusCode;

    public void Dispose() => _client.Dispose();
}
