using System.Text.Json;
using BlazorApi.Runtime.Json;

namespace BlazorApi.Runtime;

/// <summary>Options shared by the operations of a generated API client.</summary>
public class ApiClientOptions
{
    private JsonSerializerOptions _jsonSerializerOptions = ApiJson.CreateOptions();

    /// <summary>Creates options with default values.</summary>
    public ApiClientOptions()
    {
    }

    /// <summary>Creates a copy of other options (see <see cref="Clone"/>).</summary>
    protected ApiClientOptions(ApiClientOptions other)
    {
        if (other is null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        BaseAddress = other.BaseAddress;
        _jsonSerializerOptions = other._jsonSerializerOptions;
        Timeout = other.Timeout;
        OnRequest = other.OnRequest;
        OnResponse = other.OnResponse;
        MaxErrorBodyLength = other.MaxErrorBodyLength;
        foreach (var header in other.DefaultHeaders)
        {
            DefaultHeaders[header.Key] = header.Value;
        }

        foreach (var address in other.OperationBaseAddresses)
        {
            OperationBaseAddresses[address.Key] = address.Value;
        }
    }

    /// <summary>
    /// The base address of the API. When not set, the <see cref="HttpClient.BaseAddress"/> is used, then the first
    /// server URL of the OpenAPI document.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// The JSON options for request and response bodies. The default reads and writes property names exactly as
    /// the generated <c>[JsonPropertyName]</c> attributes say and uses <see cref="JsonSerializerDefaults.Web"/>
    /// number handling. Use <see cref="ApiJson.CreateOptions()"/> as a starting point to keep those defaults.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions
    {
        get => _jsonSerializerOptions;
        set => _jsonSerializerOptions = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// A timeout for each request, including reading the response body when the client reads it (JSON, text, error
    /// bodies). Streamed and file bodies (<see cref="ApiStream{T}"/>, <see cref="FileResponse"/>) are read by the caller
    /// after the call returns and are bounded only by the cancellation token passed while reading them. A request that
    /// times out yields an <see cref="ApiErrorKind.Timeout"/> error. <see langword="null"/> (the default) relies on
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

    /// <summary>
    /// Base addresses for individual operations, by operation id. They replace the server an operation declares
    /// in the OpenAPI document (operation-level <c>servers</c>), which otherwise wins over <see cref="BaseAddress"/>.
    /// </summary>
    public IDictionary<string, Uri> OperationBaseAddresses { get; } = new Dictionary<string, Uri>(StringComparer.Ordinal);

    /// <summary>
    /// Creates a copy of these options. Headers and operation base addresses are copied; the JSON options and the
    /// callbacks are shared. Generated clients use it in <c>WithOptions</c> to derive per-call settings. Derived
    /// option classes override it (with the copy constructor) to copy their own settings.
    /// </summary>
    public virtual ApiClientOptions Clone() => new(this);
}
