using System.Globalization;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Examples;

/// <summary>
/// Produces example values for media types and schemas: declared examples first (named, <c>example</c>,
/// <c>examples</c>, <c>default</c>, <c>const</c>, the first <c>enum</c> value), otherwise a value synthesized from the
/// schema (properties, formats, bounds, the first variant of unions, the first subtype of discriminated hierarchies).
/// Used by the mock server of <c>api-gen mock</c>.
/// </summary>
public sealed class OpenApiExamples
{
    private const int MaxDepth = 6;
    private readonly OpenApiDocument _document;
    private readonly HashSet<OpenApiSchema> _visiting = [];

    /// <summary>Creates a generator for a document (its components resolve discriminator mappings and subtypes).</summary>
    public OpenApiExamples(OpenApiDocument document) => _document = document ?? throw new ArgumentNullException(nameof(document));

    /// <summary>
    /// The example of a media type: the named example when <paramref name="exampleName"/> is given and exists, else
    /// <c>example</c>, else the first of <c>examples</c>, else one synthesized from the schema (or <c>itemSchema</c>).
    /// </summary>
    public DocNode? ForMediaType(OpenApiMediaType media, string? exampleName = null)
    {
        if (media is null)
        {
            throw new ArgumentNullException(nameof(media));
        }

        // Copies: callers may change the value, which must never change the document.
        if (exampleName is not null && media.Examples.TryGetValue(exampleName, out var named) && named.Resolved.Value is { } namedValue)
        {
            return namedValue.DeepClone();
        }

        if (media.Example is { } example)
        {
            return example.DeepClone();
        }

        foreach (var candidate in media.Examples.Values)
        {
            if (candidate.Resolved.Value is { } value)
            {
                return value.DeepClone();
            }
        }

        return media.ItemSchema is { } item ? ForSchema(item) : media.Schema is { } schema ? ForSchema(schema) : null;
    }

    /// <summary>An example value of a schema.</summary>
    public DocNode ForSchema(OpenApiSchema schema) => Create(schema ?? throw new ArgumentNullException(nameof(schema)), 0, dispatch: true);

    private DocNode Create(OpenApiSchema schema, int depth, bool dispatch)
    {
        var s = schema.Resolved;
        if ((s.Example ?? s.Examples.FirstOrDefault() ?? s.Default ?? s.Const) is { } declared)
        {
            return declared.DeepClone();
        }

        if ((s.Enum.FirstOrDefault(e => e is not ScalarNode { IsNull: true }) ?? s.Enum.FirstOrDefault()) is { } first)
        {
            return first.DeepClone();
        }

        if (s.BooleanSchema is not null || depth > MaxDepth || _visiting.Contains(s))
        {
            return Null();
        }

        // A discriminated base is shown as its first subtype (which includes the base through allOf).
        if (dispatch && s.Discriminator is { } discriminator && s.OneOf.Count == 0 && s.AnyOf.Count == 0 &&
            Subtype(s, discriminator) is { } subtype)
        {
            var value = Create(subtype.Schema, depth, dispatch: false);
            if (value is ObjectNode obj)
            {
                obj.Add(discriminator.PropertyName, SourceLocation.None, Text(subtype.Value));
            }

            return value;
        }

        _visiting.Add(s);
        try
        {

            if (s.OneOf.Count > 0 || s.AnyOf.Count > 0)
            {
                var variant = s.OneOf.Count > 0 ? s.OneOf[0] : s.AnyOf[0];
                var value = Create(variant, depth + 1, dispatch: true);
                if (value is ObjectNode obj && s.Discriminator is { } d && !obj.ContainsKey(d.PropertyName))
                {
                    var mapped = d.Mapping.Entries.FirstOrDefault(m => Schema(m.Value) == variant.Resolved).Key;
                    obj.Add(d.PropertyName, SourceLocation.None, Text(mapped ?? SchemaName(variant.Resolved) ?? "value"));
                }

                return value;
            }

            var types = s.Types & ~JsonSchemaTypes.Null;
            if (s.AllOf.Count > 0 || s.Properties.Count > 0 || (types & JsonSchemaTypes.Object) != 0 || (types == JsonSchemaTypes.None && s.HasAdditionalProperties))
            {
                return ObjectValue(s, depth);
            }

            if ((types & JsonSchemaTypes.Array) != 0)
            {
                var array = new ArrayNode(SourceLocation.None);
                var count = Math.Max(1, s.MinItems ?? 1);
                for (var i = 0; i < Math.Min(count, 3); i++)
                {
                    array.Add(s.Items is { } items ? Create(items, depth + 1, dispatch: true) : Text("item"));
                }

                return array;
            }

            if ((types & JsonSchemaTypes.String) != 0)
            {
                return Text(StringValue(s));
            }

            if ((types & JsonSchemaTypes.Integer) != 0)
            {
                return NumberValue(s, integer: true);
            }

            if ((types & JsonSchemaTypes.Number) != 0)
            {
                return NumberValue(s, integer: false);
            }

            if ((types & JsonSchemaTypes.Boolean) != 0)
            {
                return new ScalarNode(SourceLocation.None, NodeKind.Boolean, "true");
            }

            return types == JsonSchemaTypes.None && (s.Types & JsonSchemaTypes.Null) != 0 ? Null() : new ObjectNode(SourceLocation.None);
        }
        finally
        {
            _visiting.Remove(s);
        }
    }

    private ObjectNode ObjectValue(OpenApiSchema schema, int depth)
    {
        var result = new ObjectNode(SourceLocation.None);
        foreach (var part in schema.AllOf)
        {
            if (Create(part, depth + 1, dispatch: false) is ObjectNode partValue)
            {
                foreach (var property in partValue.Properties)
                {
                    result.Add(property.Name, property.KeyLocation, property.Value);
                }
            }
        }

        foreach (var property in schema.Properties)
        {
            var resolved = property.Value.Resolved;
            var required = schema.Required.Contains(property.Key);
            if (resolved.WriteOnly || (!required && _visiting.Contains(resolved)))
            {
                continue;
            }

            result.Add(property.Key, SourceLocation.None, Create(property.Value, depth + 1, dispatch: true));
        }

        if (result.Count == 0 && schema.AdditionalProperties is { } additional)
        {
            result.Add("key", SourceLocation.None, Create(additional, depth + 1, dispatch: true));
        }

        return result;
    }

    /// <summary>The first subtype of a discriminated base: the first mapping, else a component that extends it.</summary>
    private (OpenApiSchema Schema, string Value)? Subtype(OpenApiSchema baseSchema, OpenApiDiscriminator discriminator)
    {
        foreach (var mapping in discriminator.Mapping.Entries)
        {
            if (Schema(mapping.Value) is { } mapped && mapped != baseSchema)
            {
                return (mapped, mapping.Key);
            }
        }

        foreach (var component in _document.Components.Schemas)
        {
            var candidate = component.Value.Resolved;
            if (candidate != baseSchema && candidate.AllOf.Any(a => a.Resolved == baseSchema))
            {
                return (candidate, component.Key);
            }
        }

        return null;
    }

    private OpenApiSchema? Schema(string reference)
    {
        var name = reference.Substring(reference.LastIndexOf('/') + 1);
        return _document.Components.Schemas.TryGetValue(name, out var schema) ? schema.Resolved : null;
    }

    private string? SchemaName(OpenApiSchema schema) =>
        _document.Components.Schemas.Entries.FirstOrDefault(e => e.Value.Resolved == schema).Key;

    private static string StringValue(OpenApiSchema schema)
    {
        var value = schema.Format switch
        {
            "date" => "2024-01-01",
            "date-time" => "2024-01-01T12:00:00Z",
            "time" => "12:00:00Z",
            "duration" => "PT1H",
            "uuid" or "guid" => "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            "email" => "user@example.com",
            "uri" or "url" => "https://example.com/",
            "uri-reference" => "/example",
            "hostname" => "example.com",
            "ipv4" => "192.0.2.1",
            "ipv6" => "2001:db8::1",
            "byte" => "AQID",
            "password" => "P@ssw0rd!",
            _ => "string",
        };

        if (schema.MinLength is { } min && value.Length < min)
        {
            value = value.PadRight(Math.Min(min, 4096), 'x'); // The contract is untrusted: bound the allocation.
        }

        if (schema.MaxLength is { } max && value.Length > max)
        {
            value = value.Substring(0, Math.Max(max, 0));
        }

        return value;
    }

    private static ScalarNode NumberValue(OpenApiSchema schema, bool integer)
    {
        var step = integer ? 1m : 0.5m;
        var value = schema.Minimum ??
            (schema.ExclusiveMinimum is { } exclusive ? (exclusive > decimal.MaxValue - step ? exclusive : exclusive + step) : integer ? 1m : 1.5m);
        if (schema.Maximum is { } max && value > max)
        {
            value = max;
        }

        if (schema.ExclusiveMaximum is { } exclusiveMax && value >= exclusiveMax)
        {
            value = exclusiveMax < decimal.MinValue + step ? exclusiveMax : exclusiveMax - step;
        }

        if (integer)
        {
            value = decimal.Ceiling(value);
            if (schema.ExclusiveMaximum is { } upper && value >= upper)
            {
                value = decimal.Floor(upper) == upper ? upper - 1 : decimal.Floor(upper);
            }
        }

        return new ScalarNode(SourceLocation.None, NodeKind.Number, value.ToString(CultureInfo.InvariantCulture));
    }

    private static ScalarNode Text(string value) => new(SourceLocation.None, NodeKind.String, value);

    private static ScalarNode Null() => new(SourceLocation.None, NodeKind.Null, "null");
}
