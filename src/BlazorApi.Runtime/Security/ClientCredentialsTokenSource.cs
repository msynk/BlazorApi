using System.Text.Json;

namespace BlazorApi.Runtime.Security;

/// <summary>
/// Obtains tokens with the OAuth 2.0 client credentials grant (RFC 6749 section 4.4). Wrap it in a
/// <see cref="CachingAccessTokenProvider"/> to reuse tokens until they expire.
/// </summary>
public sealed class ClientCredentialsTokenSource
{
    private readonly HttpClient _httpClient;
    private readonly Uri _tokenEndpoint;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly TimeProvider _time;

    /// <summary>Creates the token source.</summary>
    public ClientCredentialsTokenSource(HttpClient httpClient, Uri tokenEndpoint, string clientId, string clientSecret, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenEndpoint = tokenEndpoint ?? throw new ArgumentNullException(nameof(tokenEndpoint));
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        _clientSecret = clientSecret ?? throw new ArgumentNullException(nameof(clientSecret));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Requests a token.</summary>
    /// <exception cref="HttpRequestException">The token endpoint rejected the request (its body is not included, as it may echo secrets).</exception>
    public async ValueTask<AccessToken?> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", _clientId),
            new("client_secret", _clientSecret),
        };
        if (scopes is { Count: > 0 })
        {
            fields.Add(new("scope", string.Join(" ", scopes)));
        }

        using var content = new FormUrlEncodedContent(fields);
        using var response = await _httpClient.PostAsync(_tokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The token endpoint returned {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("access_token", out var accessToken) || accessToken.ValueKind != JsonValueKind.String)
            {
                throw new HttpRequestException("The token endpoint response has no access_token.");
            }

            DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt64(out var seconds)
                ? _time.GetUtcNow().AddSeconds(seconds)
                : null;
            return new AccessToken(accessToken.GetString()!, expiresAt);
        }
    }
}
