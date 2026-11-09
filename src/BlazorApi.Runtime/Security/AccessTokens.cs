namespace BlazorApi.Runtime.Security;

/// <summary>An access token and when it expires.</summary>
public sealed class AccessToken
{
    /// <summary>Creates a token.</summary>
    public AccessToken(string value, DateTimeOffset? expiresAt = null)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        ExpiresAt = expiresAt;
    }

    /// <summary>The token value. Never log it.</summary>
    public string Value { get; }

    /// <summary>When the token expires; <see langword="null"/> when unknown.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Hides the token value.</summary>
    public override string ToString() => "AccessToken(****)";
}

/// <summary>Provides access tokens for bearer authentication (OAuth 2.0, OpenID Connect, JWT).</summary>
public interface IAccessTokenProvider
{
    /// <summary>Gets a token for the given scopes, or <see langword="null"/> when none is available (the request is sent without one).</summary>
    ValueTask<AccessToken?> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken);

    /// <summary>
    /// Discards a token the server rejected (401) so the next call obtains a new one. Providers that cannot refresh
    /// return <see langword="false"/> and the request is not retried.
    /// </summary>
    ValueTask<bool> InvalidateAsync(AccessToken token, CancellationToken cancellationToken);
}

/// <summary>
/// Caches tokens from a token source and refreshes them shortly before they expire. Concurrent callers share a single
/// refresh, so a burst of requests causes one token request.
/// </summary>
public sealed class CachingAccessTokenProvider : IAccessTokenProvider, IDisposable
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, ValueTask<AccessToken?>> _source;
    private readonly TimeSpan _refreshBefore;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, AccessToken> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates a caching provider.</summary>
    /// <param name="source">Obtains a new token for a set of scopes.</param>
    /// <param name="refreshBefore">How long before expiry a token is refreshed. Default: 60 seconds.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public CachingAccessTokenProvider(
        Func<IReadOnlyList<string>, CancellationToken, ValueTask<AccessToken?>> source,
        TimeSpan? refreshBefore = null,
        TimeProvider? timeProvider = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _refreshBefore = refreshBefore ?? TimeSpan.FromSeconds(60);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<AccessToken?> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        var key = string.Join(" ", (scopes ?? Array.Empty<string>()).OrderBy(s => s, StringComparer.Ordinal));
        if (TryGetFresh(key, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetFresh(key, out cached))
            {
                return cached;
            }

            var token = await _source(scopes ?? Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
            lock (_cache)
            {
                if (token is not null)
                {
                    _cache[key] = token;
                }
                else
                {
                    _cache.Remove(key);
                }
            }

            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> InvalidateAsync(AccessToken token, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_cache)
            {
                foreach (var entry in _cache.Where(e => ReferenceEquals(e.Value, token)).ToList())
                {
                    _cache.Remove(entry.Key);
                }
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private bool TryGetFresh(string key, out AccessToken? token)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var found) &&
                (found.ExpiresAt is null || found.ExpiresAt.Value - _refreshBefore > _time.GetUtcNow()))
            {
                token = found;
                return true;
            }
        }

        token = null;
        return false;
    }
}

/// <summary>A token provider returning a fixed token (for tests, scripts and development).</summary>
public sealed class StaticAccessTokenProvider : IAccessTokenProvider
{
    private readonly AccessToken _token;

    /// <summary>Creates the provider.</summary>
    public StaticAccessTokenProvider(string token) => _token = new AccessToken(token);

    /// <inheritdoc />
    public ValueTask<AccessToken?> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken) => new(_token);

    /// <inheritdoc />
    public ValueTask<bool> InvalidateAsync(AccessToken token, CancellationToken cancellationToken) => new(false);
}
