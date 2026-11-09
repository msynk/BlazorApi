using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BlazorApi.Runtime.Json;

namespace BlazorApi.Runtime.Http;

/// <summary>Creates request bodies and reads response bodies. Used by generated clients.</summary>
public static class ApiBody
{
    // ------------------------------------------------------------------ request bodies

    /// <summary>Creates a JSON body.</summary>
    public static HttpContent Json<T>(T value, JsonSerializerOptions options, string mediaType = "application/json") =>
        JsonContent.Create(value, ApiJson.TypeInfo<T>(options), MediaTypeHeaderValue.Parse(mediaType));

    /// <summary>Creates a text body.</summary>
    public static HttpContent Text(string value, string mediaType = "text/plain") =>
        new StringContent(value ?? throw new ArgumentNullException(nameof(value)), Encoding.UTF8, MediaTypeHeaderValue.Parse(mediaType).MediaType!);

    /// <summary>Creates a binary body.</summary>
    public static HttpContent File(FileContent value, string mediaType = "application/octet-stream") =>
        (value ?? throw new ArgumentNullException(nameof(value))).ToHttpContent(mediaType.Contains('*', StringComparison.Ordinal) ? null : mediaType);

    /// <summary>Creates a URL-encoded form body; fields with a null value are omitted.</summary>
    public static HttpContent Form(IEnumerable<KeyValuePair<string, string?>> fields) =>
        new FormUrlEncodedContent(fields.Where(f => f.Value is not null).Select(f => new KeyValuePair<string, string>(f.Key, f.Value!)));

    // ------------------------------------------------------------------ response bodies

    /// <summary>Reads a JSON body.</summary>
    public static async ValueTask<T> ReadJson<T>(HttpResponseMessage response, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return (await JsonSerializer.DeserializeAsync(stream, ApiJson.TypeInfo<T>(options), cancellationToken).ConfigureAwait(false))!;
        }
    }

    /// <summary>Reads a text body.</summary>
    public static async ValueTask<string> ReadText(HttpResponseMessage response, JsonSerializerOptions options, CancellationToken cancellationToken) =>
        await (response ?? throw new ArgumentNullException(nameof(response))).Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Opens a streamed binary body. The returned <see cref="FileResponse"/> owns the response.</summary>
    public static async ValueTask<FileResponse> ReadFile(HttpResponseMessage response, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new FileResponse(response, stream);
    }

    /// <summary>Reads an error body as JSON of the declared type.</summary>
    public static object? JsonError<T>(string body, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize(body, ApiJson.TypeInfo<T>(options));

    /// <summary>Reads an error body as problem details.</summary>
    public static object? ProblemError(string body, JsonSerializerOptions options) =>
        JsonError<ApiProblemDetails>(body, options);

    /// <summary>Returns an error body as text.</summary>
    public static object? TextError(string body, JsonSerializerOptions options) => body;
}

/// <summary>Builds a <c>multipart/form-data</c> body. Null values are omitted.</summary>
public sealed class ApiMultipart
{
    private readonly MultipartFormDataContent _content = new();
    private readonly JsonSerializerOptions _options;

    /// <summary>Creates a builder.</summary>
    public ApiMultipart(JsonSerializerOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Adds a text field.</summary>
    public ApiMultipart Add(string name, string? value, string? contentType = null)
    {
        if (value is not null)
        {
            var part = new StringContent(value, Encoding.UTF8);
            if (contentType is not null)
            {
                part.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
            else
            {
                part.Headers.ContentType = null;
            }

            _content.Add(part, name);
        }

        return this;
    }

    /// <summary>Adds a file field.</summary>
    public ApiMultipart Add(string name, FileContent? file, string? contentType = null)
    {
        if (file is not null)
        {
            _content.Add(file.ToHttpContent(contentType), name, file.FileName ?? name);
        }

        return this;
    }

    /// <summary>Adds several file fields with the same name.</summary>
    public ApiMultipart Add(string name, IEnumerable<FileContent>? files, string? contentType = null)
    {
        if (files is not null)
        {
            foreach (var file in files)
            {
                Add(name, file, contentType);
            }
        }

        return this;
    }

    /// <summary>Adds several text fields with the same name.</summary>
    public ApiMultipart Add(string name, IEnumerable<string>? values, string? contentType = null)
    {
        if (values is not null)
        {
            foreach (var value in values)
            {
                Add(name, value, contentType);
            }
        }

        return this;
    }

    /// <summary>Adds a field serialized as JSON (objects and arrays of objects).</summary>
    public ApiMultipart AddJson<T>(string name, T? value, string? contentType = null)
    {
        if (value is not null)
        {
            _content.Add(ApiBody.Json(value, _options, contentType ?? "application/json"), name);
        }

        return this;
    }

    /// <summary>The HTTP content.</summary>
    public HttpContent Build() => _content;
}
