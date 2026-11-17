using System.Text;
using BlazorApi.OpenApi.IO;
using BlazorApi.OpenApi.References;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.Cli;

/// <summary>
/// Reads the documents of a contract given by URL: the contract itself and the documents it references on the same
/// origin (scheme, host and port). Documents on other origins are refused, so a contract cannot make the tool fetch
/// arbitrary addresses; local files are never read for a remote contract (the reference policy rejects them).
/// </summary>
internal sealed class RemoteDocumentResolver : IDocumentResolver
{
    private readonly HttpClient _http;
    private readonly Uri _origin;

    public RemoteDocumentResolver(HttpClient http, Uri contract)
    {
        _http = http;
        _origin = new Uri(contract.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>Whether a contract argument is an http or https URL.</summary>
    public static bool IsUrl(string? contract) =>
        contract is not null && Uri.TryCreate(contract, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public bool TryRead(Uri uri, out string content, out string? error)
    {
        content = string.Empty;
        if (uri.Scheme is not ("http" or "https") || !string.Equals(uri.GetLeftPart(UriPartial.Authority), _origin.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            error = uri.IsFile
                ? "a remote contract cannot reference local files"
                : $"only documents from {_origin.AbsoluteUri.TrimEnd('/')} are downloaded";
            return false;
        }

        try
        {
            content = DownloadAsync(uri).GetAwaiter().GetResult();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException)
        {
            error = ex.Message;
            return false;
        }
    }

    private async Task<string> DownloadAsync(Uri uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/vnd.oai.openapi+json, application/json, application/yaml, text/yaml, */*;q=0.5");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GET {uri} returned {(int)response.StatusCode} ({response.ReasonPhrase})");
        }

        var limit = ParserLimits.Default.MaxDocumentLength;
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new InvalidDataException($"{uri} is larger than {limit} characters");
        }

        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[81920];
            var builder = new StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                builder.Append(buffer, 0, read);
                if (builder.Length > limit)
                {
                    throw new InvalidDataException($"{uri} is larger than {limit} characters");
                }
            }

            return builder.ToString();
        }
    }
}
