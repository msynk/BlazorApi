using System.Text.Json;
using BlazorApi.Runtime.Json;

namespace BlazorApi.Runtime;

/// <summary>Options shared by the operations of a generated API client.</summary>
public class ApiClientOptions
{
    private JsonSerializerOptions _jsonSerializerOptions = ApiJson.CreateOptions();

    /// <summary>
    /// The base address of the API. When not set, the <see cref="HttpClient.BaseAddress"/> is used, then the first
    /// server URL of the OpenAPI document.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// The JSON options for request and response bodies. The default reads and writes property names exactly as
    /// the generated <c>[JsonPropertyName]</c> attributes say and uses <see cref="JsonSerializerDefaults.Web"/>
    /// number handling. Use <see cref="ApiJson.CreateOptions"/> as a starting point to keep those defaults.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions
    {
        get => _jsonSerializerOptions;
        set => _jsonSerializerOptions = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// A timeout for each request, including reading the response body. A request that times out yields an
    /// <see cref="ApiErrorKind.Timeout"/> error. <see langword="null"/> (the default) relies on
    /// <see cref="HttpClient.Timeout"/>.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Headers added to every request (for example a <c>User-Agent</c>).</summary>
    public IDictionary<string, string> DefaultHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called before each request is sent (for example to add correlation headers).</summary>
    public Func<HttpRequestMessage, CancellationToken, ValueTask>? OnRequest { get; set; }

    /// <summary>Called after each response arrives, before its body is read.</summary>
    public Func<HttpResponseMessage, CancellationToken, ValueTask>? OnResponse { get; set; }

    /// <summary>
    /// The maximum number of characters of an error response body kept in <see cref="ApiError.Body"/>. Default:
    /// 65,536.
    /// </summary>
    public int MaxErrorBodyLength { get; set; } = 64 * 1024;
}
