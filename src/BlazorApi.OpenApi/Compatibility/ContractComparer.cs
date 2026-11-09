using System.Globalization;
using System.Text;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.OpenApi.Compatibility;

/// <summary>How a contract change affects existing clients.</summary>
public enum ChangeSeverity
{
    /// <summary>Existing clients keep working.</summary>
    NonBreaking,

    /// <summary>Existing clients may break, depending on how they and the server behave.</summary>
    PotentiallyBreaking,

    /// <summary>Existing clients break: requests are rejected or responses no longer match the client types.</summary>
    Breaking,
}

/// <summary>One difference between two versions of a contract.</summary>
public sealed class ContractChange
{
    internal ContractChange(ChangeSeverity severity, string kind, string path, string message)
    {
        Severity = severity;
        Kind = kind;
        Path = path;
        Message = message;
    }

    /// <summary>The severity.</summary>
    public ChangeSeverity Severity { get; }

    /// <summary>A stable change kind (for example <c>operation-removed</c>), usable in ignore lists.</summary>
    public string Kind { get; }

    /// <summary>Where the change is, for example <c>GET /pets/{petId} response 200 body.name</c>.</summary>
    public string Path { get; }

    /// <summary>A description.</summary>
    public string Message { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Severity}: {Path}: {Message} ({Kind})";
}

/// <summary>The result of comparing two contract versions.</summary>
public sealed class CompatibilityReport
{
    internal CompatibilityReport(IReadOnlyList<ContractChange> changes) => Changes = changes;

    /// <summary>The changes, most severe first, then by path.</summary>
    public IReadOnlyList<ContractChange> Changes { get; }

    /// <summary>The highest severity, or <see langword="null"/> when nothing changed.</summary>
    public ChangeSeverity? MaxSeverity => Changes.Count == 0 ? null : Changes.Max(c => c.Severity);

    /// <summary>Whether any change is at least the given severity.</summary>
    public bool HasChangesAtLeast(ChangeSeverity severity) => Changes.Any(c => c.Severity >= severity);

    /// <summary>Returns a report without the changes of the given kinds (a compatibility policy).</summary>
    public CompatibilityReport Without(IEnumerable<string> kinds)
    {
        var ignored = new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);
        return new CompatibilityReport(Changes.Where(c => !ignored.Contains(c.Kind)).ToArray());
    }

    /// <summary>A plain-text report.</summary>
    public string ToText()
    {
        if (Changes.Count == 0)
        {
            return "No changes.";
        }

        var builder = new StringBuilder();
        foreach (var group in Changes.GroupBy(c => c.Severity))
        {
            builder.Append(Label(group.Key)).Append(" (").Append(group.Count().ToString(CultureInfo.InvariantCulture)).Append(')').Append('\n');
            foreach (var change in group)
            {
                builder.Append("  ").Append(change.Path).Append(": ").Append(change.Message).Append(" [").Append(change.Kind).Append("]\n");
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>A Markdown report (for pull request comments).</summary>
    public string ToMarkdown()
    {
        var builder = new StringBuilder("## API contract changes\n\n");
        if (Changes.Count == 0)
        {
            return builder.Append("No changes.\n").ToString();
        }

        builder.Append("| Severity | Where | Change | Kind |\n|---|---|---|---|\n");
        foreach (var change in Changes)
        {
            builder.Append("| ").Append(Label(change.Severity)).Append(" | `").Append(change.Path.Replace("|", "\\|")).Append("` | ")
                .Append(change.Message.Replace("|", "\\|")).Append(" | ").Append(change.Kind).Append(" |\n");
        }

        return builder.ToString();
    }

    /// <summary>A JSON report.</summary>
    public string ToJson()
    {
        var builder = new StringBuilder("{\"maxSeverity\":");
        builder.Append(MaxSeverity is { } max ? "\"" + Name(max) + "\"" : "null").Append(",\"changes\":[");
        for (var i = 0; i < Changes.Count; i++)
        {
            var change = Changes[i];
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"severity\":\"").Append(Name(change.Severity)).Append("\",\"kind\":");
            Syntax.DocNode.WriteJsonString(builder, change.Kind);
            builder.Append(",\"path\":");
            Syntax.DocNode.WriteJsonString(builder, change.Path);
            builder.Append(",\"message\":");
            Syntax.DocNode.WriteJsonString(builder, change.Message);
            builder.Append('}');
        }

        return builder.Append("]}").ToString();
    }

    private static string Label(ChangeSeverity severity) => severity switch
    {
        ChangeSeverity.Breaking => "Breaking",
        ChangeSeverity.PotentiallyBreaking => "Potentially breaking",
        _ => "Non-breaking",
    };

    private static string Name(ChangeSeverity severity) => severity switch
    {
        ChangeSeverity.Breaking => "breaking",
        ChangeSeverity.PotentiallyBreaking => "potentially-breaking",
        _ => "non-breaking",
    };
}

/// <summary>
/// Compares two versions of a contract from the point of view of existing clients. Request and response types are
/// compared with direction in mind: widening a request type is safe, widening a response type is not.
/// </summary>
public static class ContractComparer
{
    private enum Direction
    {
        Request,
        Response,
    }

    /// <summary>Compares two API models.</summary>
    public static CompatibilityReport Compare(ApiModel previous, ApiModel current)
    {
        if (previous is null)
        {
            throw new ArgumentNullException(nameof(previous));
        }

        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        var changes = new List<ContractChange>();
        var run = new Run(changes);
        run.CompareOperations(previous, current);
        run.CompareSecuritySchemes(previous, current);
        return new CompatibilityReport(changes
            .GroupBy(c => (c.Severity, c.Kind, c.Path, c.Message))
            .Select(g => g.First())
            .OrderByDescending(c => c.Severity)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .ThenBy(c => c.Kind, StringComparer.Ordinal)
            .ToArray());
    }

    private sealed class Run
    {
        private readonly List<ContractChange> _changes;
        private readonly HashSet<(ApiNamedType, ApiNamedType, Direction)> _compared = [];

        public Run(List<ContractChange> changes) => _changes = changes;

        private void Add(ChangeSeverity severity, string kind, string path, string message) =>
            _changes.Add(new ContractChange(severity, kind, path, message));

        public void CompareOperations(ApiModel previous, ApiModel current)
        {
            var currentByKey = current.Operations.ToDictionary(Key, StringComparer.Ordinal);
            var currentById = current.Operations.Where(o => o.OperationId is not null).GroupBy(o => o.OperationId!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var matched = new HashSet<ApiOperation>();
            foreach (var old in previous.Operations)
            {
                if (!currentByKey.TryGetValue(Key(old), out var updated))
                {
                    if (old.OperationId is { } id && currentById.TryGetValue(id, out var moved))
                    {
                        Add(ChangeSeverity.Breaking, "operation-moved", old.ToString(), $"The operation '{id}' moved to {moved}");
                        matched.Add(moved);
                        CompareOperation(old, moved);
                    }
                    else
                    {
                        Add(ChangeSeverity.Breaking, "operation-removed", old.ToString(), "The operation was removed");
                    }

                    continue;
                }

                matched.Add(updated);
                CompareOperation(old, updated);
            }

            foreach (var added in current.Operations.Where(o => !matched.Contains(o)))
            {
                Add(ChangeSeverity.NonBreaking, "operation-added", added.ToString(), "The operation was added");
            }
        }

        public void CompareSecuritySchemes(ApiModel previous, ApiModel current)
        {
            foreach (var scheme in previous.SecuritySchemes)
            {
                var updated = current.SecuritySchemes.FirstOrDefault(s => s.Name == scheme.Name);
                if (updated is null)
                {
                    Add(ChangeSeverity.PotentiallyBreaking, "security-scheme-removed", "securitySchemes." + scheme.Name, "The security scheme was removed");
                }
                else if (updated.Type != scheme.Type)
                {
                    Add(ChangeSeverity.Breaking, "security-scheme-changed", "securitySchemes." + scheme.Name, $"The scheme type changed from {scheme.Type} to {updated.Type}");
                }
            }
        }

        private static string Key(ApiOperation operation) => operation.Method + " " + NormalizePath(operation.Path);

        private static string NormalizePath(string path)
        {
            var builder = new StringBuilder();
            var depth = 0;
            foreach (var c in path)
            {
                if (c == '{')
                {
                    depth++;
                    builder.Append("{}");
                }
                else if (c == '}')
                {
                    depth--;
                }
                else if (depth == 0)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        private void CompareOperation(ApiOperation old, ApiOperation updated)
        {
            var path = updated.ToString();
            if (old.OperationId != updated.OperationId)
            {
                Add(ChangeSeverity.PotentiallyBreaking, "operation-id-changed", path,
                    $"The operationId changed from '{old.OperationId ?? "(none)"}' to '{updated.OperationId ?? "(none)"}' (generated method names change)");
            }

            if (!old.Deprecated && updated.Deprecated)
            {
                Add(ChangeSeverity.NonBreaking, "operation-deprecated", path, "The operation is now deprecated");
            }

            CompareParameters(old, updated, path);
            CompareRequestBody(old, updated, path);
            CompareResponses(old, updated, path);
            CompareSecurity(old, updated, path);
        }

        private void CompareParameters(ApiOperation old, ApiOperation updated, string path)
        {
            foreach (var parameter in old.Parameters)
            {
                var match = updated.Parameters.FirstOrDefault(p => p.Location == parameter.Location && SameName(p, parameter));
                var where = $"{path} {parameter.Location.ToString().ToLowerInvariant()} parameter '{parameter.Name}'";
                if (match is null)
                {
                    var renamed = parameter.Location == ParameterLocation.Path
                        ? updated.Parameters.FirstOrDefault(p => p.Location == ParameterLocation.Path && !old.Parameters.Any(o => o.Location == ParameterLocation.Path && o.Name == p.Name))
                        : null;
                    if (renamed is not null)
                    {
                        Add(ChangeSeverity.NonBreaking, "path-parameter-renamed", where, $"The path parameter was renamed to '{renamed.Name}' (generated parameter names change)");
                        CompareType(parameter.Type, renamed.Type, Direction.Request, where);
                    }
                    else
                    {
                        Add(ChangeSeverity.PotentiallyBreaking, "parameter-removed", where, "The parameter was removed; clients still send it");
                    }

                    continue;
                }

                if (!parameter.Required && match.Required)
                {
                    Add(ChangeSeverity.Breaking, "parameter-now-required", where, "The parameter became required");
                }
                else if (parameter.Required && !match.Required)
                {
                    Add(ChangeSeverity.NonBreaking, "parameter-now-optional", where, "The parameter became optional");
                }

                if (parameter.Style != match.Style || parameter.Explode != match.Explode || parameter.ContentMediaType != match.ContentMediaType)
                {
                    Add(ChangeSeverity.Breaking, "parameter-serialization-changed", where,
                        $"The serialization changed from {parameter.Style}{(parameter.Explode ? " (explode)" : string.Empty)} to {match.Style}{(match.Explode ? " (explode)" : string.Empty)}");
                }

                CompareType(parameter.Type, match.Type, Direction.Request, where);
            }

            foreach (var parameter in updated.Parameters.Where(p => !old.Parameters.Any(o => o.Location == p.Location && SameName(o, p))))
            {
                if (parameter.Location == ParameterLocation.Path && old.Parameters.Count(o => o.Location == ParameterLocation.Path) == updated.Parameters.Count(u => u.Location == ParameterLocation.Path))
                {
                    continue; // A renamed path parameter, reported above.
                }

                var where = $"{path} {parameter.Location.ToString().ToLowerInvariant()} parameter '{parameter.Name}'";
                Add(parameter.Required ? ChangeSeverity.Breaking : ChangeSeverity.NonBreaking,
                    parameter.Required ? "required-parameter-added" : "optional-parameter-added",
                    where,
                    parameter.Required ? "A required parameter was added" : "An optional parameter was added");
            }
        }

        private static bool SameName(ApiParameter a, ApiParameter b) =>
            a.Location == ParameterLocation.Header
                ? string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(a.Name, b.Name, StringComparison.Ordinal);

        private void CompareRequestBody(ApiOperation old, ApiOperation updated, string path)
        {
            var where = path + " request body";
            if (old.RequestBody is null)
            {
                if (updated.RequestBody is { Required: var required })
                {
                    Add(required ? ChangeSeverity.Breaking : ChangeSeverity.NonBreaking, required ? "required-body-added" : "optional-body-added", where,
                        required ? "A required request body was added" : "An optional request body was added");
                }

                return;
            }

            if (updated.RequestBody is null)
            {
                Add(ChangeSeverity.PotentiallyBreaking, "body-removed", where, "The request body was removed; clients still send it");
                return;
            }

            if (!old.RequestBody.Required && updated.RequestBody.Required)
            {
                Add(ChangeSeverity.Breaking, "body-now-required", where, "The request body became required");
            }

            foreach (var content in old.RequestBody.Contents)
            {
                var match = updated.RequestBody.Contents.FirstOrDefault(c => string.Equals(c.MediaType, content.MediaType, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    Add(ChangeSeverity.Breaking, "request-media-type-removed", where, $"The media type '{content.MediaType}' is no longer accepted");
                    continue;
                }

                if (content.Type is not null && match.Type is not null)
                {
                    CompareType(content.Type, match.Type, Direction.Request, where + $" ({content.MediaType})");
                }
            }
        }

        private void CompareResponses(ApiOperation old, ApiOperation updated, string path)
        {
            foreach (var response in old.Responses)
            {
                var where = $"{path} response {response.StatusCode}";
                var match = updated.Responses.FirstOrDefault(r => r.StatusCode == response.StatusCode);
                if (match is null)
                {
                    Add(response.IsSuccess ? ChangeSeverity.Breaking : ChangeSeverity.PotentiallyBreaking, "response-removed", where,
                        response.IsSuccess ? "The success response was removed" : "The error response was removed");
                    continue;
                }

                foreach (var content in response.Contents)
                {
                    var matchContent = match.Contents.FirstOrDefault(c => string.Equals(c.MediaType, content.MediaType, StringComparison.OrdinalIgnoreCase));
                    if (matchContent is null)
                    {
                        Add(ChangeSeverity.Breaking, "response-media-type-removed", where, $"The media type '{content.MediaType}' is no longer returned");
                        continue;
                    }

                    if (content.Type is not null && matchContent.Type is not null)
                    {
                        CompareType(content.Type, matchContent.Type, Direction.Response, where + " body");
                    }
                }

                foreach (var header in response.Headers.Where(h => h.Required))
                {
                    if (!match.Headers.Any(h => string.Equals(h.Name, header.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        Add(ChangeSeverity.Breaking, "response-header-removed", where, $"The required header '{header.Name}' was removed");
                    }
                }
            }

            foreach (var response in updated.Responses.Where(r => old.Responses.All(o => o.StatusCode != r.StatusCode)))
            {
                Add(response.IsSuccess ? ChangeSeverity.PotentiallyBreaking : ChangeSeverity.NonBreaking, "response-added", $"{path} response {response.StatusCode}",
                    response.IsSuccess ? "A success response was added; clients may not read its body" : "An error response was added");
            }
        }

        private void CompareSecurity(ApiOperation old, ApiOperation updated, string path)
        {
            static IEnumerable<string> Requirements(ApiOperation operation) =>
                operation.Security.Select(r => string.Join("+", r.Items.Select(i => i.Scheme.Name + (i.Scopes.Count > 0 ? "[" + string.Join(",", i.Scopes.OrderBy(s => s, StringComparer.Ordinal)) + "]" : string.Empty))));

            var before = Requirements(old).ToList();
            var after = Requirements(updated).ToList();
            if (before.Count == 0 && after.Count > 0 && !after.Contains(string.Empty))
            {
                Add(ChangeSeverity.Breaking, "security-added", path, "The operation now requires authentication: " + string.Join(" or ", after));
            }
            else if (before.Count > 0 && after.Count > 0 && !after.Any(before.Contains))
            {
                Add(ChangeSeverity.Breaking, "security-changed", path, $"The security requirements changed from {string.Join(" or ", before)} to {string.Join(" or ", after)}");
            }
            else if (before.Count > 0 && after.Count == 0)
            {
                Add(ChangeSeverity.NonBreaking, "security-removed", path, "The operation no longer requires authentication");
            }
        }

        // ------------------------------------------------------------------ types

        private void CompareType(ApiTypeRef old, ApiTypeRef updated, Direction direction, string where)
        {
            if (old.IsNullable != updated.IsNullable)
            {
                if (direction == Direction.Response && updated.IsNullable)
                {
                    Add(ChangeSeverity.Breaking, "response-now-nullable", where, "The value can now be null");
                }
                else if (direction == Direction.Request && !updated.IsNullable)
                {
                    Add(ChangeSeverity.Breaking, "request-no-longer-nullable", where, "The value can no longer be null");
                }
                else
                {
                    Add(ChangeSeverity.NonBreaking, "nullability-changed", where, updated.IsNullable ? "The value can now be null" : "The value can no longer be null");
                }
            }

            switch (old)
            {
                case ApiPrimitiveType oldPrimitive when updated is ApiPrimitiveType newPrimitive:
                    ComparePrimitive(oldPrimitive, newPrimitive, direction, where);
                    break;
                case ApiArrayType oldArray when updated is ApiArrayType newArray:
                    CompareType(oldArray.Items, newArray.Items, direction, where + "[]");
                    break;
                case ApiMapType oldMap when updated is ApiMapType newMap:
                    CompareType(oldMap.Values, newMap.Values, direction, where + "{}");
                    break;
                case ApiNamedTypeRef oldNamed when updated is ApiNamedTypeRef newNamed:
                    CompareNamed(oldNamed.Target, newNamed.Target, direction, where);
                    break;
                default:
                    Add(ChangeSeverity.Breaking, "type-changed", where, $"The type changed from {Describe(old)} to {Describe(updated)}");
                    break;
            }
        }

        private void ComparePrimitive(ApiPrimitiveType old, ApiPrimitiveType updated, Direction direction, string where)
        {
            if (old.Kind == updated.Kind && old.Format == updated.Format)
            {
                return;
            }

            if (old.Kind == ApiPrimitiveKind.Integer && updated.Kind is ApiPrimitiveKind.Integer or ApiPrimitiveKind.Number)
            {
                var widened = updated.Kind == ApiPrimitiveKind.Number || Width(updated.Format) > Width(old.Format);
                var safe = direction == Direction.Request ? widened : !widened;
                Add(safe ? ChangeSeverity.NonBreaking : ChangeSeverity.Breaking, widened ? "type-widened" : "type-narrowed", where,
                    $"The type changed from {Describe(old)} to {Describe(updated)}");
                return;
            }

            if (old.Kind == updated.Kind || (old.Kind != ApiPrimitiveKind.Any && updated.Kind == ApiPrimitiveKind.String && old.Format is not null && direction == Direction.Request))
            {
                Add(ChangeSeverity.PotentiallyBreaking, "format-changed", where, $"The format changed from {old.Format ?? "(none)"} to {updated.Format ?? "(none)"}");
                return;
            }

            Add(ChangeSeverity.Breaking, "type-changed", where, $"The type changed from {Describe(old)} to {Describe(updated)}");
        }

        private static int Width(string? format) => format switch
        {
            "int8" or "uint8" => 8,
            "int16" or "uint16" => 16,
            "int32" or "uint32" => 32,
            _ => 64,
        };

        private void CompareNamed(ApiNamedType old, ApiNamedType updated, Direction direction, string where)
        {
            if (!_compared.Add((old, updated, direction)))
            {
                return;
            }

            switch (old)
            {
                case ApiObjectType oldObject when updated is ApiObjectType newObject:
                    CompareObject(oldObject, newObject, direction, where);
                    break;
                case ApiEnumType oldEnum when updated is ApiEnumType newEnum:
                    CompareEnum(oldEnum, newEnum, direction, where);
                    break;
                case ApiUnionType oldUnion when updated is ApiUnionType newUnion:
                    if (newUnion.Variants.Count < oldUnion.Variants.Count && direction == Direction.Request)
                    {
                        Add(ChangeSeverity.Breaking, "union-variant-removed", where, "A variant of the union was removed");
                    }
                    else if (newUnion.Variants.Count > oldUnion.Variants.Count && direction == Direction.Response)
                    {
                        Add(ChangeSeverity.PotentiallyBreaking, "union-variant-added", where, "A variant was added to a union in a response");
                    }

                    break;
                default:
                    Add(ChangeSeverity.Breaking, "type-changed", where, $"The type changed from {old.GetType().Name.Replace("Api", string.Empty).Replace("Type", string.Empty).ToLowerInvariant()} '{old.Name}' to {updated.GetType().Name.Replace("Api", string.Empty).Replace("Type", string.Empty).ToLowerInvariant()} '{updated.Name}'");
                    break;
            }
        }

        private void CompareObject(ApiObjectType old, ApiObjectType updated, Direction direction, string where)
        {
            var oldProperties = old.AllProperties.GroupBy(p => p.JsonName).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            var newProperties = updated.AllProperties.GroupBy(p => p.JsonName).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
            foreach (var pair in oldProperties)
            {
                var property = pair.Value;
                var at = where + "." + pair.Key;
                if (!newProperties.TryGetValue(pair.Key, out var match))
                {
                    if (direction == Direction.Response)
                    {
                        Add(property.Required ? ChangeSeverity.Breaking : ChangeSeverity.PotentiallyBreaking, "property-removed", at,
                            property.Required ? "A required property was removed from a response" : "An optional property was removed from a response");
                    }
                    else
                    {
                        Add(ChangeSeverity.PotentiallyBreaking, "property-removed", at, "A property was removed from a request; clients still send it");
                    }

                    continue;
                }

                if (direction == Direction.Response && property.Required && !match.Required && !match.ReadOnly && !property.ReadOnly)
                {
                    Add(ChangeSeverity.Breaking, "property-now-optional", at, "A response property became optional (clients that require it fail)");
                }

                if (direction == Direction.Request && !property.Required && match.Required && !match.ReadOnly)
                {
                    Add(ChangeSeverity.Breaking, "property-now-required", at, "A request property became required");
                }

                CompareType(property.Type, match.Type, direction, at);
            }

            foreach (var pair in newProperties.Where(p => !oldProperties.ContainsKey(p.Key)))
            {
                var at = where + "." + pair.Key;
                if (direction == Direction.Request && pair.Value.Required && !pair.Value.ReadOnly)
                {
                    Add(ChangeSeverity.Breaking, "required-property-added", at, "A required property was added to a request");
                }
                else
                {
                    Add(ChangeSeverity.NonBreaking, "property-added", at, "A property was added");
                }
            }

            if (old.Discriminator is { } discriminator && updated.Discriminator is { } newDiscriminator)
            {
                foreach (var mapping in discriminator.Mappings.Where(m => newDiscriminator.Mappings.All(n => n.Value != m.Value)))
                {
                    Add(ChangeSeverity.PotentiallyBreaking, "discriminator-value-removed", where, $"The discriminator value '{mapping.Value}' was removed");
                }

                foreach (var mapping in newDiscriminator.Mappings.Where(m => discriminator.Mappings.All(n => n.Value != m.Value)))
                {
                    Add(ChangeSeverity.NonBreaking, "discriminator-value-added", where, $"The discriminator value '{mapping.Value}' was added (older clients read it as an unrecognized subtype)");
                }
            }
        }

        private void CompareEnum(ApiEnumType old, ApiEnumType updated, Direction direction, string where)
        {
            var before = old.Members.Select(m => m.ValueText).ToList();
            var after = updated.Members.Select(m => m.ValueText).ToList();
            foreach (var removed in before.Where(v => !after.Contains(v)))
            {
                Add(direction == Direction.Request ? ChangeSeverity.Breaking : ChangeSeverity.NonBreaking, "enum-value-removed", where,
                    $"The enum value '{removed}' was removed");
            }

            foreach (var added in after.Where(v => !before.Contains(v)))
            {
                Add(direction == Direction.Response ? ChangeSeverity.PotentiallyBreaking : ChangeSeverity.NonBreaking, "enum-value-added", where,
                    direction == Direction.Response
                        ? $"The enum value '{added}' was added to a response (strict clients reject unknown values)"
                        : $"The enum value '{added}' was added");
            }
        }

        private static string Describe(ApiTypeRef type) => type.WithNullable(false).ToString();
    }
}
