using System.Text.Json;
using System.Text.Json.Serialization;

namespace IAGrim.Cloud;

/// <summary>
/// The two JSON shapes upstream puts on the wire, reproduced with System.Text.Json.
///
/// Upstream serialises with Newtonsoft, and — this is the part that is easy to get wrong — it
/// does **not** use the same settings for both transports:
///
///   * <c>CloudSyncService.Save</c> and <c>.Delete</c> call
///     <c>JsonConvert.SerializeObject(items)</c> with *no* settings, so the REST upload carries
///     the property names as declared: <c>BaseRecord</c>, <c>Id</c>, <c>StackCount</c>. Nulls
///     are written as <c>null</c>.
///   * <c>WebSocketSyncService</c> passes settings with a camel-case resolver and
///     <c>NullValueHandling.Ignore</c>, so live-sync frames carry <c>baseRecord</c>,
///     <c>id</c>, <c>stackCount</c>, and drop null fields entirely.
///
/// Both work because the Go server decodes with <c>encoding/json</c>, which matches struct tags
/// case-insensitively. That is a property of the server, not a coincidence worth relying on
/// blindly, so <see cref="Upload"/> keeps upstream's casing rather than "fixing" it: if the
/// server is ever swapped for something stricter, this port fails in the same way the Windows
/// tool does instead of being the only client that still works.
///
/// Reading is case-insensitive in both directions, as Newtonsoft is by default. The server
/// answers in camelCase.
/// </summary>
public static class CloudJson {
    /// <summary>REST bodies: declared (Pascal) casing, nulls included.</summary>
    public static readonly JsonSerializerOptions Upload = new() {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Websocket frames: camelCase, nulls omitted.</summary>
    public static readonly JsonSerializerOptions Live = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Responses, from either transport.</summary>
    public static readonly JsonSerializerOptions Read = new() {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static string SerializeUpload<T>(T value) => JsonSerializer.Serialize(value, Upload);
    public static string SerializeLive<T>(T value) => JsonSerializer.Serialize(value, Live);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Read);
}
