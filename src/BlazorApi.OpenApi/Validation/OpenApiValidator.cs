using System.Text.RegularExpressions;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Validation;

/// <summary>
/// Semantic validation of a reference-resolved document: rules the structure alone cannot express (unique
/// operation ids, path templates matching path parameters, defined security schemes, coherent schemas, ...).
/// </summary>
public static class OpenApiValidator
{
    private static readonly string[] IgnoredHeaderNames = ["Accept", "Content-Type", "Authorization"];

    /// <summary>Validates a document, adding diagnostics to <paramref name="diagnostics"/>.</summary>
    public static void Validate(OpenApiDocument document, DiagnosticBag diagnostics)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        new Run(document, diagnostics).Execute();
    }

    /// <summary>
    /// The variable names of a path template in order, or <see langword="null"/> (with <paramref name="error"/>) when the
    /// template is malformed.
    /// </summary>
    public static IReadOnlyList<string>? ParsePathTemplate(string path, out string? error)
    {
        error = null;
        var names = new List<string>();
        var i = 0;
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '}')
            {
                error = "unmatched '}'";
                return null;
            }

            if (c != '{')
            {
                i++;
                continue;
            }

            var end = path.IndexOf('}', i + 1);
            if (end < 0)
            {
                error = "unmatched '{'";
                return null;
            }

            var name = path.Substring(i + 1, end - i - 1);
            if (name.Length == 0)
            {
                error = "empty template variable '{}'";
                return null;
            }

            if (name.IndexOf('{') >= 0)
            {
                error = "nested '{'";
                return null;
            }

            if (names.Contains(name))
            {
                error = $"template variable '{{{name}}}' appears more than once";
                return null;
            }

            names.Add(name);
            i = end + 1;
        }

        return names;
    }

    private sealed class Run
    {
        private readonly OpenApiDocument _document;
        private readonly DiagnosticBag _diagnostics;
        private readonly HashSet<OpenApiSchema> _allOfMembers = [];

        public Run(OpenApiDocument document, DiagnosticBag diagnostics)
        {
            _document = document;
            _diagnostics = diagnostics;
        }

        public void Execute()
        {
            ValidateServers(_document.Servers);
            ValidatePaths();
            ValidateSecurityRequirements(_document.Security);

            var elements = OpenApiWalker.Descendants(_document, followReferences: true).ToList();
            foreach (var schema in elements.OfType<OpenApiSchema>())
            {
                foreach (var member in schema.AllOf)
                {
                    _allOfMembers.Add(member);
                }
            }

            foreach (var element in elements)
            {
                switch (element)
                {
                    case OpenApiSchema schema when !schema.IsReference:
                        ValidateSchema(schema);
                        break;
                    case OpenApiSecurityScheme scheme when !scheme.IsReference:
                        ValidateSecurityScheme(scheme);
                        break;
                }
            }
        }

        // -------------------------------------------------------------- paths and operations

        private void ValidatePaths()
        {
            var operationIds = new Dictionary<string, OpenApiOperation>(StringComparer.Ordinal);
            var normalizedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in _document.Paths)
            {
                var pathItem = entry.Value.Resolved;
                var templateNames = ParsePathTemplate(entry.Key, out var templateError);
                if (templateNames is null)
                {
                    Report(DiagnosticDescriptors.InvalidPathTemplate, entry.Value, entry.Key, templateError);
                    templateNames = [];
                }
                else
                {
                    var normalized = NormalizePath(entry.Key);
                    if (normalizedPaths.TryGetValue(normalized, out var other))
                    {
                        Report(DiagnosticDescriptors.EquivalentPaths, entry.Value, entry.Key, other);
                    }
                    else
                    {
                        normalizedPaths[normalized] = entry.Key;
                    }
                }

                ValidateServers(pathItem.Servers);
                CheckDuplicateParameters(pathItem.Parameters, $"path '{entry.Key}'");
                foreach (var operation in pathItem.Operations.Values)
                {
                    if (operation.OperationId is { } id)
                    {
                        if (operationIds.TryGetValue(id, out var first))
                        {
                            Report(DiagnosticDescriptors.DuplicateOperationId, operation, id, $"{first.Method.ToUpperInvariant()} {first.Path}");
                        }
                        else
                        {
                            operationIds[id] = operation;
                        }
                    }

                    ValidateOperation(operation, pathItem, templateNames);
                }
            }
        }

        private void ValidateOperation(OpenApiOperation operation, OpenApiPathItem pathItem, IReadOnlyList<string> templateNames)
        {
            CheckDuplicateParameters(operation.Parameters, operation.DisplayName);
            ValidateServers(operation.Servers);
            if (operation.Security is not null)
            {
                ValidateSecurityRequirements(operation.Security);
            }

            if (operation.Responses.Count == 0)
            {
                Report(DiagnosticDescriptors.MissingResponses, operation, operation.DisplayName);
            }

            // Operation parameters override path-item parameters with the same name and location.
            var effective = new List<OpenApiParameter>();
            foreach (var parameter in operation.Parameters.Select(p => p.Resolved))
            {
                effective.Add(parameter);
            }

            foreach (var parameter in pathItem.Parameters.Select(p => p.Resolved))
            {
                if (!effective.Any(p => SameParameter(p, parameter)))
                {
                    effective.Add(parameter);
                }
            }

            foreach (var parameter in effective)
            {
                ValidateParameter(parameter, operation, templateNames);
            }

            foreach (var name in templateNames)
            {
                if (!effective.Any(p => p.In == ParameterLocation.Path && p.Name == name))
                {
                    Report(DiagnosticDescriptors.PathParameterNotDeclared, operation, name, operation.DisplayName);
                }
            }
        }

        private void ValidateParameter(OpenApiParameter parameter, OpenApiOperation operation, IReadOnlyList<string> templateNames)
        {
            if (parameter.In == ParameterLocation.Path)
            {
                if (!templateNames.Contains(parameter.Name))
                {
                    Report(DiagnosticDescriptors.PathParameterNotInTemplate, parameter, parameter.Name, operation.DisplayName);
                }

                if (!parameter.Required)
                {
                    Report(DiagnosticDescriptors.PathParameterNotRequired, parameter, parameter.Name, operation.DisplayName);
                }
            }

            if (parameter.In == ParameterLocation.Header &&
                IgnoredHeaderNames.Any(n => string.Equals(n, parameter.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Report(DiagnosticDescriptors.FeatureIgnored, parameter,
                    $"Header parameter '{parameter.Name}' of {operation.DisplayName} (the specification requires Accept, Content-Type and Authorization parameters to be ignored)");
            }

            var hasSchema = parameter.Schema is not null;
            var hasContent = parameter.Content.Count > 0;
            if (hasSchema == hasContent)
            {
                Report(DiagnosticDescriptors.ParameterSchemaOrContent, parameter, parameter.Name, hasSchema ? " (both are present)" : " (neither is present)");
            }
            else if (parameter.Content.Count > 1)
            {
                Report(DiagnosticDescriptors.ParameterSchemaOrContent, parameter, parameter.Name, " ('content' must have exactly one entry)");
            }

            if (parameter.Style is { } style && parameter.In is { } location && !IsValidStyle(location, style))
            {
                Report(DiagnosticDescriptors.InvalidParameterStyle, parameter, style, location.ToString().ToLowerInvariant(), parameter.Name);
            }
        }

        private void CheckDuplicateParameters(IReadOnlyList<OpenApiParameter> parameters, string owner)
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i].Resolved;
                for (var j = 0; j < i; j++)
                {
                    if (SameParameter(parameter, parameters[j].Resolved))
                    {
                        Report(DiagnosticDescriptors.DuplicateParameter, parameters[i], parameter.Name, parameter.In?.ToString().ToLowerInvariant(), owner);
                        break;
                    }
                }
            }
        }

        private static bool SameParameter(OpenApiParameter a, OpenApiParameter b) =>
            a.In == b.In && (a.In == ParameterLocation.Header
                ? string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(a.Name, b.Name, StringComparison.Ordinal));

        private static bool IsValidStyle(ParameterLocation location, string style) => location switch
        {
            ParameterLocation.Path => style is "matrix" or "label" or "simple",
            ParameterLocation.Query => style is "form" or "spaceDelimited" or "pipeDelimited" or "deepObject",
            ParameterLocation.Header => style is "simple",
            ParameterLocation.Cookie => style is "form" or "cookie",
            _ => false,
        };

        private static string NormalizePath(string path)
        {
            var builder = new System.Text.StringBuilder(path.Length);
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

        // -------------------------------------------------------------- servers and security

        private void ValidateServers(IReadOnlyList<OpenApiServer> servers)
        {
            foreach (var server in servers)
            {
                var names = ParsePathTemplate(server.Url, out _) ?? [];
                foreach (var name in names)
                {
                    if (!server.Variables.ContainsKey(name))
                    {
                        Report(DiagnosticDescriptors.ServerVariableNotDefined, server, server.Url, name);
                    }
                }
            }
        }

        private void ValidateSecurityRequirements(IReadOnlyList<OpenApiSecurityRequirement> requirements)
        {
            foreach (var requirement in requirements)
            {
                foreach (var name in requirement.Schemes.Keys)
                {
                    if (!_document.Components.SecuritySchemes.ContainsKey(name))
                    {
                        Report(DiagnosticDescriptors.UnknownSecurityScheme, requirement, name);
                    }
                }
            }
        }

        private void ValidateSecurityScheme(OpenApiSecurityScheme scheme)
        {
            switch (scheme.Type)
            {
                case SecuritySchemeType.ApiKey:
                    if (string.IsNullOrEmpty(scheme.Name))
                    {
                        Report(DiagnosticDescriptors.RequiredFieldMissing, scheme, "An apiKey security scheme", "name");
                    }

                    if (scheme.In is null)
                    {
                        Report(DiagnosticDescriptors.RequiredFieldMissing, scheme, "An apiKey security scheme", "in");
                    }

                    break;
                case SecuritySchemeType.Http:
                    if (string.IsNullOrEmpty(scheme.Scheme))
                    {
                        Report(DiagnosticDescriptors.RequiredFieldMissing, scheme, "An http security scheme", "scheme");
                    }

                    break;
                case SecuritySchemeType.OAuth2:
                    if (scheme.Flows is null)
                    {
                        Report(DiagnosticDescriptors.RequiredFieldMissing, scheme, "An oauth2 security scheme", "flows");
                    }

                    break;
                case SecuritySchemeType.OpenIdConnect:
                    if (string.IsNullOrEmpty(scheme.OpenIdConnectUrl))
                    {
                        Report(DiagnosticDescriptors.RequiredFieldMissing, scheme, "An openIdConnect security scheme", "openIdConnectUrl");
                    }

                    break;
            }
        }

        // -------------------------------------------------------------- schemas

        private void ValidateSchema(OpenApiSchema schema)
        {
            if (schema.Properties.Count > 0 && schema.AllOf.Count == 0 && !_allOfMembers.Contains(schema))
            {
                foreach (var name in schema.Required)
                {
                    if (!schema.Properties.ContainsKey(name))
                    {
                        Report(DiagnosticDescriptors.RequiredPropertyNotDefined, schema, name);
                    }
                }
            }

            if (schema.Discriminator is { PropertyName.Length: > 0 } discriminator)
            {
                ValidateDiscriminator(schema, discriminator);
            }

            if (schema.MinLength > schema.MaxLength)
            {
                Report(DiagnosticDescriptors.InvalidConstraint, schema, $"minLength ({schema.MinLength}) is greater than maxLength ({schema.MaxLength})");
            }

            if (schema.MinItems > schema.MaxItems)
            {
                Report(DiagnosticDescriptors.InvalidConstraint, schema, $"minItems ({schema.MinItems}) is greater than maxItems ({schema.MaxItems})");
            }

            if (schema.Minimum > schema.Maximum)
            {
                Report(DiagnosticDescriptors.InvalidConstraint, schema, $"minimum ({schema.Minimum}) is greater than maximum ({schema.Maximum})");
            }

            if (schema.MultipleOf is <= 0)
            {
                Report(DiagnosticDescriptors.InvalidConstraint, schema, $"multipleOf must be greater than 0 but is {schema.MultipleOf}");
            }

            if (schema.Pattern is { } pattern)
            {
                try
                {
                    _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    Report(DiagnosticDescriptors.InvalidPattern, schema, pattern, ex.Message);
                }
            }

            var types = schema.Types & ~JsonSchemaTypes.Null;
            if (types != JsonSchemaTypes.None)
            {
                foreach (var value in schema.Enum)
                {
                    CheckValue("Enum", value, schema, types);
                }

                if (schema.Default is { } defaultValue)
                {
                    CheckValue("Default", defaultValue, schema, types);
                }
            }
        }

        private void CheckValue(string what, DocNode value, OpenApiSchema schema, JsonSchemaTypes types)
        {
            // Null values are judged by the nullability rules of the API model, not here.
            if (value.Kind == NodeKind.Null)
            {
                return;
            }

            if (!Matches(value, types))
            {
                Report(DiagnosticDescriptors.ValueTypeMismatch, schema, what, Truncate(value.ToJson()), TypesText(types));
            }
        }

        private static bool Matches(DocNode value, JsonSchemaTypes types) => value.Kind switch
        {
            NodeKind.String => (types & JsonSchemaTypes.String) != 0,
            NodeKind.Boolean => (types & JsonSchemaTypes.Boolean) != 0,
            NodeKind.Number => (types & JsonSchemaTypes.Number) != 0 ||
                ((types & JsonSchemaTypes.Integer) != 0 && value is ScalarNode s && s.TryGetDecimal(out var d) && d == decimal.Truncate(d)),
            NodeKind.Array => (types & JsonSchemaTypes.Array) != 0,
            NodeKind.Object => (types & JsonSchemaTypes.Object) != 0,
            _ => true,
        };

        private void ValidateDiscriminator(OpenApiSchema schema, OpenApiDiscriminator discriminator)
        {
            var name = discriminator.PropertyName;
            if (CollectPropertyNames(schema).Contains(name))
            {
                return;
            }

            var variants = schema.OneOf.Count > 0 ? schema.OneOf : schema.AnyOf;
            if (variants.Count > 0 && variants.All(v => CollectPropertyNames(v).Contains(name)))
            {
                return;
            }

            Report(DiagnosticDescriptors.DiscriminatorPropertyMissing, schema, name, variants.Count > 0 ? "the schema or all of its variants" : "the schema");
        }

        private static HashSet<string> CollectPropertyNames(OpenApiSchema schema)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<OpenApiSchema>();
            Collect(schema);
            return names;

            void Collect(OpenApiSchema s)
            {
                if (!visited.Add(s))
                {
                    return;
                }

                if (s.ReferenceTarget is { } target)
                {
                    Collect(target);
                }

                foreach (var key in s.Properties.Keys)
                {
                    names.Add(key);
                }

                foreach (var member in s.AllOf)
                {
                    Collect(member);
                }
            }
        }

        private static string TypesText(JsonSchemaTypes types) =>
            string.Join(", ", Enum.GetValues(typeof(JsonSchemaTypes)).Cast<JsonSchemaTypes>()
                .Where(t => t != JsonSchemaTypes.None && (types & t) != 0)
                .Select(t => t.ToString().ToLowerInvariant()));

        private static string Truncate(string text) => text.Length <= 60 ? text : text.Substring(0, 57) + "...";

        private void Report(DiagnosticDescriptor descriptor, OpenApiElement element, params object?[] args) =>
            _diagnostics.Report(descriptor, element.Location, element.Pointer, args);
    }
}
