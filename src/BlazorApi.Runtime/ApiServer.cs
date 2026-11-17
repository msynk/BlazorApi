namespace BlazorApi.Runtime;

/// <summary>
/// A server of an OpenAPI document: a URL template with variables (<c>https://{region}.api.example.com/v1</c>).
/// Generated clients list their document's servers in a static <c>Servers</c> property; use
/// <see cref="Resolve"/> to build a base address for <see cref="ApiClientOptions.BaseAddress"/>.
/// </summary>
public sealed class ApiServer
{
    private readonly IReadOnlyDictionary<string, string> _defaults;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _allowed;

    /// <summary>Creates a server.</summary>
    /// <param name="urlTemplate">The URL template.</param>
    /// <param name="description">The description.</param>
    /// <param name="defaults">The default value of each variable.</param>
    /// <param name="allowed">The allowed values of variables that restrict them (<c>enum</c>).</param>
    public ApiServer(
        string urlTemplate,
        string? description = null,
        IReadOnlyDictionary<string, string>? defaults = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? allowed = null)
    {
        UrlTemplate = urlTemplate ?? throw new ArgumentNullException(nameof(urlTemplate));
        Description = description;
        _defaults = defaults ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _allowed = allowed ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    }

    /// <summary>The URL template.</summary>
    public string UrlTemplate { get; }

    /// <summary>The description from the OpenAPI document.</summary>
    public string? Description { get; }

    /// <summary>The variable names with their default values.</summary>
    public IReadOnlyDictionary<string, string> Variables => _defaults;

    /// <summary>The allowed values of a variable; empty when any value is allowed.</summary>
    public IReadOnlyList<string> AllowedValues(string variable) =>
        _allowed.TryGetValue(variable, out var values) ? values : Array.Empty<string>();

    /// <summary>The address with every variable at its default value.</summary>
    public Uri DefaultAddress => Resolve();

    /// <summary>Builds the address, replacing variables with the given values or their defaults.</summary>
    /// <exception cref="ArgumentException">A variable is unknown or a value is not one of its allowed values.</exception>
    /// <exception cref="UriFormatException">The result is not an absolute URL (a relative server URL).</exception>
    public Uri Resolve(IReadOnlyDictionary<string, string>? variables = null)
    {
        var url = UrlTemplate;
        if (variables is not null)
        {
            foreach (var variable in variables)
            {
                if (!_defaults.ContainsKey(variable.Key))
                {
                    throw new ArgumentException($"The server '{UrlTemplate}' has no variable '{variable.Key}'.", nameof(variables));
                }

                var allowed = AllowedValues(variable.Key);
                if (allowed.Count > 0 && !allowed.Contains(variable.Value, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"'{variable.Value}' is not an allowed value of the server variable '{variable.Key}' ({string.Join(", ", allowed)}).", nameof(variables));
                }
            }
        }

        var authorityEnd = AuthorityEnd(UrlTemplate);
        foreach (var variable in _defaults)
        {
            var value = variables is not null && variables.TryGetValue(variable.Key, out var given) ? given : variable.Value;
            var placeholder = "{" + variable.Key + "}";
            var index = UrlTemplate.IndexOf(placeholder, StringComparison.Ordinal);
            var inAuthority = index >= 0 && index < authorityEnd;
            // A value must not move the request to another host or add a query, fragment or credentials.
            if (inAuthority ? value.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '.' or ':' or '[' or ']')) : value.IndexOfAny(['?', '#', '\\']) >= 0 || value.Contains("..", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"'{value}' is not a valid value of the server variable '{variable.Key}' (it would change the {(inAuthority ? "host" : "path")} unexpectedly).", nameof(variables));
            }

            url = url.Replace(placeholder, value, StringComparison.Ordinal);
        }

        return new Uri(url, UriKind.Absolute);
    }

    /// <summary>The index where the authority (host and port) of a URL template ends.</summary>
    private static int AuthorityEnd(string template)
    {
        var scheme = template.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return 0;
        }

        var slash = template.IndexOf('/', scheme + 3);
        return slash < 0 ? template.Length : slash;
    }

    /// <inheritdoc />
    public override string ToString() => UrlTemplate;
}
