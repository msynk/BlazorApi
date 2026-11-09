using System.Text.Json;
using BlazorApi.Runtime;

namespace BlazorApi.Blazor;

/// <summary>Reads validation errors from API error responses (RFC 9457 problem details with an <c>errors</c> member).</summary>
public static class ApiValidation
{
    /// <summary>
    /// Returns the field errors of an API error: the <c>errors</c> object of a problem details body
    /// (<c>{"errors": {"name": ["Required"]}}</c>), keyed by field. Empty when there are none.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetFieldErrors(ApiError? error)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (error is null)
        {
            return result;
        }

        if (error.Problem?.Errors is { } typed)
        {
            foreach (var pair in typed)
            {
                result[pair.Key] = pair.Value;
            }

            return result;
        }

        if (string.IsNullOrWhiteSpace(error.Body))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(error.Body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in errors.EnumerateObject())
                {
                    var messages = field.Value.ValueKind switch
                    {
                        JsonValueKind.Array => field.Value.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!).ToArray(),
                        JsonValueKind.String => [field.Value.GetString()!],
                        _ => [],
                    };
                    if (messages.Length > 0)
                    {
                        result[field.Name] = messages;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: no field errors.
        }

        return result;
    }

    /// <summary>The title and detail to show for an error.</summary>
    public static (string Title, string? Detail) Describe(ApiError error)
    {
        if (error is null)
        {
            throw new ArgumentNullException(nameof(error));
        }

        if (error.Problem is { } problem)
        {
            return (problem.Title ?? DefaultTitle(error), problem.Detail);
        }

        if (!string.IsNullOrWhiteSpace(error.Body))
        {
            try
            {
                using var document = JsonDocument.Parse(error.Body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var title = Text(root, "title") ?? Text(root, "message") ?? Text(root, "error");
                    var detail = Text(root, "detail") ?? Text(root, "error_description");
                    if (title is not null || detail is not null)
                    {
                        return (title ?? DefaultTitle(error), detail);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        return (DefaultTitle(error), null);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string DefaultTitle(ApiError error) => error.Kind switch
    {
        ApiErrorKind.Network => "The service could not be reached.",
        ApiErrorKind.Timeout => "The request timed out.",
        ApiErrorKind.Deserialization => "The response could not be read.",
        _ => error.StatusCode switch
        {
            400 => "The request is invalid.",
            401 => "You need to sign in.",
            403 => "You are not allowed to do this.",
            404 => "Not found.",
            409 => "The request conflicts with the current state.",
            422 => "The request could not be processed.",
            429 => "Too many requests; try again later.",
            >= 500 => "The service failed to process the request.",
            _ => "The request failed.",
        },
    };
}
