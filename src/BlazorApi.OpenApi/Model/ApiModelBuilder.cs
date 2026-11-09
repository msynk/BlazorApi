using System.Globalization;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.References;
using BlazorApi.OpenApi.Syntax;
using BlazorApi.OpenApi.Validation;

namespace BlazorApi.OpenApi.Model;

/// <summary>Builds the normalized <see cref="ApiModel"/> from a loaded, reference-resolved document.</summary>
public static class ApiModelBuilder
{
    /// <summary>Builds the API model, reporting representation problems to <paramref name="diagnostics"/>.</summary>
    public static ApiModel Build(OpenApiDocument document, DiagnosticBag diagnostics)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        return new Builder(document, diagnostics).Build();
    }

    private enum Shape
    {
        Any,
        Primitive,
        Array,
        Map,
        Object,
        Enum,
        Union,
    }

    private sealed class Builder
    {
        private static readonly HashSet<string> KnownStringFormats = new(StringComparer.Ordinal)
        {
            "email", "idn-email", "hostname", "idn-hostname", "ipv4", "ipv6", "uri-template", "iri", "iri-reference",
            "json-pointer", "relative-json-pointer", "regex", "password", "char", "binary", "byte", "date", "date-time",
            "time", "duration", "uuid", "guid", "uri", "uri-reference", "url", "html", "markdown", "phone", "string",
        };

        private static readonly HashSet<string> KnownIntegerFormats = new(StringComparer.Ordinal)
        {
            "int8", "int16", "int32", "int64", "uint8", "uint16", "uint32", "uint64", "integer", "timestamp", "unix-time",
        };

        private static readonly HashSet<string> KnownNumberFormats = new(StringComparer.Ordinal)
        {
            "float", "double", "decimal", "decimal128", "int32", "int64", "number",
        };

        private readonly OpenApiDocument _document;
        private readonly DiagnosticBag _diagnostics;
        private readonly Dictionary<OpenApiSchema, ApiTypeRef> _mapped = new(IdentityComparer<OpenApiSchema>.Instance);
        private readonly Dictionary<OpenApiSchema, ApiNamedType> _named = new(IdentityComparer<OpenApiSchema>.Instance);
        private readonly HashSet<OpenApiSchema> _mapping = new(IdentityComparer<OpenApiSchema>.Instance);
        private readonly HashSet<OpenApiSchema> _reported = new(IdentityComparer<OpenApiSchema>.Instance);
        private readonly List<ApiNamedType> _types = [];
        private readonly Dictionary<string, ApiSecurityScheme> _schemes = new(StringComparer.Ordinal);

        public Builder(OpenApiDocument document, DiagnosticBag diagnostics)
        {
            _document = document;
            _diagnostics = diagnostics;
        }

        public ApiModel Build()
        {
            foreach (var entry in _document.Components.Schemas)
            {
                Map(entry.Value, [entry.Key]);
            }

            foreach (var entry in _document.Components.SecuritySchemes)
            {
                _schemes[entry.Key] = new ApiSecurityScheme(entry.Key, entry.Value.Resolved);
            }

            var operations = new List<ApiOperation>();
            foreach (var path in _document.Paths)
            {
                var pathItem = path.Value.Resolved;
                foreach (var operation in pathItem.Operations.Values)
                {
                    operations.Add(BuildOperation(operation, pathItem, path.Key));
                }
            }

            CompleteHierarchies();
            foreach (var operation in operations)
            {
                operation.Pagination = PaginationDetector.Detect(operation, _diagnostics);
            }

            return new ApiModel
            {
                Document = _document,
                Title = _document.Info.Title,
                Version = _document.Info.Version,
                Description = _document.Info.Description,
                Servers = _document.Servers.Select(ToServer).ToArray(),
                Operations = operations,
                Types = _types,
                SecuritySchemes = _schemes.Values.ToArray(),
                Tags = _document.Tags,
            };
        }

        // ------------------------------------------------------------------ operations

        private ApiOperation BuildOperation(OpenApiOperation source, OpenApiPathItem pathItem, string path)
        {
            var nameHint = source.OperationId is { Length: > 0 } id
                ? new[] { id }
                : new[] { source.Method }.Concat(path.Split('/').Where(s => s.Length > 0).Select(s => s.Trim('{', '}'))).ToArray();
            var operation = new ApiOperation(source)
            {
                NameHint = nameHint,
                Method = source.Method.ToUpperInvariant(),
                Path = path,
                Summary = source.Summary ?? pathItem.Summary,
                Description = source.Description ?? pathItem.Description,
                Deprecated = source.Deprecated,
                Tags = source.Tags,
            };

            operation.Parameters = BuildParameters(source, pathItem, path, nameHint);
            if (source.RequestBody is { } body)
            {
                var resolvedBody = body.Resolved;
                operation.RequestBody = new ApiRequestBody
                {
                    Required = resolvedBody.Required,
                    Description = body.ReferenceDescription ?? resolvedBody.Description,
                    Contents = BuildContents(resolvedBody.Content, [.. nameHint, "Request"]),
                };
            }

            operation.Responses = BuildResponses(source, nameHint);
            operation.Security = BuildSecurity(source.Security ?? _document.Security);
            var servers = source.Servers.Count > 0 ? source.Servers : pathItem.Servers;
            operation.Servers = servers.Select(ToServer).ToArray();
            return operation;
        }

        private IReadOnlyList<ApiParameter> BuildParameters(OpenApiOperation source, OpenApiPathItem pathItem, string path, string[] nameHint)
        {
            var effective = new List<OpenApiParameter>();
            foreach (var parameter in pathItem.Parameters.Select(p => p.Resolved))
            {
                effective.Add(parameter);
            }

            foreach (var parameter in source.Parameters.Select(p => p.Resolved))
            {
                var index = effective.FindIndex(p => p.In == parameter.In && SameName(p, parameter));
                if (index >= 0)
                {
                    effective[index] = parameter;
                }
                else
                {
                    effective.Add(parameter);
                }
            }

            var templateOrder = OpenApiValidator.ParsePathTemplate(path, out _) ?? [];
            var ordered = effective
                .Where(p => p.In == ParameterLocation.Path)
                .OrderBy(p => IndexOf(templateOrder, p.Name))
                .Concat(effective.Where(p => p.In != ParameterLocation.Path));

            var result = new List<ApiParameter>();
            foreach (var parameter in ordered)
            {
                if (parameter.In is not { } location || parameter.Name.Length == 0)
                {
                    continue;
                }

                if (location == ParameterLocation.Header &&
                    (string.Equals(parameter.Name, "Accept", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(parameter.Name, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(parameter.Name, "Authorization", StringComparison.OrdinalIgnoreCase)))
                {
                    continue; // The specification requires these to be ignored (reported by validation).
                }

                result.Add(BuildParameter(parameter, location, nameHint));
            }

            return result;
        }

        private ApiParameter BuildParameter(OpenApiParameter source, ParameterLocation location, string[] nameHint)
        {
            OpenApiSchema? schema = source.Schema;
            string? contentMediaType = null;
            if (schema is null && source.Content.Count > 0)
            {
                var content = source.Content.Entries[0];
                contentMediaType = content.Key;
                schema = content.Value.Schema;
            }

            var type = Map(schema, [.. nameHint, source.Name]);
            var defaultStyle = location is ParameterLocation.Path or ParameterLocation.Header ? ApiParameterStyle.Simple : ApiParameterStyle.Form;
            var style = source.Style switch
            {
                "simple" => ApiParameterStyle.Simple,
                "form" => ApiParameterStyle.Form,
                "matrix" => ApiParameterStyle.Matrix,
                "label" => ApiParameterStyle.Label,
                "spaceDelimited" => ApiParameterStyle.SpaceDelimited,
                "pipeDelimited" => ApiParameterStyle.PipeDelimited,
                "deepObject" => ApiParameterStyle.DeepObject,
                _ => defaultStyle,
            };

            var parameter = new ApiParameter(source, source.Name, location, type)
            {
                Required = source.Required || location == ParameterLocation.Path,
                Deprecated = source.Deprecated,
                Description = source.Description,
                Style = style,
                Explode = source.Explode ?? style == ApiParameterStyle.Form,
                AllowReserved = source.AllowReserved,
                ContentMediaType = contentMediaType,
                Default = schema?.Resolved.Default,
                Constraints = schema is null ? ApiConstraints.None : ApiConstraints.From(schema.Resolved),
            };

            if (contentMediaType is null)
            {
                CheckParameterSerialization(parameter, source);
            }

            return parameter;
        }

        private void CheckParameterSerialization(ApiParameter parameter, OpenApiParameter source)
        {
            var type = parameter.Type;
            var isObject = type is ApiMapType || (type is ApiNamedTypeRef { Target: ApiObjectType });
            var isArray = type is ApiArrayType;
            string? problem = null;
            if (parameter.Style == ApiParameterStyle.DeepObject && !isObject)
            {
                problem = "style 'deepObject' applies to objects only; the value is sent with style 'form'";
                parameter.Style = ApiParameterStyle.Form;
            }
            else if (parameter.Style is ApiParameterStyle.SpaceDelimited or ApiParameterStyle.PipeDelimited && !isArray)
            {
                problem = $"style '{source.Style}' applies to arrays only; the value is sent with style 'form'";
                parameter.Style = ApiParameterStyle.Form;
            }
            else if (isObject && type is ApiNamedTypeRef { Target: ApiObjectType obj } &&
                     obj.AllProperties.Any(p => p.Type is ApiNamedTypeRef { Target: ApiObjectType } or ApiArrayType or ApiMapType))
            {
                problem = "nested objects and arrays inside an object parameter have no standard serialization; nested values are sent as JSON";
            }
            else if (isArray && type is ApiArrayType { Items: ApiArrayType or ApiMapType or ApiNamedTypeRef { Target: ApiObjectType } })
            {
                problem = "arrays of objects or arrays have no standard parameter serialization; items are sent as JSON";
            }

            if (problem is not null)
            {
                _diagnostics.Report(DiagnosticDescriptors.UnsupportedParameterSerialization, source.Location, source.Pointer, parameter.Name, problem);
            }
        }

        private IReadOnlyList<ApiResponse> BuildResponses(OpenApiOperation source, string[] nameHint)
        {
            var hasSuccessCode = source.Responses.Keys.Any(k => k.StartsWith("2", StringComparison.Ordinal));
            var responses = new List<ApiResponse>();
            foreach (var entry in source.Responses)
            {
                var response = entry.Value.Resolved;
                var api = new ApiResponse(entry.Key)
                {
                    Description = entry.Value.ReferenceDescription ?? response.Description,
                };

                if (entry.Key != "default")
                {
                    api.StatusClass = entry.Key[0] - '0';
                    if (int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var status))
                    {
                        api.Status = status;
                    }
                }

                api.IsSuccess = api.StatusClass == 2 || (api.IsDefault && !hasSuccessCode);
                string[] hint = api.IsSuccess && !api.IsDefault
                    ? (source.Responses.Keys.Count(k => k.StartsWith("2", StringComparison.Ordinal)) > 1 ? [.. nameHint, entry.Key, "Response"] : [.. nameHint, "Response"])
                    : [.. nameHint, entry.Key == "default" ? "Default" : entry.Key, "Response"];
                api.Contents = BuildContents(response.Content, hint);
                api.Headers = response.Headers
                    .Where(h => !string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    .Select(h =>
                    {
                        var header = h.Value.Resolved;
                        var schema = header.Schema ?? header.Content.Values.FirstOrDefault()?.Schema;
                        return new ApiHeader(h.Key, Map(schema, [.. hint, h.Key]))
                        {
                            Required = header.Required,
                            Description = header.Description,
                        };
                    })
                    .ToArray();
                responses.Add(api);
            }

            return responses
                .OrderBy(r => r.IsDefault ? 2 : r.Status is null ? 1 : 0)
                .ThenBy(r => r.Status ?? r.StatusClass ?? 0)
                .ToArray();
        }

        private IReadOnlyList<ApiContent> BuildContents(OrderedMap<OpenApiMediaType> content, string[] hint)
        {
            var result = new List<ApiContent>();
            foreach (var entry in content)
            {
                var media = entry.Value;
                var kind = ClassifyMediaType(entry.Key, media.Schema);
                ApiTypeRef? type;
                switch (kind)
                {
                    case ApiBodyKind.Binary:
                        type = new ApiPrimitiveType(ApiPrimitiveKind.Binary);
                        break;
                    case ApiBodyKind.Text:
                    case ApiBodyKind.Other:
                        type = media.Schema is null ? new ApiPrimitiveType(ApiPrimitiveKind.String) : Map(media.Schema, hint);
                        if (type is not ApiPrimitiveType)
                        {
                            type = new ApiPrimitiveType(ApiPrimitiveKind.String);
                        }

                        if (kind == ApiBodyKind.Other)
                        {
                            _diagnostics.Report(DiagnosticDescriptors.UnsupportedMediaType, media.Location, media.Pointer, entry.Key,
                                "has no typed serializer; the body is exchanged as a string");
                        }

                        break;
                    default:
                        type = media.Schema is null ? null : Map(media.Schema, hint);
                        break;
                }

                var api = new ApiContent(entry.Key, kind, type, media);
                if (kind is ApiBodyKind.Multipart or ApiBodyKind.FormUrlEncoded && type is not null)
                {
                    api.Parts = BuildParts(type, media);
                }

                result.Add(api);
            }

            return result;
        }

        private static IReadOnlyList<ApiBodyPart> BuildParts(ApiTypeRef type, OpenApiMediaType media)
        {
            if (type is not ApiNamedTypeRef { Target: ApiObjectType obj })
            {
                return [];
            }

            return obj.AllProperties
                .Where(p => !p.ReadOnly)
                .Select(p => new ApiBodyPart(p.JsonName, p.Type)
                {
                    Required = p.Required,
                    Description = p.Description,
                    ContentType = media.Encoding.TryGetValue(p.JsonName, out var encoding) ? encoding.ContentType : null,
                })
                .ToArray();
        }

        private static ApiBodyKind ClassifyMediaType(string mediaType, OpenApiSchema? schema)
        {
            var semicolon = mediaType.IndexOf(';');
            var type = (semicolon >= 0 ? mediaType.Substring(0, semicolon) : mediaType).Trim().ToLowerInvariant();
            var resolved = schema?.Resolved;
            var isBinarySchema = resolved is not null &&
                ((resolved.Format is "binary") || (resolved.ContentMediaType is not null && resolved.ContentEncoding is null && !resolved.HasType) ||
                 (resolved.ContentMediaType is not null && (resolved.Types & JsonSchemaTypes.String) != 0 && resolved.ContentEncoding is null));

            if (type is "application/json" or "text/json" || type.EndsWith("+json", StringComparison.Ordinal) || type == "application/x-ndjson")
            {
                return ApiBodyKind.Json;
            }

            if (type == "application/x-www-form-urlencoded")
            {
                return ApiBodyKind.FormUrlEncoded;
            }

            if (type.StartsWith("multipart/", StringComparison.Ordinal))
            {
                return ApiBodyKind.Multipart;
            }

            if (type is "application/xml" || type.EndsWith("+xml", StringComparison.Ordinal))
            {
                return ApiBodyKind.Other;
            }

            if (type.StartsWith("text/", StringComparison.Ordinal))
            {
                return isBinarySchema ? ApiBodyKind.Binary : ApiBodyKind.Text;
            }

            if (type is "*/*" or "application/*")
            {
                if (resolved is null || isBinarySchema)
                {
                    return ApiBodyKind.Binary;
                }

                return (resolved.Types & ~JsonSchemaTypes.Null) == JsonSchemaTypes.String && !resolved.HasEnum ? ApiBodyKind.Text : ApiBodyKind.Json;
            }

            return ApiBodyKind.Binary;
        }

        private IReadOnlyList<ApiSecurityRequirement> BuildSecurity(IReadOnlyList<OpenApiSecurityRequirement> requirements) =>
            requirements
                .Select(r => new ApiSecurityRequirement(r.Schemes
                    .Where(s => _schemes.ContainsKey(s.Key))
                    .Select(s => new ApiSecurityRequirementItem(_schemes[s.Key], s.Value))
                    .ToArray()))
                .ToArray();

        private static ApiServer ToServer(OpenApiServer server) => new(server.Url, server.Description, server.Variables);

        // ------------------------------------------------------------------ schema mapping

        private ApiTypeRef Map(OpenApiSchema? schema, string[] hint)
        {
            if (schema is null)
            {
                return new ApiPrimitiveType(ApiPrimitiveKind.Any);
            }

            if (_mapped.TryGetValue(schema, out var cached))
            {
                return cached;
            }

            if (_named.TryGetValue(schema, out var named))
            {
                // A named type that is still being built (a recursive reference) is referenced, not rebuilt.
                return new ApiNamedTypeRef(named, schema.IsNullable);
            }

            if (!_mapping.Add(schema))
            {
                // Re-entered while mapping a non-named schema: only an array or map alias that contains itself can
                // get here, and such a type has no finite representation.
                ReportOnce(schema, DiagnosticDescriptors.UnsupportedSchemaKeyword, "$ref", "a recursive array or map alias has no named type to refer to; it is typed as untyped JSON");
                return new ApiPrimitiveType(ApiPrimitiveKind.Any);
            }

            try
            {
                var result = MapCore(schema, hint);
                _mapped[schema] = result;
                return result;
            }
            finally
            {
                _mapping.Remove(schema);
            }
        }

        private ApiTypeRef MapCore(OpenApiSchema schema, string[] hint)
        {
            if (schema.BooleanSchema is not null)
            {
                return new ApiPrimitiveType(ApiPrimitiveKind.Any);
            }

            if (schema.IsReference)
            {
                if (schema.ReferenceTarget is not { } target)
                {
                    return new ApiPrimitiveType(ApiPrimitiveKind.Any, isNullable: true);
                }

                if (schema.HasStructuralKeywords)
                {
                    // OpenAPI 3.1: '$ref' with sibling keywords means "all of the target and the siblings".
                    return MapNamed(schema, hint, Shape.Object, nullable: schema.IsNullable);
                }

                var referenced = Map(target, hint);
                return schema.IsNullable && !referenced.IsNullable ? referenced.WithNullable(true) : referenced;
            }

            if (TryUnwrapNullable(schema, out var inner, out var innerNullable))
            {
                var unwrapped = Map(inner, hint);
                return innerNullable && !unwrapped.IsNullable ? unwrapped.WithNullable(true) : unwrapped;
            }

            var shape = Classify(schema);
            ReportUnsupportedKeywords(schema);
            switch (shape)
            {
                case Shape.Object:
                case Shape.Enum:
                case Shape.Union:
                    return MapNamed(schema, hint, shape, schema.IsNullable);
                case Shape.Array:
                    var items = schema.Items is null ? new ApiPrimitiveType(ApiPrimitiveKind.Any) : Map(schema.Items, [.. hint, "Item"]);
                    return new ApiArrayType(items, schema.UniqueItems, schema.IsNullable);
                case Shape.Map:
                    var values = schema.AdditionalProperties is null ? new ApiPrimitiveType(ApiPrimitiveKind.Any) : Map(schema.AdditionalProperties, [.. hint, "Value"]);
                    return new ApiMapType(values, schema.IsNullable);
                case Shape.Primitive:
                    return MapPrimitive(schema);
                default:
                    return new ApiPrimitiveType(ApiPrimitiveKind.Any, isNullable: schema.IsNullable);
            }
        }

        private ApiTypeRef MapNamed(OpenApiSchema schema, string[] hint, Shape shape, bool nullable)
        {
            if (!_named.TryGetValue(schema, out var named))
            {
                var componentName = ComponentName(schema);
                var nameHint = componentName is null ? hint : new[] { componentName };
                var origin = componentName is null ? ApiTypeOrigin.Inline : ApiTypeOrigin.Component;
                named = shape switch
                {
                    Shape.Enum => BuildEnum(schema, nameHint, origin),
                    Shape.Union => BuildUnion(schema, nameHint, origin),
                    _ => BuildObject(schema, nameHint, origin),
                };
            }

            if (named is null)
            {
                return new ApiPrimitiveType(ApiPrimitiveKind.Any, isNullable: nullable);
            }

            return new ApiNamedTypeRef(named, nullable);
        }

        /// <summary>
        /// Recognizes nullable wrappers: <c>allOf: [X]</c> with nothing else (the OpenAPI 3.0 idiom for a nullable or
        /// documented reference) and <c>oneOf</c>/<c>anyOf</c> of one type plus <c>null</c>.
        /// </summary>
        private static bool TryUnwrapNullable(OpenApiSchema schema, out OpenApiSchema inner, out bool nullable)
        {
            inner = schema;
            nullable = schema.IsNullable;
            var otherTypes = schema.Types & ~(JsonSchemaTypes.Null | JsonSchemaTypes.Object);
            var hasStructure = schema.Properties.Count > 0 || schema.HasEnum || schema.Discriminator is not null ||
                schema.AdditionalProperties is not null || schema.Items is not null || otherTypes != JsonSchemaTypes.None;
            if (hasStructure)
            {
                return false;
            }

            if (schema.AllOf.Count == 1 && schema.OneOf.Count == 0 && schema.AnyOf.Count == 0)
            {
                inner = schema.AllOf[0];
                return true;
            }

            var variants = schema.OneOf.Count > 0 ? schema.OneOf : schema.AnyOf;
            if (schema.AllOf.Count == 0 && variants.Count > 0 && (schema.OneOf.Count == 0 || schema.AnyOf.Count == 0))
            {
                var nonNull = variants.Where(v => !IsNullOnly(v)).ToList();
                if (nonNull.Count == 1 && variants.Count > 1)
                {
                    inner = nonNull[0];
                    nullable = true;
                    return true;
                }
            }

            return false;
        }

        private static bool IsNullOnly(OpenApiSchema schema)
        {
            var resolved = schema.Resolved;
            return resolved.Types == JsonSchemaTypes.Null && !resolved.HasStructuralKeywordsExceptType();
        }

        private Shape Classify(OpenApiSchema schema)
        {
            if (schema.HasEnum)
            {
                return Shape.Enum;
            }

            var variants = schema.OneOf.Count > 0 ? schema.OneOf : schema.AnyOf;
            if (variants.Count(v => !IsNullOnly(v)) > 1)
            {
                if (schema.Properties.Count > 0)
                {
                    ReportOnce(schema, DiagnosticDescriptors.UnsupportedSchemaKeyword, schema.OneOf.Count > 0 ? "oneOf" : "anyOf",
                        "properties declared next to oneOf/anyOf are not generated; move them into each variant or an allOf base");
                }

                return Shape.Union;
            }

            if (schema.AllOf.Count > 0)
            {
                return schema.AllOf.Any(m => IsObjectLike(m.Resolved)) || schema.Properties.Count > 0 ? Shape.Object : ClassifyAllOfPrimitive(schema);
            }

            var types = schema.Types & ~JsonSchemaTypes.Null;
            if (schema.Const is not null && types == JsonSchemaTypes.None)
            {
                return Shape.Primitive;
            }

            var isObject = (types & JsonSchemaTypes.Object) != 0 ||
                (types == JsonSchemaTypes.None && (schema.Properties.Count > 0 || schema.HasAdditionalProperties || schema.Discriminator is not null));
            if (isObject && (types & ~JsonSchemaTypes.Object) == JsonSchemaTypes.None)
            {
                if (schema.Properties.Count == 0 && schema.Discriminator is null && schema.AdditionalPropertiesAllowed)
                {
                    return Shape.Map;
                }

                return Shape.Object;
            }

            if ((types & JsonSchemaTypes.Integer) != 0 && (types & JsonSchemaTypes.Number) != 0)
            {
                types &= ~JsonSchemaTypes.Integer;
            }

            if (CountBits(types) > 1)
            {
                ReportOnce(schema, DiagnosticDescriptors.MultipleTypes, TypeNames(types));
                return Shape.Any;
            }

            return types switch
            {
                JsonSchemaTypes.Array => Shape.Array,
                JsonSchemaTypes.String or JsonSchemaTypes.Integer or JsonSchemaTypes.Number or JsonSchemaTypes.Boolean => Shape.Primitive,
                _ => schema.Items is not null ? Shape.Array : Shape.Any,
            };
        }

        private Shape ClassifyAllOfPrimitive(OpenApiSchema schema)
        {
            // allOf of non-object members (for example a reference plus extra constraints): the first member decides.
            var first = schema.AllOf[0].Resolved;
            return first == schema ? Shape.Any : Classify(first);
        }

        private bool IsObjectLike(OpenApiSchema schema)
        {
            if (schema.BooleanSchema is not null || schema.HasEnum)
            {
                return false;
            }

            if (schema.Properties.Count > 0 || schema.Discriminator is not null)
            {
                return true;
            }

            var types = schema.Types & ~JsonSchemaTypes.Null;
            if (schema.AllOf.Count > 0)
            {
                return schema.AllOf.Any(m => m.Resolved != schema && IsObjectLike(m.Resolved));
            }

            return types == JsonSchemaTypes.Object && !schema.AdditionalPropertiesAllowed;
        }

        private ApiPrimitiveType MapPrimitive(OpenApiSchema schema)
        {
            var nullable = schema.IsNullable;
            if (schema.Const is { } constant)
            {
                var kind = constant.Kind switch
                {
                    NodeKind.String => ApiPrimitiveKind.String,
                    NodeKind.Boolean => ApiPrimitiveKind.Boolean,
                    NodeKind.Number => constant is ScalarNode n && n.TryGetInt64(out _) ? ApiPrimitiveKind.Integer : ApiPrimitiveKind.Number,
                    _ => ApiPrimitiveKind.Any,
                };
                return new ApiPrimitiveType(kind, schema.Format, nullable, constant);
            }

            var types = schema.Types & ~JsonSchemaTypes.Null;
            var format = schema.Format;
            if ((types & JsonSchemaTypes.Number) != 0)
            {
                if (format is not null && !KnownNumberFormats.Contains(format))
                {
                    ReportOnce(schema, DiagnosticDescriptors.UnknownFormat, format, "number");
                }

                return format is "int32" or "int64"
                    ? new ApiPrimitiveType(ApiPrimitiveKind.Integer, format, nullable)
                    : new ApiPrimitiveType(ApiPrimitiveKind.Number, format, nullable);
            }

            if ((types & JsonSchemaTypes.Integer) != 0)
            {
                if (format is not null && !KnownIntegerFormats.Contains(format))
                {
                    ReportOnce(schema, DiagnosticDescriptors.UnknownFormat, format, "integer");
                }

                return new ApiPrimitiveType(ApiPrimitiveKind.Integer, format, nullable);
            }

            if ((types & JsonSchemaTypes.Boolean) != 0)
            {
                return new ApiPrimitiveType(ApiPrimitiveKind.Boolean, format, nullable);
            }

            if (schema.ContentEncoding is { } encoding)
            {
                if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(encoding, "base64url", StringComparison.OrdinalIgnoreCase))
                {
                    return new ApiPrimitiveType(ApiPrimitiveKind.Base64, encoding.ToLowerInvariant(), nullable);
                }
            }

            if (schema.ContentMediaType is { } media && format is null && !media.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                !media.EndsWith("json", StringComparison.OrdinalIgnoreCase))
            {
                return new ApiPrimitiveType(ApiPrimitiveKind.Binary, null, nullable);
            }

            var primitive = format switch
            {
                "date" => ApiPrimitiveKind.Date,
                "date-time" => ApiPrimitiveKind.DateTime,
                "time" => ApiPrimitiveKind.Time,
                "duration" => ApiPrimitiveKind.Duration,
                "uuid" or "guid" => ApiPrimitiveKind.Uuid,
                "uri" or "uri-reference" or "url" => ApiPrimitiveKind.Uri,
                "byte" => ApiPrimitiveKind.Base64,
                "binary" => ApiPrimitiveKind.Binary,
                _ => ApiPrimitiveKind.String,
            };

            if (format is not null && !KnownStringFormats.Contains(format))
            {
                ReportOnce(schema, DiagnosticDescriptors.UnknownFormat, format, "string");
            }

            return new ApiPrimitiveType(primitive, format, nullable);
        }

        // ------------------------------------------------------------------ objects

        private ApiObjectType BuildObject(OpenApiSchema schema, string[] nameHint, ApiTypeOrigin origin)
        {
            var obj = new ApiObjectType(nameHint, origin, schema)
            {
                Title = schema.Title,
                Description = schema.Description,
                Deprecated = schema.Deprecated,
            };
            Register(schema, obj);

            // The schema's own keywords; for a 3.1 '$ref' with siblings the reference acts as an allOf member.
            var members = new List<OpenApiSchema>(schema.AllOf);
            if (schema.IsReference && schema.ReferenceTarget is { } refTarget)
            {
                members.Insert(0, refTarget);
                obj.Description ??= refTarget.Resolved.Description;
            }

            // Pick a base type: a referenced object member, preferring one with (or inheriting) a discriminator.
            var candidates = members
                .Where(m => (m.IsReference || ComponentName(m.Resolved) is not null) && IsObjectLike(m.Resolved))
                .ToList();
            OpenApiSchema? baseSchema = candidates.Count == 1
                ? candidates[0]
                : candidates.FirstOrDefault(c => HasDiscriminatorInChain(c.Resolved));

            if (baseSchema is not null)
            {
                if (Map(baseSchema, [.. nameHint, "Base"]) is ApiNamedTypeRef { Target: ApiObjectType baseType } && !InheritsFrom(baseType, obj))
                {
                    obj.BaseType = baseType;
                }
                else
                {
                    baseSchema = null;
                }
            }

            // Collect own properties: every member except the base (flattened), then the schema's own properties.
            var inherited = baseSchema is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(CollectProperties(baseSchema.Resolved).Keys, StringComparer.Ordinal);
            var collected = new OrderedMap<OpenApiSchema>();
            var required = new HashSet<string>(StringComparer.Ordinal);
            OpenApiSchema? additional = null;
            var additionalForbidden = !schema.AdditionalPropertiesAllowed;
            var additionalFree = schema.HasAdditionalProperties && schema.AdditionalPropertiesAllowed && schema.AdditionalProperties is null;
            foreach (var member in members)
            {
                if (baseSchema is not null && ReferenceEquals(member, baseSchema))
                {
                    continue;
                }

                var resolved = member.Resolved;
                foreach (var property in CollectProperties(resolved))
                {
                    AddProperty(collected, property.Key, property.Value);
                }

                foreach (var name in CollectRequired(resolved))
                {
                    required.Add(name);
                }

                additional ??= resolved.AdditionalProperties;
                additionalForbidden |= !resolved.AdditionalPropertiesAllowed;
            }

            foreach (var property in schema.Properties)
            {
                AddProperty(collected, property.Key, property.Value);
            }

            foreach (var name in schema.Required)
            {
                required.Add(name);
            }

            additional = schema.AdditionalProperties ?? additional;
            var properties = new List<ApiProperty>();
            foreach (var entry in collected)
            {
                if (inherited.Contains(entry.Key))
                {
                    continue;
                }

                properties.Add(BuildProperty(entry.Key, entry.Value, required.Contains(entry.Key), nameHint));
            }

            obj.Properties = properties;
            if (additional is not null)
            {
                obj.AdditionalProperties = Map(additional, [.. nameHint, "AdditionalProperty"]);
            }
            else if (additionalFree)
            {
                obj.AdditionalProperties = new ApiPrimitiveType(ApiPrimitiveKind.Any);
            }

            obj.AdditionalPropertiesForbidden = additionalForbidden && additional is null;
            if (schema.Discriminator is { PropertyName.Length: > 0 } discriminator)
            {
                obj.Discriminator = new ApiDiscriminator(discriminator.PropertyName);
            }

            return obj;
        }

        private void AddProperty(OrderedMap<OpenApiSchema> collected, string name, OpenApiSchema property)
        {
            if (collected.TryGetValue(name, out var existing))
            {
                if (!ReferenceEquals(existing, property) && !ReferenceEquals(existing.Resolved, property.Resolved) &&
                    !DocNode.DeepEquals(existing.Node, property.Node))
                {
                    ReportOnce(property, DiagnosticDescriptors.ConflictingAllOfProperty, name);
                }

                return;
            }

            collected.Set(name, property);
        }

        private ApiProperty BuildProperty(string name, OpenApiSchema schema, bool required, string[] ownerHint)
        {
            var resolved = schema.Resolved;
            var type = Map(schema, [.. ownerHint, name]);

            // A single-value enum on a property (a typical discriminator declaration) is a constant, not a new type.
            if (!schema.IsReference && resolved.HasEnum && resolved.Enum.Count(v => v.Kind != NodeKind.Null) == 1 &&
                type is ApiNamedTypeRef { Target: ApiEnumType single } && single.Origin == ApiTypeOrigin.Inline)
            {
                var value = resolved.Enum.First(v => v.Kind != NodeKind.Null);
                RemoveType(single);
                type = new ApiPrimitiveType(single.UnderlyingKind, single.Format, type.IsNullable, value);
            }

            var annotationSource = schema.IsReference ? schema : resolved;
            return new ApiProperty(name, type, schema)
            {
                Required = required,
                ReadOnly = schema.ReadOnly || resolved.ReadOnly,
                WriteOnly = schema.WriteOnly || resolved.WriteOnly,
                Deprecated = schema.Deprecated || (!schema.IsReference && resolved.Deprecated),
                Description = schema.IsReference ? schema.Description ?? schema.ReferenceDescription : resolved.Description,
                Default = annotationSource.Default ?? resolved.Default,
                Example = annotationSource.Example ?? resolved.Example ?? (resolved.Examples.Count > 0 ? resolved.Examples[0] : null),
                Constraints = type is ApiNamedTypeRef ? ApiConstraints.None : MergeConstraints(schema, resolved),
            };
        }

        private static ApiConstraints MergeConstraints(OpenApiSchema schema, OpenApiSchema resolved)
        {
            var own = ApiConstraints.From(schema);
            return own.IsEmpty ? ApiConstraints.From(resolved) : own;
        }

        /// <summary>Properties declared by a schema and its allOf members (recursively), in order.</summary>
        private static OrderedMap<OpenApiSchema> CollectProperties(OpenApiSchema schema)
        {
            var result = new OrderedMap<OpenApiSchema>();
            var visited = new HashSet<OpenApiSchema>(IdentityComparer<OpenApiSchema>.Instance);
            Collect(schema);
            return result;

            void Collect(OpenApiSchema s)
            {
                if (!visited.Add(s))
                {
                    return;
                }

                if (s.IsReference && s.ReferenceTarget is { } target)
                {
                    Collect(target);
                }

                foreach (var member in s.AllOf)
                {
                    Collect(member.Resolved);
                }

                foreach (var property in s.Properties)
                {
                    if (!result.ContainsKey(property.Key))
                    {
                        result.Set(property.Key, property.Value);
                    }
                }
            }
        }

        private static IEnumerable<string> CollectRequired(OpenApiSchema schema)
        {
            var visited = new HashSet<OpenApiSchema>(IdentityComparer<OpenApiSchema>.Instance);
            var result = new List<string>();
            Collect(schema);
            return result;

            void Collect(OpenApiSchema s)
            {
                if (!visited.Add(s))
                {
                    return;
                }

                foreach (var member in s.AllOf)
                {
                    Collect(member.Resolved);
                }

                result.AddRange(s.Required);
            }
        }

        private static bool HasDiscriminatorInChain(OpenApiSchema schema)
        {
            var visited = new HashSet<OpenApiSchema>(IdentityComparer<OpenApiSchema>.Instance);
            var stack = new Stack<OpenApiSchema>();
            stack.Push(schema);
            while (stack.Count > 0)
            {
                var s = stack.Pop().Resolved;
                if (!visited.Add(s))
                {
                    continue;
                }

                if (s.Discriminator is not null)
                {
                    return true;
                }

                foreach (var member in s.AllOf)
                {
                    stack.Push(member);
                }
            }

            return false;
        }

        private static bool InheritsFrom(ApiObjectType candidateBase, ApiObjectType derived)
        {
            for (var current = candidateBase; current is not null; current = current.BaseType)
            {
                if (ReferenceEquals(current, derived))
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------ enums

        private ApiNamedType? BuildEnum(OpenApiSchema schema, string[] nameHint, ApiTypeOrigin origin)
        {
            var values = new List<DocNode>();
            foreach (var value in schema.Enum)
            {
                if (value.Kind == NodeKind.Null)
                {
                    continue;
                }

                if (values.Any(v => DocNode.DeepEquals(v, value)))
                {
                    ReportOnce(schema, DiagnosticDescriptors.InvalidEnumValue, value.ToJson(), "appears more than once; the duplicate is ignored");
                    continue;
                }

                values.Add(value);
            }

            var kinds = values.Select(v => v is ScalarNode s && s.Kind == NodeKind.Number ? (s.TryGetInt64(out _) ? "integer" : "number") : v.Kind.ToString()).Distinct().ToList();
            ApiPrimitiveKind underlying;
            if (values.Count == 0)
            {
                ReportOnce(schema, DiagnosticDescriptors.InvalidEnumValue, "(none)", "- the enum has no non-null values; it is typed as untyped JSON");
                return null;
            }

            if (kinds.Count == 1 && kinds[0] == "String")
            {
                underlying = ApiPrimitiveKind.String;
            }
            else if (kinds.Count == 1 && kinds[0] == "integer")
            {
                underlying = ApiPrimitiveKind.Integer;
            }
            else
            {
                ReportOnce(schema, DiagnosticDescriptors.InvalidEnumValue, string.Join(", ", values.Select(v => v.ToJson())),
                    "- only all-string or all-integer enums have a typed representation; the value is typed as untyped JSON");
                return null;
            }

            var names = ReadStringArrayExtension(schema, "x-enum-varnames") ?? ReadStringArrayExtension(schema, "x-enumNames");
            var descriptions = ReadStringArrayExtension(schema, "x-enum-descriptions") ?? ReadStringArrayExtension(schema, "x-enumDescriptions");
            var enumType = new ApiEnumType(nameHint, origin, schema, underlying)
            {
                Title = schema.Title,
                Description = schema.Description,
                Deprecated = schema.Deprecated,
                Format = schema.Format,
            };
            Register(schema, enumType);
            var members = new List<ApiEnumMember>();
            var index = 0;
            foreach (var value in schema.Enum)
            {
                if (value.Kind != NodeKind.Null && values.Contains(value))
                {
                    members.Add(new ApiEnumMember(value, Element(names, index), Element(descriptions, index)));
                    values.Remove(value);
                }

                index++;
            }

            enumType.Members = members;
            return enumType;

            static string? Element(IReadOnlyList<string>? list, int i) => list is not null && i < list.Count ? list[i] : null;
        }

        private static IReadOnlyList<string>? ReadStringArrayExtension(OpenApiSchema schema, string name) =>
            schema.TryGetExtension(name, out var node) && node is ArrayNode array
                ? array.Items.Select(i => i is ScalarNode s ? s.Text : i.ToJson()).ToArray()
                : null;

        // ------------------------------------------------------------------ unions

        private ApiNamedType BuildUnion(OpenApiSchema schema, string[] nameHint, ApiTypeOrigin origin)
        {
            var kind = schema.OneOf.Count > 0 ? ApiUnionKind.OneOf : ApiUnionKind.AnyOf;
            var union = new ApiUnionType(nameHint, origin, schema, kind)
            {
                Title = schema.Title,
                Description = schema.Description,
                Deprecated = schema.Deprecated,
                DiscriminatorPropertyName = schema.Discriminator?.PropertyName is { Length: > 0 } p ? p : null,
            };
            Register(schema, union);

            var members = (kind == ApiUnionKind.OneOf ? schema.OneOf : schema.AnyOf).Where(m => !IsNullOnly(m)).ToList();
            var variants = new List<ApiUnionVariant>();
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                string[] variantHint = ComponentName(member.Resolved) is { } component
                    ? [.. nameHint, component]
                    : [.. nameHint, "Variant" + (i + 1).ToString(CultureInfo.InvariantCulture)];
                variants.Add(new ApiUnionVariant(Map(member, variantHint), null));
            }

            union.Variants = variants;
            if (union.DiscriminatorPropertyName is { } property)
            {
                AssignUnionDiscriminatorValues(union, schema, members, property);
            }

            union.Dispatch = DetermineDispatch(union);
            if (union.Dispatch == ApiUnionDispatch.Ambiguous)
            {
                ReportOnce(schema, DiagnosticDescriptors.AmbiguousUnion,
                    $"The variants of '{union.Name}' cannot be told apart at runtime (add a discriminator or use variants of different JSON types); the value is exposed as raw JSON with typed accessors");
            }

            return union;
        }

        private void AssignUnionDiscriminatorValues(ApiUnionType union, OpenApiSchema schema, List<OpenApiSchema> members, string property)
        {
            var mapping = schema.Discriminator?.Mapping ?? OrderedMap<string>.Empty;
            for (var i = 0; i < members.Count; i++)
            {
                var resolved = members[i].Resolved;
                string? value = null;
                foreach (var entry in mapping)
                {
                    if (ResolveMappingTarget(entry.Value, schema) is { } target && ReferenceEquals(target, resolved))
                    {
                        value = entry.Key;
                        break;
                    }
                }

                value ??= ConstOf(CollectProperties(resolved), property);
                value ??= ComponentName(resolved);
                union.Variants[i].DiscriminatorValue = value;
            }

            foreach (var entry in mapping)
            {
                if (ResolveMappingTarget(entry.Value, schema) is not { } target || !members.Any(m => ReferenceEquals(m.Resolved, target)))
                {
                    ReportOnce(schema, DiagnosticDescriptors.InvalidDiscriminatorMapping, entry.Key, entry.Value, "does not resolve to one of the variants");
                }
            }
        }

        private static string? ConstOf(OrderedMap<OpenApiSchema> properties, string property)
        {
            if (!properties.TryGetValue(property, out var schema))
            {
                return null;
            }

            var resolved = schema.Resolved;
            if (resolved.Const is ScalarNode constant)
            {
                return constant.Text;
            }

            return resolved.HasEnum && resolved.Enum.Count == 1 && resolved.Enum[0] is ScalarNode single ? single.Text : null;
        }

        private static ApiUnionDispatch DetermineDispatch(ApiUnionType union)
        {
            if (union.DiscriminatorPropertyName is not null &&
                union.Variants.All(v => v.DiscriminatorValue is not null && v.Type is ApiNamedTypeRef { Target: ApiObjectType }) &&
                union.Variants.Select(v => v.DiscriminatorValue).Distinct(StringComparer.Ordinal).Count() == union.Variants.Count)
            {
                return ApiUnionDispatch.Discriminator;
            }

            var kinds = union.Variants.Select(v => JsonKindOf(v.Type)).ToList();
            return kinds.All(k => k is not null) && kinds.Distinct().Count() == kinds.Count
                ? ApiUnionDispatch.ValueKind
                : ApiUnionDispatch.Ambiguous;
        }

        /// <summary>The JSON value kind a type always serializes to, or null when it can vary.</summary>
        internal static string? JsonKindOf(ApiTypeRef type) => type switch
        {
            ApiPrimitiveType { Kind: ApiPrimitiveKind.Integer or ApiPrimitiveKind.Number } => "number",
            ApiPrimitiveType { Kind: ApiPrimitiveKind.Boolean } => "boolean",
            ApiPrimitiveType { Kind: ApiPrimitiveKind.Any or ApiPrimitiveKind.Binary } => null,
            ApiPrimitiveType => "string",
            ApiArrayType => "array",
            ApiMapType => "object",
            ApiNamedTypeRef { Target: ApiObjectType } => "object",
            ApiNamedTypeRef { Target: ApiEnumType e } => e.UnderlyingKind == ApiPrimitiveKind.String ? "string" : "number",
            _ => null,
        };

        // ------------------------------------------------------------------ hierarchies

        private void CompleteHierarchies()
        {
            var objects = _types.OfType<ApiObjectType>().ToList();
            foreach (var obj in objects)
            {
                obj.DerivedTypes = objects.Where(o => ReferenceEquals(o.BaseType, obj)).ToArray();
            }

            foreach (var root in objects.Where(o => o.Discriminator is not null && o.BaseType is null))
            {
                var discriminator = root.Discriminator!;
                var descendants = Descendants(root).ToList();
                var mappings = new List<ApiDiscriminatorMapping>();
                var mappingSource = root.Schema.Discriminator?.Mapping ?? OrderedMap<string>.Empty;
                foreach (var entry in mappingSource)
                {
                    var target = ResolveMappingTarget(entry.Value, root.Schema);
                    if (target is null || !_named.TryGetValue(target, out var type) || type is not ApiObjectType targetObject)
                    {
                        ReportOnce(root.Schema, DiagnosticDescriptors.InvalidDiscriminatorMapping, entry.Key, entry.Value, "does not resolve to an object schema");
                        continue;
                    }

                    if (!ReferenceEquals(targetObject, root) && !descendants.Contains(targetObject))
                    {
                        ReportOnce(root.Schema, DiagnosticDescriptors.InvalidDiscriminatorMapping, entry.Key, entry.Value, $"does not extend '{root.Name}' through allOf");
                        continue;
                    }

                    mappings.Add(new ApiDiscriminatorMapping(entry.Key, targetObject));
                    targetObject.DiscriminatorValue ??= entry.Key;
                }

                foreach (var descendant in descendants)
                {
                    if (descendant.DiscriminatorValue is null)
                    {
                        var value = ConstOf(CollectProperties(descendant.Schema), discriminator.PropertyName) ??
                            (descendant.Origin == ApiTypeOrigin.Component ? descendant.NameHint[descendant.NameHint.Count - 1] : null);
                        if (value is null)
                        {
                            continue;
                        }

                        descendant.DiscriminatorValue = value;
                        mappings.Add(new ApiDiscriminatorMapping(value, descendant));
                    }
                }

                discriminator.Mappings = mappings;
                foreach (var property in root.Properties.Where(p => p.JsonName == discriminator.PropertyName))
                {
                    property.IsDiscriminator = true;
                }
            }
        }

        private static IEnumerable<ApiObjectType> Descendants(ApiObjectType root)
        {
            foreach (var derived in root.DerivedTypes)
            {
                yield return derived;
                foreach (var nested in Descendants(derived))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>Resolves a discriminator mapping value: a reference (<c>#/components/schemas/Dog</c>) or a schema name.</summary>
        private OpenApiSchema? ResolveMappingTarget(string value, OpenApiSchema context)
        {
            if (value.IndexOf('#') < 0 && value.IndexOf('/') < 0)
            {
                return _document.Components.Schemas.TryGetValue(value, out var named) ? named.Resolved : null;
            }

            var hash = value.IndexOf('#');
            if (hash > 0)
            {
                // A reference into another document: match by pointer suffix among the types already built.
                var pointer = DocumentPrefix(context) == value.Substring(0, hash) ? value.Substring(hash) : value;
                return _named.Keys.FirstOrDefault(s => s.Pointer == pointer || s.Pointer == value);
            }

            var fragment = value.Substring(hash + 1);
            var segments = fragment.Split('/');
            if (segments.Length == 4 && segments[1] == "components" && segments[2] == "schemas")
            {
                var name = JsonPointer.Unescape(segments[3]);
                return _document.Components.Schemas.TryGetValue(name, out var schema) ? schema.Resolved : null;
            }

            return _named.Keys.FirstOrDefault(s => s.Pointer == DocumentPrefix(context) + "#" + fragment);
        }

        private static string DocumentPrefix(OpenApiSchema schema)
        {
            var hash = schema.Pointer.IndexOf('#');
            return hash > 0 ? schema.Pointer.Substring(0, hash) : string.Empty;
        }

        // ------------------------------------------------------------------ helpers

        private void Register(OpenApiSchema schema, ApiNamedType type)
        {
            _named[schema] = type;
            _types.Add(type);
        }

        private void RemoveType(ApiNamedType type)
        {
            _types.Remove(type);
            _named.Remove(type.Schema);
            _mapped.Remove(type.Schema);
        }

        /// <summary>
        /// The component-like name of a schema: the last pointer segment under <c>schemas</c>, <c>$defs</c> or
        /// <c>definitions</c>, or the file name of a whole-document schema; null for inline schemas.
        /// </summary>
        internal static string? ComponentName(OpenApiSchema schema)
        {
            var pointer = schema.Pointer;
            var hash = pointer.IndexOf('#');
            var fragment = hash >= 0 ? pointer.Substring(hash + 1) : pointer;
            var segments = fragment.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[segments.Length - 2] is "schemas" or "$defs" or "definitions")
            {
                return JsonPointer.Unescape(segments[segments.Length - 1]);
            }

            if (segments.Length == 0 && hash > 0)
            {
                var document = pointer.Substring(0, hash);
                var slash = Math.Max(document.LastIndexOf('/'), document.LastIndexOf('\\'));
                var file = document.Substring(slash + 1);
                var dot = file.IndexOf('.');
                return dot > 0 ? file.Substring(0, dot) : file;
            }

            return null;
        }

        private void ReportUnsupportedKeywords(OpenApiSchema schema)
        {
            foreach (var keyword in schema.UnsupportedKeywords)
            {
                var (severity, message) = keyword switch
                {
                    "not" or "contains" or "propertyNames" or "unevaluatedItems" =>
                        (DiagnosticSeverity.Info, "it only restricts valid values, so the generated type accepts a superset"),
                    "patternProperties" =>
                        (DiagnosticSeverity.Warning, "properties matching the patterns are not typed (they are kept as additional properties when allowed)"),
                    "if" or "then" or "else" or "dependentSchemas" =>
                        (DiagnosticSeverity.Warning, "conditional subschemas are ignored, so properties they define are not generated"),
                    "prefixItems" or "items (array form)" =>
                        (DiagnosticSeverity.Warning, "tuple items are not typed; the array items are untyped JSON"),
                    _ => (DiagnosticSeverity.Warning, "the schema is generated without it"),
                };
                _diagnostics.Add(DiagnosticDescriptors.UnsupportedSchemaKeyword.Create(severity, schema.Location, schema.Pointer, keyword, message));
            }
        }

        private void ReportOnce(OpenApiSchema schema, DiagnosticDescriptor descriptor, params object?[] args)
        {
            if (_reported.Add(schema))
            {
                _diagnostics.Report(descriptor, schema.Location, schema.Pointer, args);
            }
        }

        private static bool SameName(OpenApiParameter a, OpenApiParameter b) =>
            a.In == ParameterLocation.Header
                ? string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(a.Name, b.Name, StringComparison.Ordinal);

        private static int IndexOf(IReadOnlyList<string> list, string value)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] == value)
                {
                    return i;
                }
            }

            return int.MaxValue;
        }

        private static int CountBits(JsonSchemaTypes types)
        {
            var count = 0;
            for (var value = (int)types; value != 0; value &= value - 1)
            {
                count++;
            }

            return count;
        }

        private static string TypeNames(JsonSchemaTypes types) =>
            string.Join(", ", Enum.GetValues(typeof(JsonSchemaTypes)).Cast<JsonSchemaTypes>()
                .Where(t => t != JsonSchemaTypes.None && (types & t) != 0)
                .Select(t => t.ToString().ToLowerInvariant()));
    }
}

/// <summary>Reference-equality comparer.</summary>
internal sealed class IdentityComparer<T> : IEqualityComparer<T>
    where T : class
{
    public static readonly IdentityComparer<T> Instance = new();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}

internal static class OpenApiSchemaExtensions
{
    /// <summary>Whether a schema has structure beyond its <c>type</c> (used to recognize the <c>{type: null}</c> schema).</summary>
    public static bool HasStructuralKeywordsExceptType(this OpenApiSchema schema) =>
        schema.HasEnum || schema.Const is not null || schema.Properties.Count > 0 || schema.Items is not null ||
        schema.AllOf.Count > 0 || schema.OneOf.Count > 0 || schema.AnyOf.Count > 0 || schema.AdditionalProperties is not null;
}
