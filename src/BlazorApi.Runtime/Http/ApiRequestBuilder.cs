using System.Text;

namespace BlazorApi.Runtime.Http;

/// <summary>Builds the <see cref="HttpRequestMessage"/> of one operation call. Used by generated clients.</summary>
public sealed class ApiRequestBuilder
{
    private readonly string _pathTemplate;
    private readonly Dictionary<string, string> _pathValues = new(StringComparer.Ordinal);
    private readonly List<string> _query = [];
    private readonly List<KeyValuePair<string, string>> _headers = [];
    private readonly List<string> _cookies = [];
    private readonly List<string> _accept = [];

    /// <summary>Creates a builder for a path template such as <c>/pets/{petId}</c>.</summary>
    public ApiRequestBuilder(HttpMethod method, string pathTemplate, string operationId)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        _pathTemplate = pathTemplate ?? throw new ArgumentNullException(nameof(pathTemplate));
        OperationId = operationId;
    }

    /// <summary>The HTTP method.</summary>
    public HttpMethod Method { get; }

    /// <summary>The operation identifier (for diagnostics, logging and mocks).</summary>
    public string OperationId { get; }

    /// <summary>The path template.</summary>
    public string PathTemplate => _pathTemplate;

    /// <summary>The request body.</summary>
    public HttpContent? Content { get; set; }

    /// <summary>The security requirements of the operation, consumed by authentication handlers.</summary>
    public IReadOnlyList<ApiSecurityRequirement>? Security { get; set; }

    /// <summary>Sets a path parameter.</summary>
    public ApiRequestBuilder Path(string name, ParameterValue value, ParameterStyle style = ParameterStyle.Simple, bool explode = false)
    {
        _pathValues[name] = ParameterSerializer.Path(name, value, style, explode);
        return this;
    }

    /// <summary>Adds a query parameter (omitted when the value is <see cref="ParameterValue.None"/>).</summary>
    public ApiRequestBuilder Query(string name, ParameterValue value, ParameterStyle style = ParameterStyle.Form, bool explode = true, bool allowReserved = false)
    {
        ParameterSerializer.Query(_query, name, value, style, explode, allowReserved);
        return this;
    }

    /// <summary>Adds a header parameter (omitted when the value is <see cref="ParameterValue.None"/>).</summary>
    public ApiRequestBuilder Header(string name, ParameterValue value, bool explode = false)
    {
        if (ParameterSerializer.Header(value, explode) is { } text)
        {
            _headers.Add(new KeyValuePair<string, string>(name, text));
        }

        return this;
    }

    /// <summary>Adds a cookie parameter (omitted when the value is <see cref="ParameterValue.None"/>).</summary>
    public ApiRequestBuilder Cookie(string name, ParameterValue value, bool explode = true)
    {
        _cookies.AddRange(ParameterSerializer.Cookie(name, value, explode));
        return this;
    }

    /// <summary>Adds an accepted response media type.</summary>
    public ApiRequestBuilder Accept(string mediaType)
    {
        if (!_accept.Contains(mediaType))
        {
            _accept.Add(mediaType);
        }

        return this;
    }

    /// <summary>Builds the relative URI (path and query).</summary>
    public string BuildRelativeUri()
    {
        var path = new StringBuilder(_pathTemplate.Length + 16);
        var i = 0;
        while (i < _pathTemplate.Length)
        {
            var c = _pathTemplate[i];
            if (c == '{')
            {
                var end = _pathTemplate.IndexOf('}', i + 1);
                if (end > i)
                {
                    var name = _pathTemplate.Substring(i + 1, end - i - 1);
                    if (!_pathValues.TryGetValue(name, out var value))
                    {
                        throw new InvalidOperationException($"The path parameter '{name}' of operation '{OperationId}' has no value.");
                    }

                    path.Append(value);
                    i = end + 1;
                    continue;
                }
            }

            path.Append(c);
            i++;
        }

        if (_query.Count > 0)
        {
            path.Append('?').Append(string.Join("&", _query));
        }

        return path.ToString();
    }

    /// <summary>Creates the request message against a base address.</summary>
    public HttpRequestMessage Build(Uri? baseAddress)
    {
        var relative = BuildRelativeUri();
        Uri uri;
        if (baseAddress is null)
        {
            uri = new Uri(relative, UriKind.Relative);
        }
        else
        {
            var root = baseAddress.AbsoluteUri;
            var queryIndex = root.IndexOf('?', StringComparison.Ordinal);
            if (queryIndex >= 0)
            {
                root = root.Substring(0, queryIndex);
            }

            uri = new Uri(root.TrimEnd('/') + (relative.StartsWith('/') ? relative : "/" + relative), UriKind.Absolute);
        }

        var request = new HttpRequestMessage(Method, uri) { Content = Content };
        foreach (var header in _headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (_cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies));
        }

        foreach (var mediaType in _accept)
        {
            request.Headers.Accept.ParseAdd(mediaType);
        }

        request.Options.Set(ApiRequestOptionKeys.OperationId, OperationId);
        if (Security is not null)
        {
            request.Options.Set(ApiRequestOptionKeys.Security, Security);
        }

        return request;
    }
}

/// <summary>The keys of the metadata generated clients attach to <see cref="HttpRequestMessage.Options"/>.</summary>
public static class ApiRequestOptionKeys
{
    /// <summary>The operation identifier.</summary>
    public static readonly HttpRequestOptionsKey<string> OperationId = new("BlazorApi.OperationId");

    /// <summary>
    /// The operation's security requirements (alternatives). Authentication handlers apply only when their scheme is
    /// required; an empty list means the operation needs no authentication.
    /// </summary>
    public static readonly HttpRequestOptionsKey<IReadOnlyList<ApiSecurityRequirement>> Security = new("BlazorApi.Security");
}

/// <summary>One security requirement of an operation: all listed schemes apply together.</summary>
public sealed class ApiSecurityRequirement
{
    /// <summary>Creates a requirement.</summary>
    public ApiSecurityRequirement(IReadOnlyDictionary<string, IReadOnlyList<string>> schemes) =>
        Schemes = schemes ?? throw new ArgumentNullException(nameof(schemes));

    /// <summary>Creates a requirement from scheme names without scopes.</summary>
    public ApiSecurityRequirement(params string[] schemes)
        : this(schemes.ToDictionary(s => s, _ => (IReadOnlyList<string>)Array.Empty<string>(), StringComparer.Ordinal))
    {
    }

    /// <summary>The scheme names and their required scopes.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Schemes { get; }

    /// <summary>
    /// Whether a scheme applies to a request: true when the request carries no security metadata (not created by a
    /// generated client) or when any requirement names the scheme.
    /// </summary>
    public static bool Applies(HttpRequestMessage request, string scheme, out IReadOnlyList<string> scopes)
    {
        scopes = Array.Empty<string>();
        if (request is null || !request.Options.TryGetValue(ApiRequestOptionKeys.Security, out var requirements))
        {
            return true;
        }

        foreach (var requirement in requirements)
        {
            if (requirement.Schemes.TryGetValue(scheme, out var found))
            {
                scopes = found;
                return true;
            }
        }

        return false;
    }
}
