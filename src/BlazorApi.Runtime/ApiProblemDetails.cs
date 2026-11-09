using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorApi.Runtime;

/// <summary>
/// RFC 9457 problem details (<c>application/problem+json</c>). Generated clients read error responses with this media
/// type and no declared schema into this type.
/// </summary>
public sealed class ApiProblemDetails
{
    /// <summary>A URI reference identifying the problem type.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>A short summary of the problem type.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>The HTTP status code.</summary>
    [JsonPropertyName("status")]
    public int? Status { get; set; }

    /// <summary>An explanation of this occurrence.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>A URI reference identifying this occurrence.</summary>
    [JsonPropertyName("instance")]
    public string? Instance { get; set; }

    /// <summary>Validation errors keyed by field (the ASP.NET Core <c>errors</c> extension), when present.</summary>
    [JsonPropertyName("errors")]
    public IDictionary<string, string[]>? Errors { get; set; }

    /// <summary>Other members of the problem object.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}
