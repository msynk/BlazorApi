namespace BlazorApi.Runtime.Http;

/// <summary>Reads RFC 8288 <c>Link</c> headers (used by APIs that page with <c>rel="next"</c> links).</summary>
public static class ApiLinkHeader
{
    /// <summary>Returns the URL of the link with the given relation (for example <c>next</c>), or <see langword="null"/>.</summary>
    public static string? GetLink(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string relation)
    {
        if (headers is null || !headers.TryGetValue("Link", out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            foreach (var link in Split(value))
            {
                var open = link.IndexOf('<', StringComparison.Ordinal);
                var close = link.IndexOf('>', StringComparison.Ordinal);
                if (open < 0 || close < open)
                {
                    continue;
                }

                var url = link.Substring(open + 1, close - open - 1);
                foreach (var parameter in link.Substring(close + 1).Split(';'))
                {
                    var pair = parameter.Split(['='], 2);
                    if (pair.Length == 2 && pair[0].Trim().Equals("rel", StringComparison.OrdinalIgnoreCase) &&
                        pair[1].Trim().Trim('"').Split(' ').Contains(relation, StringComparer.OrdinalIgnoreCase))
                    {
                        return url;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the value of a query parameter in the link with the given relation (by default <c>next</c>), or
    /// <see langword="null"/> when there is no such link or parameter.
    /// </summary>
    public static string? GetQueryParameter(IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string parameter, string relation = "next")
    {
        var url = GetLink(headers, relation);
        if (url is null)
        {
            return null;
        }

        var query = url.IndexOf('?', StringComparison.Ordinal);
        if (query < 0)
        {
            return null;
        }

        var fragment = url.IndexOf('#', query);
        var text = fragment < 0 ? url.Substring(query + 1) : url.Substring(query + 1, fragment - query - 1);
        foreach (var pair in text.Split('&'))
        {
            var parts = pair.Split(['='], 2);
            if (Uri.UnescapeDataString(parts[0].Replace('+', ' ')) == parameter)
            {
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            }
        }

        return null;
    }

    /// <summary>Splits a header value at commas that separate links (commas inside <c>&lt;...&gt;</c> or quotes are kept).</summary>
    private static IEnumerable<string> Split(string value)
    {
        var start = 0;
        var inUrl = false;
        var inQuotes = false;
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '<' when !inQuotes:
                    inUrl = true;
                    break;
                case '>' when !inQuotes:
                    inUrl = false;
                    break;
                case '"' when !inUrl:
                    inQuotes = !inQuotes;
                    break;
                case ',' when !inUrl && !inQuotes:
                    yield return value.Substring(start, i - start);
                    start = i + 1;
                    break;
            }
        }

        yield return value.Substring(start);
    }
}
