using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using BlazorApi.OpenApi.Examples;
using BlazorApi.OpenApi.Model;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.Cli;

/// <summary>
/// A mock HTTP server for a contract (<c>api-gen mock</c>): every operation answers with the example of its response,
/// or a value synthesized from the schema. Clients choose the response with <c>Prefer: code=404</c> and
/// <c>Prefer: example=name</c> (the Prism convention). CORS is open so Blazor WebAssembly apps can call it. It speaks
/// HTTP/1.1 over a plain socket (one request per connection), so it needs no URL reservation or elevation. Requests
/// are validated against the contract first (<see cref="RequestValidator"/>): missing credentials answer 401, an
/// unsupported content type 415, and invalid parameters or bodies 400 with the problems listed.
/// </summary>
internal sealed class MockServer
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const long MaxBodyBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _connections = new(64, 64);
    private readonly HashSet<string> _allowedHosts;
    private readonly ApiModel _model;
    private readonly OpenApiExamples _examples;
    private readonly TextWriter _log;
    private readonly List<(ApiOperation Operation, Regex Pattern, string[] Names)> _routes;
    private readonly bool _validate;
    private readonly string _basePath;

    /// <param name="model">The API.</param>
    /// <param name="log">Receives one line per request.</param>
    /// <param name="allowedHosts">
    /// Accepted <c>Host</c> header names (without port), or <see langword="null"/> to accept any: a loopback server
    /// rejects other names so web pages cannot reach it through DNS rebinding.
    /// </param>
    /// <param name="validate">Whether requests are validated against the contract.</param>
    public MockServer(ApiModel model, TextWriter log, IEnumerable<string>? allowedHosts = null, bool validate = true)
    {
        _validate = validate;
        _allowedHosts = allowedHosts is null ? [] : new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
        _model = model;
        _log = log;
        _examples = new OpenApiExamples(model.Document);
        _routes = model.Operations.Select(o => (o, Route(o.Path, out var names), names)).ToList();
        var server = model.Servers.FirstOrDefault()?.DefaultUrl;
        _basePath = server is not null && Uri.TryCreate(server, UriKind.Absolute, out var uri) ? uri.AbsolutePath.TrimEnd('/')
            : server is not null && server.StartsWith("/", StringComparison.Ordinal) ? server.TrimEnd('/') : string.Empty;
    }

    /// <summary>Serves until cancelled.</summary>
    public async Task RunAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(listener.Stop);
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException && cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue; // A client reset its connection while it was accepted: keep serving the others.
            }

            if (!await _connections.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                client.Dispose(); // Too many open connections: refuse rather than queue without bound.
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(client, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _connections.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(RequestTimeout); // A client that never finishes its request does not hold the socket.
            cancellationToken = timeout.Token;
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                var response = Respond(request);
                lock (_log)
                {
                    _log.WriteLine($"{request.Method} {request.Target} -> {response.Status}{(response.OperationId is null ? string.Empty : " (" + response.OperationId + ")")}");
                }

                await WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // The client went away or the server is stopping.
            }
        }
    }

    // ------------------------------------------------------------------ routing and responses

    internal sealed record Request(string Method, string Target, Dictionary<string, string> Headers)
    {
        /// <summary>The request body (decoded when it was chunked).</summary>
        public byte[] Body { get; init; } = [];
    }

    internal sealed record Response(int Status, string? ContentType, byte[] Body, Dictionary<string, string> Headers, string? OperationId);

    internal Response Respond(Request request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_allowedHosts.Count > 0 &&
            !(request.Headers.TryGetValue("Host", out var host) && _allowedHosts.Contains(HostName(host))))
        {
            return Problem(421, "The Host header names a host this mock server does not serve (DNS rebinding protection).", null);
        }

        if (request.Method == "OPTIONS" && request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            return new Response(204, null, [], headers, null);
        }

        var path = Uri.UnescapeDataString(request.Target.Split('?')[0]);
        if (_basePath.Length > 0 && path.StartsWith(_basePath + "/", StringComparison.Ordinal))
        {
            path = path.Substring(_basePath.Length);
        }

        var matches = _routes.Where(r => r.Pattern.IsMatch(path)).ToList();
        if (matches.Count == 0)
        {
            return Problem(404, $"No operation of the contract matches the path '{path}'.", null);
        }

        // Literal segments win over variables (/pets/mine before /pets/{id}).
        var route = matches
            .Where(r => string.Equals(r.Operation.Method, request.Method, StringComparison.Ordinal))
            .OrderBy(r => r.Operation.Path.Count(c => c == '{'))
            .FirstOrDefault();
        var operation = route.Operation;
        if (operation is null)
        {
            headers["Allow"] = string.Join(", ", matches.Select(r => r.Operation.Method).Distinct());
            return Problem(405, $"'{path}' does not support {request.Method}.", null) with { Headers = headers };
        }

        if (_validate)
        {
            var match = route.Pattern.Match(path);
            var pathValues = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < route.Names.Length; i++)
            {
                pathValues[route.Names[i]] = match.Groups["p" + i.ToString(CultureInfo.InvariantCulture)].Value;
            }

            if (RequestValidator.Validate(operation, request.Method, request.Target, request.Headers, request.Body, pathValues) is { } failure)
            {
                var problem = Problem(failure.Status, failure.Detail, operation, failure.Violations);
                if (failure.Challenge is not null)
                {
                    problem.Headers["WWW-Authenticate"] = failure.Challenge;
                }

                return problem;
            }
        }

        var prefer = request.Headers.TryGetValue("Prefer", out var preferHeader) ? ParsePrefer(preferHeader) : new Dictionary<string, string>();
        ApiResponse? response;
        if (prefer.TryGetValue("code", out var code) && code.Length == 3 && code.All(char.IsDigit))
        {
            response = operation.Responses.FirstOrDefault(r => r.StatusCode == code) ??
                operation.Responses.FirstOrDefault(r => r.StatusCode.Length == 3 && r.StatusCode[0] == code[0] && r.StatusCode.EndsWith("XX", StringComparison.OrdinalIgnoreCase)) ??
                operation.Responses.FirstOrDefault(r => r.IsDefault);
            if (response is null)
            {
                return Problem(422, $"{OperationName(operation)} declares no {code} response.", operation);
            }
        }
        else
        {
            response = operation.Responses.FirstOrDefault(r => r.IsSuccess) ?? operation.Responses.FirstOrDefault();
        }

        var status = response?.Status ?? (code is { Length: 3 } && int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : response?.StatusClass is { } statusClass ? statusClass * 100 : 200);
        if (response is null)
        {
            return new Response(status, null, [], headers, OperationName(operation));
        }

        foreach (var header in response.Headers.Where(h => h.Required))
        {
            headers[header.Name] = header.Type is ApiPrimitiveType { Kind: ApiPrimitiveKind.Integer or ApiPrimitiveKind.Number } ? "1" : "example";
        }

        var content = Choose(response.Contents, request.Headers.TryGetValue("Accept", out var accept) ? accept : null);
        if (content is null || status is 204 or 304 || request.Method == "HEAD")
        {
            return new Response(status, null, [], headers, OperationName(operation));
        }

        var example = _examples.ForMediaType(content.Source, prefer.TryGetValue("example", out var name) ? name : null);
        var mediaType = content.MediaType.Contains('*') ? "application/octet-stream" : content.MediaType;
        var body = content.Kind switch
        {
            ApiBodyKind.EventStream => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 3).Select(i => $"id: {i}\ndata: {Json(example, text: true)}\n\n"))),
            ApiBodyKind.JsonSequence => Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 3).Select(_ => Json(example, text: false) + "\n"))),
            ApiBodyKind.Json => Encoding.UTF8.GetBytes(Json(example, text: false)),
            ApiBodyKind.Binary => example is ScalarNode { IsString: true } bytes ? Encoding.UTF8.GetBytes(bytes.Value) : [0x42, 0x41, 0x50, 0x49],
            _ => Encoding.UTF8.GetBytes(example is ScalarNode scalar ? scalar.Value : Json(example, text: false)),
        };
        return new Response(status, mediaType, body, headers, OperationName(operation));
    }

    private static ApiContent? Choose(IReadOnlyList<ApiContent> contents, string? accept)
    {
        if (contents.Count == 0)
        {
            return null;
        }

        if (accept is not null)
        {
            foreach (var range in accept.Split(',').Select(a => a.Split(';')[0].Trim()))
            {
                var match = contents.FirstOrDefault(c => string.Equals(c.MediaType, range, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return contents.FirstOrDefault(c => c.Kind == ApiBodyKind.Json) ?? contents[0];
    }

    private static string Json(DocNode? node, bool text) =>
        node is null ? "null" : text && node is ScalarNode { IsString: true } s ? s.Value : node.ToJson();

    private static string OperationName(ApiOperation operation) => operation.OperationId ?? operation.ToString();

    private static Response Problem(int status, string detail, ApiOperation? operation, IReadOnlyList<RequestValidator.Violation>? errors = null)
    {
        var body = new StringBuilder("{\"title\":");
        AppendJsonString(body, status switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            415 => "Unsupported Media Type",
            _ => "Mock error",
        });
        body.Append(",\"status\":").Append(status.ToString(CultureInfo.InvariantCulture)).Append(",\"detail\":");
        AppendJsonString(body, detail);
        if (errors is { Count: > 0 })
        {
            // RFC 9457 section 3: problems listed with JSON pointers into the request.
            body.Append(",\"errors\":[");
            for (var i = 0; i < errors.Count; i++)
            {
                body.Append(i == 0 ? "{\"pointer\":" : ",{\"pointer\":");
                AppendJsonString(body, errors[i].Pointer);
                body.Append(",\"detail\":");
                AppendJsonString(body, errors[i].Detail);
                body.Append('}');
            }

            body.Append(']');
        }

        body.Append('}');
        return new Response(status, "application/problem+json", Encoding.UTF8.GetBytes(body.ToString()),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), operation is null ? null : OperationName(operation));
    }

    private static void AppendJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\').Append(c);
            }
            else if (c < 0x20)
            {
                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        builder.Append('"');
    }

    /// <summary><c>Prefer: code=404, example=notFound</c> (RFC 7240 preferences used by mock servers).</summary>
    private static Dictionary<string, string> ParsePrefer(string header)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in header.Split(',', ';'))
        {
            var pair = part.Split(['='], 2);
            if (pair.Length == 2)
            {
                result[pair[0].Trim()] = pair[1].Trim().Trim('"');
            }
        }

        return result;
    }

    /// <summary>The host of a Host header without its port (<c>[::1]:4010</c> gives <c>[::1]</c>).</summary>
    private static string HostName(string host)
    {
        var colon = host.LastIndexOf(':');
        return colon > 0 && host.IndexOf(']', StringComparison.Ordinal) < colon ? host.Substring(0, colon) : host;
    }

    private static Regex Route(string template, out string[] names)
    {
        var pattern = new StringBuilder("^");
        var found = new List<string>();
        foreach (Match part in Regex.Matches(template, @"\{[^}]+\}|[^{]+"))
        {
            if (part.Value.StartsWith("{", StringComparison.Ordinal))
            {
                pattern.Append("(?<p").Append(found.Count.ToString(CultureInfo.InvariantCulture)).Append(">[^/]+)");
                found.Add(part.Value.Substring(1, part.Value.Length - 2));
            }
            else
            {
                pattern.Append(Regex.Escape(part.Value));
            }
        }

        names = [.. found];

        return new Regex(pattern.Append("/?$").ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    // ------------------------------------------------------------------ HTTP/1.1

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var received = new MemoryStream();
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(received)) < 0)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0 || received.Length > MaxHeaderBytes)
            {
                return null;
            }

            received.Write(buffer, 0, read);
        }

        var head = Encoding.ASCII.GetString(received.GetBuffer(), 0, headerEnd);
        var lines = head.Split(["\r\n"], StringSplitOptions.None);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
        }

        // Read the body (validated against the contract) so the client also sees a clean response.
        var bodyStart = headerEnd + 4;
        var buffered = received.Length - bodyStart;
        var body = new MemoryStream();
        body.Write(received.GetBuffer(), bodyStart, (int)buffered);
        if (headers.TryGetValue("Content-Length", out var lengthText) && long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            if (length > MaxBodyBytes)
            {
                return null; // Not read: the connection is closed.
            }

            var remaining = length - buffered;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                body.Write(buffer, 0, read);
                remaining -= read;
            }

            body.SetLength(Math.Min(body.Length, length));
        }
        else if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            while (!EndsChunks(body))
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                body.Write(buffer, 0, read);
                if (body.Length > MaxBodyBytes)
                {
                    return null;
                }
            }

            return new Request(requestLine[0], requestLine[1], headers) { Body = Dechunk(body.ToArray()) };
        }
        else
        {
            body.SetLength(0); // No length: the request has no body (HTTP/1.1).
        }

        return new Request(requestLine[0], requestLine[1], headers) { Body = body.ToArray() };
    }

    /// <summary>Whether a chunked body ends with its last chunk (<c>0</c>, optional trailers, an empty line).</summary>
    private static bool EndsChunks(MemoryStream body)
    {
        var text = Encoding.ASCII.GetString(body.GetBuffer(), 0, (int)body.Length);
        return text.StartsWith("0\r\n", StringComparison.Ordinal) && text.EndsWith("\r\n\r\n", StringComparison.Ordinal) ||
            text.Contains("\r\n0\r\n", StringComparison.Ordinal) && text.EndsWith("\r\n\r\n", StringComparison.Ordinal);
    }

    /// <summary>Decodes a chunked body (RFC 9112 section 7.1); malformed framing ends it.</summary>
    internal static byte[] Dechunk(byte[] data)
    {
        var result = new MemoryStream();
        var position = 0;
        while (position < data.Length)
        {
            var lineEnd = IndexOf(data, position);
            if (lineEnd < 0)
            {
                break;
            }

            var sizeText = Encoding.ASCII.GetString(data, position, lineEnd - position).Split(';')[0].Trim();
            if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0 ||
                size == 0 || lineEnd + 2 + size > data.Length)
            {
                break;
            }

            result.Write(data, lineEnd + 2, size);
            position = lineEnd + 2 + size + 2;
        }

        return result.ToArray();

        static int IndexOf(byte[] data, int start)
        {
            for (var i = start; i + 1 < data.Length; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }
    }

    private static int IndexOfHeaderEnd(MemoryStream received)
    {
        var data = received.GetBuffer();
        for (var i = 0; i + 3 < received.Length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, Response response, CancellationToken cancellationToken)
    {
        var head = new StringBuilder();
        var reason = Enum.IsDefined(typeof(HttpStatusCode), response.Status) ? ((HttpStatusCode)response.Status).ToString() : "Status";
        head.Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
        head.Append("Access-Control-Allow-Origin: *\r\n");
        head.Append("Access-Control-Allow-Methods: *\r\n");
        head.Append("Access-Control-Allow-Headers: *\r\n");
        head.Append("Access-Control-Expose-Headers: *\r\n");
        head.Append("Connection: close\r\n");
        foreach (var header in response.Headers)
        {
            head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }

        if (response.ContentType is not null)
        {
            head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        }

        head.Append("Content-Length: ").Append(response.Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
