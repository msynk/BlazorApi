using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Model;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.Cli;

/// <summary>
/// Checks a request against its operation for <c>api-gen mock</c>: credentials (their presence, not their value),
/// required parameters and the format of scalar parameters, the content type and the JSON body against the schema
/// (types, required and forbidden properties, enums, formats, string, number and array constraints, discriminated
/// hierarchies and unions). Read-only properties are not required in requests.
/// </summary>
internal sealed class RequestValidator
{
    private const int MaxViolations = 20;
    private const int MaxDepth = 64;
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    private readonly List<Violation> _violations = [];

    /// <summary>One problem: a JSON pointer into the request (<c>/body/name</c>, <c>/query/limit</c>) and what is wrong.</summary>
    internal readonly record struct Violation(string Pointer, string Detail);

    /// <summary>A rejected request: the status, a summary, the problems and the <c>WWW-Authenticate</c> challenge.</summary>
    internal sealed record Failure(int Status, string Detail, IReadOnlyList<Violation> Violations, string? Challenge);

    /// <summary>Validates a request; <see langword="null"/> when it matches the operation.</summary>
    public static Failure? Validate(ApiOperation operation, string method, string target, IReadOnlyDictionary<string, string> headers,
        byte[] body, IReadOnlyDictionary<string, string> pathValues)
    {
        var query = ParseQuery(target);
        var cookies = ParseCookies(headers);
        if (operation.Security.Count > 0 &&
            !operation.Security.Any(r => r.IsAnonymous || r.Items.All(i => HasCredential(i.Scheme.Source, headers, query, cookies))))
        {
            var schemes = operation.Security.SelectMany(r => r.Items).Select(i => Describe(i.Scheme)).Distinct();
            var challenge = operation.Security.SelectMany(r => r.Items).Select(i => i.Scheme.Source)
                .Where(s => s.Type is SecuritySchemeType.Http or SecuritySchemeType.OAuth2 or SecuritySchemeType.OpenIdConnect)
                .Select(s => s.Type == SecuritySchemeType.Http && !string.IsNullOrEmpty(s.Scheme) ? Capitalize(s.Scheme!) : "Bearer")
                .FirstOrDefault();
            return new Failure(401, $"The operation requires credentials: {string.Join(" or ", schemes)}.", [], challenge);
        }

        var validator = new RequestValidator();
        validator.Parameters(operation, headers, query, cookies, pathValues);
        if (operation.RequestBody is { } requestBody && method is not ("GET" or "HEAD"))
        {
            if (body.Length == 0)
            {
                if (requestBody.Required)
                {
                    validator.Add("/body", "A request body is required.");
                }
            }
            else
            {
                var mediaType = headers.TryGetValue("Content-Type", out var contentType) ? contentType.Split(';')[0].Trim() : null;
                var content = mediaType is null ? null : Match(requestBody.Contents, mediaType);
                if (content is null)
                {
                    var accepted = string.Join(", ", requestBody.Contents.Select(c => c.MediaType));
                    return new Failure(415, mediaType is null
                        ? $"The request body has no Content-Type; the operation accepts {accepted}."
                        : $"The Content-Type '{mediaType}' is not accepted; the operation accepts {accepted}.", [], null);
                }

                if (content.Kind == ApiBodyKind.Json && content.Type is { } type)
                {
                    validator.Body(body, type);
                }
            }
        }

        return validator._violations.Count == 0 ? null : new Failure(400,
            $"The request does not match the contract ({validator._violations.Count}{(validator._violations.Count >= MaxViolations ? "+" : string.Empty)} problem{(validator._violations.Count == 1 ? string.Empty : "s")}).",
            validator._violations, null);
    }

    // ------------------------------------------------------------------ credentials

    private static bool HasCredential(OpenApiSecurityScheme scheme, IReadOnlyDictionary<string, string> headers,
        Dictionary<string, List<string>> query, Dictionary<string, string> cookies)
    {
        var authorization = headers.TryGetValue("Authorization", out var value) ? value : null;
        return scheme.Type switch
        {
            SecuritySchemeType.ApiKey => scheme.In switch
            {
                ParameterLocation.Header => scheme.Name is not null && headers.ContainsKey(scheme.Name),
                ParameterLocation.Query => scheme.Name is not null && query.ContainsKey(scheme.Name),
                ParameterLocation.Cookie => scheme.Name is not null && cookies.ContainsKey(scheme.Name),
                _ => true,
            },
            SecuritySchemeType.Http => authorization is not null && (string.IsNullOrEmpty(scheme.Scheme) ||
                authorization.StartsWith(scheme.Scheme + " ", StringComparison.OrdinalIgnoreCase)),
            SecuritySchemeType.OAuth2 or SecuritySchemeType.OpenIdConnect => authorization is not null &&
                authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase),
            _ => true, // Mutual TLS and unknown schemes cannot be checked here.
        };
    }

    private static string Describe(ApiSecurityScheme scheme) => scheme.Source.Type switch
    {
        SecuritySchemeType.ApiKey => $"the API key '{scheme.Source.Name}' ({scheme.Source.In?.ToString().ToLowerInvariant()})",
        SecuritySchemeType.Http => $"an Authorization header ({scheme.Source.Scheme})",
        _ => "an Authorization header (Bearer)",
    };

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

    // ------------------------------------------------------------------ parameters

    private void Parameters(ApiOperation operation, IReadOnlyDictionary<string, string> headers, Dictionary<string, List<string>> query,
        Dictionary<string, string> cookies, IReadOnlyDictionary<string, string> pathValues)
    {
        foreach (var parameter in operation.Parameters)
        {
            var location = parameter.Location.ToString().ToLowerInvariant();
            var pointer = $"/{location}/{Escape(parameter.Name)}";
            var target = Resolve(parameter.Type);
            if (target is ApiObjectType or ApiMapType || parameter.Location == ParameterLocation.QueryString)
            {
                continue; // Spread over several keys (form or deepObject): not checked.
            }

            List<string>? values = parameter.Location switch
            {
                ParameterLocation.Path => pathValues.TryGetValue(parameter.Name, out var p) ? [p] : null,
                ParameterLocation.Query => query.TryGetValue(parameter.Name, out var q) ? q : null,
                ParameterLocation.Header => headers.TryGetValue(parameter.Name, out var h) ? [h] : null,
                ParameterLocation.Cookie => cookies.TryGetValue(parameter.Name, out var c) ? [c] : null,
                _ => null,
            };
            if (values is null)
            {
                if (parameter.Required)
                {
                    Add(pointer, $"The {location} parameter '{parameter.Name}' is required.");
                }

                continue;
            }

            if (parameter.ContentMediaType is not null || parameter.Style is ApiParameterStyle.Label or ApiParameterStyle.Matrix)
            {
                continue;
            }

            if (target is ApiArrayType array)
            {
                var items = parameter.Explode && parameter.Location is ParameterLocation.Query or ParameterLocation.Cookie
                    ? values
                    : values.SelectMany(v => v.Split(parameter.Style switch
                    {
                        ApiParameterStyle.SpaceDelimited => ' ',
                        ApiParameterStyle.PipeDelimited => '|',
                        _ => ',',
                    })).ToList();
                ItemCount(items.Count, parameter.Constraints, pointer);
                for (var i = 0; i < items.Count; i++)
                {
                    Scalar(items[i], array.Items, ApiConstraints.None, $"{pointer}/{i}");
                }
            }
            else
            {
                Scalar(values[values.Count - 1], parameter.Type, parameter.Constraints, pointer);
            }
        }
    }

    /// <summary>Checks the text of a parameter value against a scalar type.</summary>
    private void Scalar(string text, ApiTypeRef type, ApiConstraints constraints, string pointer)
    {
        switch (type)
        {
            case ApiNamedTypeRef { Target: ApiEnumType enumType }:
                if (!enumType.Members.Any(m => m.Value is ScalarNode s && (enumType.UnderlyingKind == ApiPrimitiveKind.String
                        ? s.Value == text
                        : decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && s.TryGetDecimal(out var member) && number == member)))
                {
                    Add(pointer, $"'{text}' is not one of {Members(enumType)}.");
                }

                break;
            case ApiPrimitiveType primitive:
                switch (primitive.Kind)
                {
                    case ApiPrimitiveKind.Integer:
                        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                        {
                            Add(pointer, $"'{text}' is not an integer.");
                        }
                        else
                        {
                            Range(integer, constraints, pointer);
                        }

                        break;
                    case ApiPrimitiveKind.Number:
                        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        {
                            Add(pointer, $"'{text}' is not a number.");
                        }
                        else
                        {
                            Range(number, constraints, pointer);
                        }

                        break;
                    case ApiPrimitiveKind.Boolean:
                        if (text is not ("true" or "false"))
                        {
                            Add(pointer, $"'{text}' is not true or false.");
                        }

                        break;
                    case ApiPrimitiveKind.Any:
                        break;
                    default:
                        Text(text, primitive, constraints, pointer);
                        break;
                }

                break;
        }
    }

    // ------------------------------------------------------------------ body

    private void Body(byte[] body, ApiTypeRef type)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException ex)
        {
            Add("/body", $"The body is not valid JSON: {ex.Message}");
            return;
        }

        using (document)
        {
            Value(document.RootElement, type, ApiConstraints.None, "/body", 0);
        }
    }

    private void Value(JsonElement value, ApiTypeRef type, ApiConstraints constraints, string pointer, int depth)
    {
        if (_violations.Count >= MaxViolations || depth > MaxDepth)
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!type.IsNullable && type is not ApiPrimitiveType { Kind: ApiPrimitiveKind.Any })
            {
                Add(pointer, "Must not be null.");
            }

            return;
        }

        switch (type)
        {
            case ApiPrimitiveType primitive:
                Primitive(value, primitive, constraints, pointer);
                break;
            case ApiArrayType array:
                if (Expect(value, JsonValueKind.Array, "an array", pointer))
                {
                    ItemCount(value.GetArrayLength(), constraints, pointer);
                    var index = 0;
                    var seen = array.UniqueItems ? new HashSet<string>(StringComparer.Ordinal) : null;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (seen is not null && !seen.Add(item.GetRawText()))
                        {
                            Add($"{pointer}/{index}", "Duplicates an earlier item; the items must be unique.");
                        }

                        Value(item, array.Items, ApiConstraints.None, $"{pointer}/{index}", depth + 1);
                        index++;
                    }
                }

                break;
            case ApiMapType map:
                if (Expect(value, JsonValueKind.Object, "an object", pointer))
                {
                    foreach (var property in value.EnumerateObject())
                    {
                        Value(property.Value, map.Values, ApiConstraints.None, $"{pointer}/{Escape(property.Name)}", depth + 1);
                    }
                }

                break;
            case ApiNamedTypeRef named:
                Named(value, named.Target, constraints, pointer, depth);
                break;
        }
    }

    private void Named(JsonElement value, ApiNamedType type, ApiConstraints constraints, string pointer, int depth)
    {
        switch (type)
        {
            case ApiEnumType enumType:
                var matches = enumType.Members.Any(m => m.Value is ScalarNode s && (value.ValueKind == JsonValueKind.String
                    ? s.IsString && s.Value == value.GetString()
                    : value.ValueKind == JsonValueKind.Number && !s.IsString && s.TryGetDecimal(out var member) &&
                      value.TryGetDecimal(out var number) && member == number));
                if (!matches)
                {
                    Add(pointer, $"{Show(value)} is not one of {Members(enumType)}.");
                }

                break;
            case ApiObjectType obj:
                Object(value, obj, pointer, depth);
                break;
            case ApiUnionType union:
                Union(value, union, pointer, depth);
                break;
        }
    }

    private void Object(JsonElement value, ApiObjectType type, string pointer, int depth)
    {
        if (!Expect(value, JsonValueKind.Object, "an object", pointer))
        {
            return;
        }

        // A discriminated hierarchy: the value is checked as the subtype its discriminator names.
        if (type.Root.Discriminator is { } discriminator &&
            value.TryGetProperty(discriminator.PropertyName, out var tag) && tag.ValueKind == JsonValueKind.String)
        {
            var mapping = discriminator.Mappings.FirstOrDefault(m => m.Value == tag.GetString());
            if (mapping?.Type is ApiObjectType subtype && Extends(subtype, type))
            {
                type = subtype;
            }
            else if (discriminator.Mappings.Count > 0 && mapping is null)
            {
                Add($"{pointer}/{Escape(discriminator.PropertyName)}",
                    $"'{tag.GetString()}' is not one of {string.Join(", ", discriminator.Mappings.Select(m => "'" + m.Value + "'"))}.");
            }
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in type.AllProperties)
        {
            declared.Add(property.JsonName);
            var propertyPointer = $"{pointer}/{Escape(property.JsonName)}";
            if (!value.TryGetProperty(property.JsonName, out var propertyValue))
            {
                if (property.Required && !property.ReadOnly)
                {
                    Add(propertyPointer, "Required.");
                }
            }
            else if (!property.ReadOnly)
            {
                Value(propertyValue, property.Type, property.Constraints, propertyPointer, depth + 1);
            }
        }

        if (type.Root.Discriminator is { } root)
        {
            declared.Add(root.PropertyName);
        }

        foreach (var property in value.EnumerateObject().Where(p => !declared.Contains(p.Name)))
        {
            if (type.AdditionalPropertiesForbidden)
            {
                Add($"{pointer}/{Escape(property.Name)}", "Not a property of the schema (additional properties are not allowed).");
            }
            else if (type.AdditionalProperties is { } additional)
            {
                Value(property.Value, additional, ApiConstraints.None, $"{pointer}/{Escape(property.Name)}", depth + 1);
            }
        }
    }

    private void Union(JsonElement value, ApiUnionType union, string pointer, int depth)
    {
        if (union.DiscriminatorPropertyName is { } name && value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(name, out var tag) && tag.ValueKind == JsonValueKind.String &&
            union.Variants.FirstOrDefault(v => v.DiscriminatorValue == tag.GetString()) is { } selected)
        {
            Value(value, selected.Type, ApiConstraints.None, pointer, depth + 1);
            return;
        }

        // Without a discriminator: valid when one variant accepts the value.
        List<Violation>? closest = null;
        foreach (var variant in union.Variants)
        {
            var trial = new RequestValidator();
            trial.Value(value, variant.Type, ApiConstraints.None, pointer, depth + 1);
            if (trial._violations.Count == 0)
            {
                return;
            }

            if (closest is null || trial._violations.Count < closest.Count)
            {
                closest = trial._violations;
            }
        }

        Add(pointer, $"Matches none of the {union.Variants.Count} allowed schemas" +
            (closest is { Count: > 0 } ? $" (closest: {closest[0].Pointer}: {closest[0].Detail})." : "."));
    }

    private void Primitive(JsonElement value, ApiPrimitiveType type, ApiConstraints constraints, string pointer)
    {
        if (type.ConstValue is ScalarNode constant && !SameScalar(value, constant))
        {
            Add(pointer, $"Must be {constant.ToJson()}.");
            return;
        }

        switch (type.Kind)
        {
            case ApiPrimitiveKind.Any:
                return;
            case ApiPrimitiveKind.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    Add(pointer, $"Must be a boolean, not {Kind(value)}.");
                }

                return;
            case ApiPrimitiveKind.Integer:
            case ApiPrimitiveKind.Number:
                if (!Expect(value, JsonValueKind.Number, type.Kind == ApiPrimitiveKind.Integer ? "an integer" : "a number", pointer))
                {
                    return;
                }

                if (value.TryGetDecimal(out var number))
                {
                    if (type.Kind == ApiPrimitiveKind.Integer && number != decimal.Truncate(number))
                    {
                        Add(pointer, $"Must be an integer, not {value.GetRawText()}.");
                        return;
                    }

                    Range(number, constraints, pointer);
                }

                return;
            default:
                if (Expect(value, JsonValueKind.String, "a string", pointer))
                {
                    Text(value.GetString()!, type, constraints, pointer);
                }

                return;
        }
    }

    /// <summary>Checks the format and constraints of a string.</summary>
    private void Text(string text, ApiPrimitiveType type, ApiConstraints constraints, string pointer)
    {
        var valid = type.Kind switch
        {
            ApiPrimitiveKind.Date => DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            ApiPrimitiveKind.DateTime => text.Length >= 20 && (text[10] == 'T' || text[10] == 't') &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            ApiPrimitiveKind.Uuid => Guid.TryParseExact(text, "D", out _),
            ApiPrimitiveKind.Uri => Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out _),
            ApiPrimitiveKind.Base64 => IsBase64(text),
            _ => true,
        };
        if (!valid)
        {
            Add(pointer, $"'{Truncate(text)}' is not a valid {type.Format ?? type.Kind.ToString().ToLowerInvariant()}.");
            return;
        }

        var length = CodePoints(text);
        if (constraints.MinLength is { } min && length < min)
        {
            Add(pointer, $"Must have at least {min} characters.");
        }

        if (constraints.MaxLength is { } max && length > max)
        {
            Add(pointer, $"Must have at most {max} characters.");
        }

        if (constraints.Pattern is { } pattern)
        {
            try
            {
                if (!Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, PatternTimeout))
                {
                    Add(pointer, $"Does not match the pattern '{pattern}'.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                // A pattern .NET cannot run (ECMA-262 differences) or a slow one: not checked.
            }
        }
    }

    private void Range(decimal number, ApiConstraints constraints, string pointer)
    {
        if (constraints.Minimum is { } minimum && number < minimum)
        {
            Add(pointer, $"Must be at least {minimum.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (constraints.Maximum is { } maximum && number > maximum)
        {
            Add(pointer, $"Must be at most {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (constraints.ExclusiveMinimum is { } exclusiveMinimum && number <= exclusiveMinimum)
        {
            Add(pointer, $"Must be greater than {exclusiveMinimum.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (constraints.ExclusiveMaximum is { } exclusiveMaximum && number >= exclusiveMaximum)
        {
            Add(pointer, $"Must be less than {exclusiveMaximum.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (constraints.MultipleOf is { } multipleOf && multipleOf > 0 && number % multipleOf != 0)
        {
            Add(pointer, $"Must be a multiple of {multipleOf.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private void ItemCount(int count, ApiConstraints constraints, string pointer)
    {
        if (constraints.MinItems is { } min && count < min)
        {
            Add(pointer, $"Must have at least {min} items.");
        }

        if (constraints.MaxItems is { } max && count > max)
        {
            Add(pointer, $"Must have at most {max} items.");
        }
    }

    // ------------------------------------------------------------------ helpers

    private void Add(string pointer, string detail)
    {
        if (_violations.Count < MaxViolations)
        {
            _violations.Add(new Violation(pointer, detail));
        }
    }

    private bool Expect(JsonElement value, JsonValueKind kind, string description, string pointer)
    {
        if (value.ValueKind == kind)
        {
            return true;
        }

        Add(pointer, $"Must be {description}, not {Kind(value)}.");
        return false;
    }

    private static object? Resolve(ApiTypeRef type) => type is ApiNamedTypeRef named ? named.Target : type;

    private static bool Extends(ApiObjectType type, ApiObjectType ancestor)
    {
        for (ApiObjectType? current = type; current is not null; current = current.BaseType)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameScalar(JsonElement value, ScalarNode constant) => value.ValueKind switch
    {
        JsonValueKind.String => constant.IsString && constant.Value == value.GetString(),
        JsonValueKind.Number => !constant.IsString && constant.TryGetDecimal(out var c) && value.TryGetDecimal(out var v) && c == v,
        JsonValueKind.True or JsonValueKind.False => constant.TryGetBoolean(out var b) && b == (value.ValueKind == JsonValueKind.True),
        _ => false,
    };

    private static string Members(ApiEnumType type)
    {
        var members = type.Members.Select(m => m.Value.ToJson()).ToList();
        return members.Count <= 10 ? string.Join(", ", members) : string.Join(", ", members.Take(10)) + $" (and {members.Count - 10} more)";
    }

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };

    private static string Show(JsonElement value) => value.ValueKind == JsonValueKind.String ? $"'{Truncate(value.GetString()!)}'" : Truncate(value.GetRawText());

    private static string Truncate(string text) => text.Length <= 80 ? text : text.Substring(0, 80) + "...";

    private static int CodePoints(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLowSurrogate(text[i]))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsBase64(string text)
    {
        var buffer = new byte[(text.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(text, buffer, out _);
    }

    /// <summary>Escapes a JSON pointer segment (RFC 6901).</summary>
    private static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static ApiContent? Match(IReadOnlyList<ApiContent> contents, string mediaType)
    {
        return contents.FirstOrDefault(c => string.Equals(c.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)) ??
            contents.FirstOrDefault(c => Covers(c.MediaType, mediaType));

        static bool Covers(string range, string mediaType)
        {
            if (range == "*/*")
            {
                return true;
            }

            return range.EndsWith("/*", StringComparison.Ordinal) &&
                mediaType.StartsWith(range.Substring(0, range.Length - 1), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, List<string>> ParseQuery(string target)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var question = target.IndexOf('?', StringComparison.Ordinal);
        if (question < 0)
        {
            return result;
        }

        foreach (var pair in target.Substring(question + 1).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Decode(equals < 0 ? pair : pair.Substring(0, equals));
            var value = equals < 0 ? string.Empty : Decode(pair.Substring(equals + 1));
            if (!result.TryGetValue(name, out var values))
            {
                result[name] = values = [];
            }

            values.Add(value);
        }

        return result;

        static string Decode(string text)
        {
            try
            {
                return Uri.UnescapeDataString(text.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return text;
            }
        }
    }

    private static Dictionary<string, string> ParseCookies(IReadOnlyDictionary<string, string> headers)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (headers.TryGetValue("Cookie", out var header))
        {
            foreach (var pair in header.Split(';'))
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    result[pair.Substring(0, equals).Trim()] = pair.Substring(equals + 1).Trim();
                }
            }
        }

        return result;
    }
}
