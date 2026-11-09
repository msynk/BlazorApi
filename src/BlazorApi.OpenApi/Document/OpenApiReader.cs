using System.Globalization;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Document;

/// <summary>
/// Reads a parsed node tree into the typed OpenAPI model, reporting structural problems as diagnostics. References
/// are recorded, not followed; <c>OpenApiReferenceResolver</c> resolves them afterwards. Each node is read at most once
/// per element type, so a schema reached through several paths (or YAML aliases) is a single model instance.
/// </summary>
internal sealed class OpenApiReader
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.Ordinal)
    {
        "get", "put", "post", "delete", "options", "head", "patch", "trace", "query",
    };

    private readonly DiagnosticBag _diagnostics;
    private readonly string _pointerPrefix;
    private readonly Dictionary<(DocNode Node, Type Type), OpenApiElement> _registry = [];

    public OpenApiReader(DiagnosticBag diagnostics, OpenApiSpecVersion version, string? documentName = null)
    {
        _diagnostics = diagnostics;
        Version = version;
        _pointerPrefix = documentName ?? string.Empty;
    }

    public OpenApiSpecVersion Version { get; private set; }

    // ------------------------------------------------------------------ entry points

    /// <summary>Detects the specification line of a root node, reporting unsupported or missing versions.</summary>
    public static OpenApiSpecVersion? DetectVersion(DocNode root, DiagnosticBag diagnostics, out string versionText)
    {
        versionText = string.Empty;
        if (root is not ObjectNode obj)
        {
            diagnostics.Report(DiagnosticDescriptors.UnexpectedNodeType, root.Location, "#", "the document", "an object", root.KindDescription);
            return null;
        }

        var openapi = obj.GetProperty("openapi");
        if (openapi is null)
        {
            var swagger = obj.GetProperty("swagger");
            if (swagger is not null)
            {
                versionText = (swagger.Value as ScalarNode)?.Text ?? string.Empty;
                diagnostics.Report(DiagnosticDescriptors.UnsupportedVersion, swagger.Value.Location, "#/swagger", "swagger " + versionText,
                    "; convert Swagger 2.0 documents to OpenAPI 3 first (for example with the swagger2openapi tool)");
                return null;
            }

            diagnostics.Report(DiagnosticDescriptors.MissingVersion, obj.Location, "#", string.Empty);
            return null;
        }

        versionText = (openapi.Value as ScalarNode)?.Text ?? string.Empty;
        if (versionText.StartsWith("3.0.", StringComparison.Ordinal) || versionText == "3.0")
        {
            return OpenApiSpecVersion.OpenApi3_0;
        }

        if (versionText.StartsWith("3.1.", StringComparison.Ordinal) || versionText == "3.1")
        {
            return OpenApiSpecVersion.OpenApi3_1;
        }

        if (versionText.StartsWith("3.2.", StringComparison.Ordinal) || versionText == "3.2")
        {
            diagnostics.Add(DiagnosticDescriptors.UnsupportedVersion.Create(
                DiagnosticSeverity.Warning, openapi.Value.Location, "#/openapi", versionText,
                "; the document is processed as OpenAPI 3.1 and 3.2-only features are ignored"));
            return OpenApiSpecVersion.OpenApi3_1;
        }

        diagnostics.Report(DiagnosticDescriptors.UnsupportedVersion, openapi.Value.Location, "#/openapi", versionText, string.Empty);
        return null;
    }

    public OpenApiDocument ReadDocument(ObjectNode root, string versionText)
    {
        var doc = Init(new OpenApiDocument(), root, "#");
        doc.OpenApiVersion = versionText;
        doc.SpecVersion = Version;
        var extensions = new OrderedMap<DocNode>();
        var hasInfo = false;
        var hasPaths = false;
        foreach (var p in root.Properties)
        {
            var path = Child("#", p.Name);
            switch (p.Name)
            {
                case "openapi":
                    break;
                case "info":
                    hasInfo = true;
                    doc.Info = ReadInfo(p.Value, path);
                    break;
                case "jsonSchemaDialect":
                    doc.JsonSchemaDialect = ReadString(p.Value, path);
                    break;
                case "servers":
                    doc.Servers = ReadList(p.Value, path, ReadServer);
                    break;
                case "paths":
                    hasPaths = true;
                    doc.Paths = ReadPaths(p.Value, path);
                    break;
                case "webhooks":
                    doc.Webhooks = ReadMap(p.Value, path, (n, ptr, key) => ReadPathItem(n, ptr, key));
                    break;
                case "components":
                    doc.Components = ReadComponents(p.Value, path);
                    break;
                case "security":
                    doc.Security = ReadList(p.Value, path, ReadSecurityRequirement);
                    break;
                case "tags":
                    doc.Tags = ReadList(p.Value, path, ReadTag);
                    break;
                case "externalDocs":
                    doc.ExternalDocs = ReadExternalDocs(p.Value, path);
                    break;
                default:
                    Unknown(p, path, "the OpenAPI Object", extensions);
                    break;
            }
        }

        doc.Extensions = extensions;
        if (!hasInfo)
        {
            Report(DiagnosticDescriptors.RequiredFieldMissing, root.Location, "#", "The OpenAPI Object", "info");
        }

        if (!hasPaths && Version == OpenApiSpecVersion.OpenApi3_0)
        {
            Report(DiagnosticDescriptors.RequiredFieldMissing, root.Location, "#", "An OpenAPI 3.0 document", "paths");
        }

        return doc;
    }

    /// <summary>Reads the node at a reference target as the requested element type.</summary>
    public T? ReadReferenceTarget<T>(DocNode node, string pointer)
        where T : OpenApiElement
    {
        object? result = typeof(T) == typeof(OpenApiSchema) ? ReadSchema(node, pointer)
            : typeof(T) == typeof(OpenApiParameter) ? ReadParameter(node, pointer)
            : typeof(T) == typeof(OpenApiResponse) ? ReadResponse(node, pointer)
            : typeof(T) == typeof(OpenApiRequestBody) ? ReadRequestBody(node, pointer)
            : typeof(T) == typeof(OpenApiHeader) ? ReadHeader(node, pointer)
            : typeof(T) == typeof(OpenApiExample) ? ReadExample(node, pointer)
            : typeof(T) == typeof(OpenApiSecurityScheme) ? ReadSecurityScheme(node, pointer)
            : typeof(T) == typeof(OpenApiPathItem) ? ReadPathItem(node, pointer, LastSegment(pointer))
            : throw new NotSupportedException(typeof(T).Name);
        return (T?)result;
    }

    // ------------------------------------------------------------------ info, servers, tags

    private OpenApiInfo ReadInfo(DocNode node, string pointer)
    {
        var info = new OpenApiInfo();
        if (Expect(node, pointer, "info") is not ObjectNode obj)
        {
            return info;
        }

        Init(info, obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "title": info.Title = ReadString(p.Value, path) ?? string.Empty; break;
                case "version": info.Version = ReadString(p.Value, path) ?? string.Empty; break;
                case "summary": info.Summary = ReadString(p.Value, path); break;
                case "description": info.Description = ReadString(p.Value, path); break;
                case "termsOfService": info.TermsOfService = ReadString(p.Value, path); break;
                case "contact": info.Contact = ReadContact(p.Value, path); break;
                case "license": info.License = ReadLicense(p.Value, path); break;
                default: Unknown(p, path, "the Info Object", extensions); break;
            }
        }

        info.Extensions = extensions;
        RequireField(obj, pointer, "The Info Object", "title");
        RequireField(obj, pointer, "The Info Object", "version");
        return info;
    }

    private OpenApiContact? ReadContact(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "contact") is not ObjectNode obj)
        {
            return null;
        }

        var contact = Init(new OpenApiContact(), obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "name": contact.Name = ReadString(p.Value, path); break;
                case "url": contact.Url = ReadString(p.Value, path); break;
                case "email": contact.Email = ReadString(p.Value, path); break;
                default: Unknown(p, path, "the Contact Object", extensions); break;
            }
        }

        contact.Extensions = extensions;
        return contact;
    }

    private OpenApiLicense? ReadLicense(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "license") is not ObjectNode obj)
        {
            return null;
        }

        var license = Init(new OpenApiLicense(), obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "name": license.Name = ReadString(p.Value, path) ?? string.Empty; break;
                case "identifier": license.Identifier = ReadString(p.Value, path); break;
                case "url": license.Url = ReadString(p.Value, path); break;
                default: Unknown(p, path, "the License Object", extensions); break;
            }
        }

        license.Extensions = extensions;
        RequireField(obj, pointer, "The License Object", "name");
        return license;
    }

    private OpenApiServer? ReadServer(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "a server") is not ObjectNode obj)
        {
            return null;
        }

        var server = Init(new OpenApiServer(), obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "url": server.Url = ReadString(p.Value, path) ?? string.Empty; break;
                case "description": server.Description = ReadString(p.Value, path); break;
                case "variables": server.Variables = ReadMap(p.Value, path, (n, ptr, _) => ReadServerVariable(n, ptr)); break;
                default: Unknown(p, path, "the Server Object", extensions); break;
            }
        }

        server.Extensions = extensions;
        RequireField(obj, pointer, "The Server Object", "url");
        return server;
    }

    private OpenApiServerVariable? ReadServerVariable(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "a server variable") is not ObjectNode obj)
        {
            return null;
        }

        var variable = Init(new OpenApiServerVariable(), obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "default": variable.Default = ReadString(p.Value, path) ?? string.Empty; break;
                case "enum": variable.Enum = ReadStringList(p.Value, path); break;
                case "description": variable.Description = ReadString(p.Value, path); break;
                default: Unknown(p, path, "the Server Variable Object", extensions); break;
            }
        }

        variable.Extensions = extensions;
        RequireField(obj, pointer, "The Server Variable Object", "default");
        return variable;
    }

    private OpenApiTag? ReadTag(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "a tag") is not ObjectNode obj)
        {
            return null;
        }

        var tag = Init(new OpenApiTag(), obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "name": tag.Name = ReadString(p.Value, path) ?? string.Empty; break;
                case "description": tag.Description = ReadString(p.Value, path); break;
                case "externalDocs": tag.ExternalDocs = ReadExternalDocs(p.Value, path); break;
                case "summary":
                case "parent":
                case "kind":
                    break; // OpenAPI 3.2 tag fields: accepted, not used.
                default: Unknown(p, path, "the Tag Object", extensions); break;
            }
        }

        tag.Extensions = extensions;
        RequireField(obj, pointer, "The Tag Object", "name");
        return tag;
    }

    private OpenApiExternalDocs? ReadExternalDocs(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "externalDocs") is not ObjectNode obj)
        {
            return null;
        }

        var docs = Init(new OpenApiExternalDocs(), obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "url": docs.Url = ReadString(p.Value, path) ?? string.Empty; break;
                case "description": docs.Description = ReadString(p.Value, path); break;
                default: Unknown(p, path, "the External Documentation Object", extensions); break;
            }
        }

        docs.Extensions = extensions;
        RequireField(obj, pointer, "The External Documentation Object", "url");
        return docs;
    }

    // ------------------------------------------------------------------ components

    private OpenApiComponents ReadComponents(DocNode node, string pointer)
    {
        var components = new OpenApiComponents();
        if (Expect(node, pointer, "components") is not ObjectNode obj)
        {
            return components;
        }

        Init(components, obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "schemas": components.Schemas = ReadMap(p.Value, path, (n, ptr, _) => ReadSchema(n, ptr)); break;
                case "responses": components.Responses = ReadMap(p.Value, path, (n, ptr, _) => ReadResponse(n, ptr)); break;
                case "parameters": components.Parameters = ReadMap(p.Value, path, (n, ptr, _) => ReadParameter(n, ptr)); break;
                case "examples": components.Examples = ReadMap(p.Value, path, (n, ptr, _) => ReadExample(n, ptr)); break;
                case "requestBodies": components.RequestBodies = ReadMap(p.Value, path, (n, ptr, _) => ReadRequestBody(n, ptr)); break;
                case "headers": components.Headers = ReadMap(p.Value, path, (n, ptr, _) => ReadHeader(n, ptr)); break;
                case "securitySchemes": components.SecuritySchemes = ReadMap(p.Value, path, (n, ptr, _) => ReadSecurityScheme(n, ptr)); break;
                case "pathItems": components.PathItems = ReadMap(p.Value, path, (n, ptr, key) => ReadPathItem(n, ptr, key)); break;
                case "links": components.Links = KeysOf(p.Value, path); break;
                case "callbacks": components.Callbacks = KeysOf(p.Value, path); break;
                case "mediaTypes": break; // OpenAPI 3.2: accepted, not used.
                default: Unknown(p, path, "the Components Object", extensions); break;
            }
        }

        components.Extensions = extensions;
        return components;
    }

    // ------------------------------------------------------------------ paths and operations

    private OrderedMap<OpenApiPathItem> ReadPaths(DocNode node, string pointer)
    {
        var map = new OrderedMap<OpenApiPathItem>();
        if (Expect(node, pointer, "paths") is not ObjectNode obj)
        {
            return map;
        }

        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            if (p.Name.StartsWith("x-", StringComparison.Ordinal))
            {
                continue;
            }

            if (!p.Name.StartsWith("/", StringComparison.Ordinal))
            {
                Report(DiagnosticDescriptors.InvalidPathTemplate, p.KeyLocation, path, p.Name, "paths must start with '/'");
            }

            if (ReadPathItem(p.Value, path, p.Name) is { } item)
            {
                map.Set(p.Name, item);
            }
        }

        return map;
    }

    private OpenApiPathItem? ReadPathItem(DocNode node, string pointer, string pathKey)
    {
        if (Expect(node, pointer, "a path item") is not ObjectNode obj)
        {
            return null;
        }

        if (TryGetRegistered<OpenApiPathItem>(obj, out var existing))
        {
            return existing;
        }

        var item = Register(Init(new OpenApiPathItem { Path = pathKey }, obj, pointer), obj);
        var operations = new OrderedMap<OpenApiOperation>();
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            if (HttpMethods.Contains(p.Name))
            {
                if (p.Name == "query" && Version == OpenApiSpecVersion.OpenApi3_0)
                {
                    Unknown(p, path, "the Path Item Object", extensions);
                    continue;
                }

                if (ReadOperation(p.Value, path, p.Name, item) is { } operation)
                {
                    operations.Set(p.Name, operation);
                }

                continue;
            }

            switch (p.Name)
            {
                case "$ref": item.Reference = ReadString(p.Value, path); break;
                case "summary": item.Summary = ReadString(p.Value, path); break;
                case "description": item.Description = ReadString(p.Value, path); break;
                case "servers": item.Servers = ReadList(p.Value, path, ReadServer); break;
                case "parameters": item.Parameters = ReadList(p.Value, path, ReadParameter); break;
                case "additionalOperations":
                    Report(DiagnosticDescriptors.FeatureIgnored, p.KeyLocation, path, "'additionalOperations' (OpenAPI 3.2)");
                    break;
                default: Unknown(p, path, "the Path Item Object", extensions); break;
            }
        }

        item.Operations = operations;
        item.Extensions = extensions;
        return item;
    }

    private OpenApiOperation? ReadOperation(DocNode node, string pointer, string method, OpenApiPathItem pathItem)
    {
        if (Expect(node, pointer, "an operation") is not ObjectNode obj)
        {
            return null;
        }

        var operation = Init(new OpenApiOperation { Method = method, Path = pathItem.Path, PathItem = pathItem }, obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        var hasResponses = false;
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "operationId": operation.OperationId = ReadString(p.Value, path); break;
                case "summary": operation.Summary = ReadString(p.Value, path); break;
                case "description": operation.Description = ReadString(p.Value, path); break;
                case "tags": operation.Tags = ReadStringList(p.Value, path); break;
                case "parameters": operation.Parameters = ReadList(p.Value, path, ReadParameter); break;
                case "requestBody": operation.RequestBody = ReadRequestBody(p.Value, path); break;
                case "responses":
                    hasResponses = true;
                    operation.Responses = ReadResponses(p.Value, path);
                    break;
                case "deprecated": operation.Deprecated = ReadBool(p.Value, path); break;
                case "security": operation.Security = ReadList(p.Value, path, ReadSecurityRequirement); break;
                case "servers": operation.Servers = ReadList(p.Value, path, ReadServer); break;
                case "externalDocs": operation.ExternalDocs = ReadExternalDocs(p.Value, path); break;
                case "callbacks": operation.Callbacks = KeysOf(p.Value, path); break;
                default: Unknown(p, path, "the Operation Object", extensions); break;
            }
        }

        operation.Extensions = extensions;
        if (!hasResponses && Version == OpenApiSpecVersion.OpenApi3_0)
        {
            Report(DiagnosticDescriptors.RequiredFieldMissing, obj.Location, pointer, "An OpenAPI 3.0 Operation Object", "responses");
        }

        return operation;
    }

    private OrderedMap<OpenApiResponse> ReadResponses(DocNode node, string pointer)
    {
        var map = new OrderedMap<OpenApiResponse>();
        if (Expect(node, pointer, "responses") is not ObjectNode obj)
        {
            return map;
        }

        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            if (p.Name.StartsWith("x-", StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsValidStatusKey(p.Name))
            {
                Report(DiagnosticDescriptors.InvalidValue, p.KeyLocation, path, p.Name, "response key",
                    "expected 'default', a status code (200) or a range (2XX)");
                continue;
            }

            if (ReadResponse(p.Value, path) is { } response)
            {
                map.Set(p.Name.ToUpperInvariant() == "DEFAULT" ? "default" : p.Name.ToUpperInvariant(), response);
            }
        }

        return map;
    }

    private static bool IsValidStatusKey(string key)
    {
        if (string.Equals(key, "default", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (key.Length != 3 || key[0] < '1' || key[0] > '5')
        {
            return false;
        }

        return (char.IsDigit(key[1]) && char.IsDigit(key[2])) || (key[1] is 'X' or 'x' && key[2] is 'X' or 'x');
    }

    private OpenApiResponse? ReadResponse(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "a response") is not ObjectNode obj)
        {
            return null;
        }

        if (TryGetRegistered<OpenApiResponse>(obj, out var existing))
        {
            return existing;
        }

        var response = Register(Init(new OpenApiResponse(), obj, pointer), obj);
        if (ReadReference(response, obj, pointer))
        {
            return response;
        }

        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "description": response.Description = ReadString(p.Value, path) ?? string.Empty; break;
                case "headers": response.Headers = ReadMap(p.Value, path, (n, ptr, _) => ReadHeader(n, ptr)); break;
                case "content": response.Content = ReadContent(p.Value, path); break;
                case "links": response.Links = KeysOf(p.Value, path); break;
                case "summary": break; // OpenAPI 3.2
                default: Unknown(p, path, "the Response Object", extensions); break;
            }
        }

        response.Extensions = extensions;
        if (!obj.ContainsKey("description") && Version == OpenApiSpecVersion.OpenApi3_0)
        {
            Report(DiagnosticDescriptors.RequiredFieldMissing, obj.Location, pointer, "The Response Object", "description");
        }

        return response;
    }

    private OpenApiRequestBody? ReadRequestBody(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "a request body") is not ObjectNode obj)
        {
            return null;
        }

        if (TryGetRegistered<OpenApiRequestBody>(obj, out var existing))
        {
            return existing;
        }

        var body = Register(Init(new OpenApiRequestBody(), obj, pointer), obj);
        if (ReadReference(body, obj, pointer))
        {
            return body;
        }

        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "description": body.Description = ReadString(p.Value, path); break;
                case "content": body.Content = ReadContent(p.Value, path); break;
                case "required": body.Required = ReadBool(p.Value, path); break;
                default: Unknown(p, path, "the Request Body Object", extensions); break;
            }
        }

        body.Extensions = extensions;
        RequireField(obj, pointer, "The Request Body Object", "content");
        return body;
    }

    private OrderedMap<OpenApiMediaType> ReadContent(DocNode node, string pointer) =>
        ReadMap(node, pointer, ReadMediaType);

    private OpenApiMediaType? ReadMediaType(DocNode node, string pointer, string mediaType)
    {
        if (Expect(node, pointer, "a media type") is not ObjectNode obj)
        {
            return null;
        }

        var media = Init(new OpenApiMediaType { MediaType = mediaType }, obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "schema": media.Schema = ReadSchema(p.Value, path); break;
                case "example": media.Example = p.Value; break;
                case "examples": media.Examples = ReadMap(p.Value, path, (n, ptr, _) => ReadExample(n, ptr)); break;
                case "encoding": media.Encoding = ReadMap(p.Value, path, (n, ptr, _) => ReadEncoding(n, ptr)); break;
                case "itemSchema":
                case "itemEncoding":
                case "prefixEncoding":
                    Report(DiagnosticDescriptors.FeatureIgnored, p.KeyLocation, path, $"'{p.Name}' (OpenAPI 3.2 sequential media types)");
                    break;
                default: Unknown(p, path, "the Media Type Object", extensions); break;
            }
        }

        media.Extensions = extensions;
        return media;
    }

    private OpenApiEncoding? ReadEncoding(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "an encoding") is not ObjectNode obj)
        {
            return null;
        }

        var encoding = Init(new OpenApiEncoding(), obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "contentType": encoding.ContentType = ReadString(p.Value, path); break;
                case "headers": encoding.Headers = ReadMap(p.Value, path, (n, ptr, _) => ReadHeader(n, ptr)); break;
                case "style": encoding.Style = ReadString(p.Value, path); break;
                case "explode": encoding.Explode = ReadBool(p.Value, path); break;
                case "allowReserved": encoding.AllowReserved = ReadBool(p.Value, path); break;
                default: Unknown(p, path, "the Encoding Object", extensions); break;
            }
        }

        encoding.Extensions = extensions;
        return encoding;
    }

    private OpenApiParameter? ReadParameter(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "a parameter") is not ObjectNode obj)
        {
            return null;
        }

        if (TryGetRegistered<OpenApiParameter>(obj, out var existing))
        {
            return existing;
        }

        var parameter = Register(Init(new OpenApiParameter(), obj, pointer), obj);
        if (ReadReference(parameter, obj, pointer))
        {
            return parameter;
        }

        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "name": parameter.Name = ReadString(p.Value, path) ?? string.Empty; break;
                case "in":
                    var location = ReadString(p.Value, path);
                    parameter.In = location switch
                    {
                        "query" => ParameterLocation.Query,
                        "header" => ParameterLocation.Header,
                        "path" => ParameterLocation.Path,
                        "cookie" => ParameterLocation.Cookie,
                        _ => null,
                    };
                    if (parameter.In is null && location is not null)
                    {
                        Report(DiagnosticDescriptors.InvalidValue, p.Value.Location, path, location, "in", "expected query, header, path or cookie");
                    }

                    break;
                case "allowEmptyValue": parameter.AllowEmptyValue = ReadBool(p.Value, path); break;
                case "allowReserved": parameter.AllowReserved = ReadBool(p.Value, path); break;
                default:
                    if (!ReadParameterCommon(parameter, p, path))
                    {
                        Unknown(p, path, "the Parameter Object", extensions);
                    }

                    break;
            }
        }

        parameter.Extensions = extensions;
        RequireField(obj, pointer, "The Parameter Object", "name");
        RequireField(obj, pointer, "The Parameter Object", "in");
        return parameter;
    }

    private OpenApiHeader? ReadHeader(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "a header") is not ObjectNode obj)
        {
            return null;
        }

        if (TryGetRegistered<OpenApiHeader>(obj, out var existing))
        {
            return existing;
        }

        var header = Register(Init(new OpenApiHeader(), obj, pointer), obj);
        if (ReadReference(header, obj, pointer))
        {
            return header;
        }

        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            if (!ReadParameterCommon(header, p, path))
            {
                Unknown(p, path, "the Header Object", extensions);
            }
        }

        header.Extensions = extensions;
        return header;
    }

    private bool ReadParameterCommon<T>(OpenApiParameterBase<T> parameter, PropertyNode p, string path)
        where T : OpenApiParameterBase<T>
    {
        switch (p.Name)
        {
            case "description": parameter.Description = ReadString(p.Value, path); return true;
            case "required": parameter.Required = ReadBool(p.Value, path); return true;
            case "deprecated": parameter.Deprecated = ReadBool(p.Value, path); return true;
            case "style": parameter.Style = ReadString(p.Value, path); return true;
            case "explode": parameter.Explode = ReadBool(p.Value, path); return true;
            case "schema": parameter.Schema = ReadSchema(p.Value, path); return true;
            case "content": parameter.Content = ReadContent(p.Value, path); return true;
            case "example": parameter.Example = p.Value; return true;
            case "examples": parameter.Examples = ReadMap(p.Value, path, (n, ptr, _) => ReadExample(n, ptr)); return true;
            default: return false;
        }
    }

    private OpenApiExample? ReadExample(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "an example") is not ObjectNode obj)
        {
            return null;
        }

        if (TryGetRegistered<OpenApiExample>(obj, out var existing))
        {
            return existing;
        }

        var example = Register(Init(new OpenApiExample(), obj, pointer), obj);
        if (ReadReference(example, obj, pointer))
        {
            return example;
        }

        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "summary": example.Summary = ReadString(p.Value, path); break;
                case "description": example.Description = ReadString(p.Value, path); break;
                case "value": example.Value = p.Value; break;
                case "externalValue": example.ExternalValue = ReadString(p.Value, path); break;
                case "dataValue":
                case "serializedValue":
                    break; // OpenAPI 3.2
                default: Unknown(p, path, "the Example Object", extensions); break;
            }
        }

        example.Extensions = extensions;
        return example;
    }

    // ------------------------------------------------------------------ security

    private OpenApiSecurityScheme? ReadSecurityScheme(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "a security scheme") is not ObjectNode obj)
        {
            return null;
        }

        if (TryGetRegistered<OpenApiSecurityScheme>(obj, out var existing))
        {
            return existing;
        }

        var scheme = Register(Init(new OpenApiSecurityScheme(), obj, pointer), obj);
        if (ReadReference(scheme, obj, pointer))
        {
            return scheme;
        }

        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "type":
                    var type = ReadString(p.Value, path);
                    scheme.Type = type switch
                    {
                        "apiKey" => SecuritySchemeType.ApiKey,
                        "http" => SecuritySchemeType.Http,
                        "oauth2" => SecuritySchemeType.OAuth2,
                        "openIdConnect" => SecuritySchemeType.OpenIdConnect,
                        "mutualTLS" => SecuritySchemeType.MutualTls,
                        _ => null,
                    };
                    if (scheme.Type is null && type is not null)
                    {
                        Report(DiagnosticDescriptors.InvalidValue, p.Value.Location, path, type, "type", "expected apiKey, http, oauth2, openIdConnect or mutualTLS");
                    }

                    break;
                case "description": scheme.Description = ReadString(p.Value, path); break;
                case "name": scheme.Name = ReadString(p.Value, path); break;
                case "in":
                    var location = ReadString(p.Value, path);
                    scheme.In = location switch
                    {
                        "query" => ParameterLocation.Query,
                        "header" => ParameterLocation.Header,
                        "cookie" => ParameterLocation.Cookie,
                        _ => null,
                    };
                    if (scheme.In is null && location is not null)
                    {
                        Report(DiagnosticDescriptors.InvalidValue, p.Value.Location, path, location, "in", "expected query, header or cookie");
                    }

                    break;
                case "scheme": scheme.Scheme = ReadString(p.Value, path); break;
                case "bearerFormat": scheme.BearerFormat = ReadString(p.Value, path); break;
                case "flows": scheme.Flows = ReadOAuthFlows(p.Value, path); break;
                case "openIdConnectUrl": scheme.OpenIdConnectUrl = ReadString(p.Value, path); break;
                case "oauth2MetadataUrl":
                case "deprecated":
                    break; // OpenAPI 3.2
                default: Unknown(p, path, "the Security Scheme Object", extensions); break;
            }
        }

        scheme.Extensions = extensions;
        RequireField(obj, pointer, "The Security Scheme Object", "type");
        return scheme;
    }

    private OpenApiOAuthFlows? ReadOAuthFlows(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "OAuth flows") is not ObjectNode obj)
        {
            return null;
        }

        var flows = Init(new OpenApiOAuthFlows(), obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "implicit": flows.Implicit = ReadOAuthFlow(p.Value, path); break;
                case "password": flows.Password = ReadOAuthFlow(p.Value, path); break;
                case "clientCredentials": flows.ClientCredentials = ReadOAuthFlow(p.Value, path); break;
                case "authorizationCode": flows.AuthorizationCode = ReadOAuthFlow(p.Value, path); break;
                case "deviceAuthorization": break; // OpenAPI 3.2
                default: Unknown(p, path, "the OAuth Flows Object", extensions); break;
            }
        }

        flows.Extensions = extensions;
        return flows;
    }

    private OpenApiOAuthFlow? ReadOAuthFlow(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "an OAuth flow") is not ObjectNode obj)
        {
            return null;
        }

        var flow = Init(new OpenApiOAuthFlow(), obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "authorizationUrl": flow.AuthorizationUrl = ReadString(p.Value, path); break;
                case "tokenUrl": flow.TokenUrl = ReadString(p.Value, path); break;
                case "refreshUrl": flow.RefreshUrl = ReadString(p.Value, path); break;
                case "scopes": flow.Scopes = ReadMap(p.Value, path, (n, ptr, _) => ReadString(n, ptr) ?? string.Empty); break;
                default: Unknown(p, path, "the OAuth Flow Object", extensions); break;
            }
        }

        flow.Extensions = extensions;
        return flow;
    }

    private OpenApiSecurityRequirement? ReadSecurityRequirement(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "a security requirement") is not ObjectNode obj)
        {
            return null;
        }

        var requirement = Init(new OpenApiSecurityRequirement(), obj, pointer);
        requirement.Schemes = ReadMap<IReadOnlyList<string>>(node, pointer, (n, ptr, _) => ReadStringList(n, ptr));
        return requirement;
    }

    // ------------------------------------------------------------------ schemas

    public OpenApiSchema ReadSchema(DocNode node, string pointer)
    {
        if (node is ScalarNode scalar && scalar.TryGetBoolean(out var booleanSchema))
        {
            if (TryGetRegistered<OpenApiSchema>(node, out var existingBoolean))
            {
                return existingBoolean;
            }

            if (Version == OpenApiSpecVersion.OpenApi3_0)
            {
                Report(DiagnosticDescriptors.UnexpectedNodeType, node.Location, pointer, pointer, "a schema object (boolean schemas require OpenAPI 3.1)", node.KindDescription);
            }

            return Register(Init(new OpenApiSchema { BooleanSchema = booleanSchema }, node, pointer), node);
        }

        if (node is not ObjectNode obj)
        {
            Report(DiagnosticDescriptors.UnexpectedNodeType, node.Location, pointer, LastSegment(pointer), "a schema", node.KindDescription);
            return Init(new OpenApiSchema { BooleanSchema = true }, node, pointer);
        }

        if (TryGetRegistered<OpenApiSchema>(obj, out var existing))
        {
            return existing;
        }

        var schema = Register(Init(new OpenApiSchema(), obj, pointer), obj);
        var extensions = new OrderedMap<DocNode>();
        var unsupported = new List<string>();
        bool? exclusiveMinimumFlag = null;
        bool? exclusiveMaximumFlag = null;
        var nullable = false;
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "$ref":
                    schema.Reference = ReadString(p.Value, path);
                    break;
                case "type":
                    schema.Types |= ReadSchemaTypes(p.Value, path);
                    break;
                case "nullable":
                    schema.HasNullableKeyword = true;
                    nullable = ReadBool(p.Value, path);
                    if (Version == OpenApiSpecVersion.OpenApi3_1)
                    {
                        _diagnostics.Add(DiagnosticDescriptors.InvalidValue.Create(DiagnosticSeverity.Warning, p.KeyLocation, path,
                            "nullable", "keyword", "'nullable' is not part of OpenAPI 3.1; it is honored, but use type: [<type>, 'null'] instead"));
                    }

                    break;
                case "format": schema.Format = ReadString(p.Value, path); break;
                case "title": schema.Title = ReadString(p.Value, path); break;
                case "description": schema.Description = ReadString(p.Value, path); break;
                case "default": schema.Default = p.Value; break;
                case "example": schema.Example = p.Value; break;
                case "examples":
                    if (p.Value is ArrayNode examples)
                    {
                        schema.Examples = examples.Items;
                    }

                    break;
                case "enum":
                    schema.HasEnum = true;
                    if (p.Value is ArrayNode values)
                    {
                        schema.Enum = values.Items;
                    }
                    else
                    {
                        TypeError(p.Value, path, "an array");
                    }

                    break;
                case "const": schema.Const = p.Value; break;
                case "properties":
                    schema.Properties = ReadMap(p.Value, path, (n, ptr, _) => ReadSchema(n, ptr));
                    break;
                case "required":
                    schema.Required = ReadStringList(p.Value, path);
                    break;
                case "additionalProperties":
                    schema.HasAdditionalProperties = true;
                    if (p.Value is ScalarNode ap && ap.TryGetBoolean(out var allowed))
                    {
                        schema.AdditionalPropertiesAllowed = allowed;
                    }
                    else
                    {
                        schema.AdditionalProperties = ReadSchema(p.Value, path);
                    }

                    break;
                case "items":
                    if (p.Value is ArrayNode)
                    {
                        unsupported.Add("items (array form)");
                    }
                    else
                    {
                        schema.Items = ReadSchema(p.Value, path);
                    }

                    break;
                case "allOf": schema.AllOf = ReadSchemaList(p.Value, path); break;
                case "oneOf": schema.OneOf = ReadSchemaList(p.Value, path); break;
                case "anyOf": schema.AnyOf = ReadSchemaList(p.Value, path); break;
                case "discriminator": schema.Discriminator = ReadDiscriminator(p.Value, path); break;
                case "readOnly": schema.ReadOnly = ReadBool(p.Value, path); break;
                case "writeOnly": schema.WriteOnly = ReadBool(p.Value, path); break;
                case "deprecated": schema.Deprecated = ReadBool(p.Value, path); break;
                case "minimum": schema.Minimum = ReadDecimal(p.Value, path); break;
                case "maximum": schema.Maximum = ReadDecimal(p.Value, path); break;
                case "exclusiveMinimum":
                    if (p.Value is ScalarNode { Kind: NodeKind.Boolean } exMin)
                    {
                        exMin.TryGetBoolean(out var flag);
                        exclusiveMinimumFlag = flag;
                    }
                    else
                    {
                        schema.ExclusiveMinimum = ReadDecimal(p.Value, path);
                    }

                    break;
                case "exclusiveMaximum":
                    if (p.Value is ScalarNode { Kind: NodeKind.Boolean } exMax)
                    {
                        exMax.TryGetBoolean(out var flag);
                        exclusiveMaximumFlag = flag;
                    }
                    else
                    {
                        schema.ExclusiveMaximum = ReadDecimal(p.Value, path);
                    }

                    break;
                case "multipleOf": schema.MultipleOf = ReadDecimal(p.Value, path); break;
                case "minLength": schema.MinLength = ReadCount(p.Value, path); break;
                case "maxLength": schema.MaxLength = ReadCount(p.Value, path); break;
                case "pattern": schema.Pattern = ReadString(p.Value, path); break;
                case "minItems": schema.MinItems = ReadCount(p.Value, path); break;
                case "maxItems": schema.MaxItems = ReadCount(p.Value, path); break;
                case "uniqueItems": schema.UniqueItems = ReadBool(p.Value, path); break;
                case "minProperties": schema.MinProperties = ReadCount(p.Value, path); break;
                case "maxProperties": schema.MaxProperties = ReadCount(p.Value, path); break;
                case "contentEncoding": schema.ContentEncoding = ReadString(p.Value, path); break;
                case "contentMediaType": schema.ContentMediaType = ReadString(p.Value, path); break;
                case "$defs":
                case "definitions":
                    schema.Definitions = ReadMap(p.Value, path, (n, ptr, _) => ReadSchema(n, ptr));
                    break;

                // Shape-changing keywords without a typed representation.
                case "not":
                case "if":
                case "then":
                case "else":
                case "patternProperties":
                case "prefixItems":
                case "dependentSchemas":
                case "$dynamicRef":
                case "unevaluatedItems":
                case "contains":
                case "propertyNames":
                    unsupported.Add(p.Name);
                    break;
                case "unevaluatedProperties":
                    if (p.Value is ScalarNode up && up.TryGetBoolean(out var unevaluatedAllowed))
                    {
                        // 'false' only forbids extra properties, like additionalProperties: false.
                        if (!unevaluatedAllowed && !schema.HasAdditionalProperties)
                        {
                            schema.AdditionalPropertiesAllowed = false;
                        }
                    }
                    else
                    {
                        unsupported.Add(p.Name);
                    }

                    break;

                // Annotations and validation-only keywords that do not affect the generated types.
                case "$schema":
                case "$id":
                case "$anchor":
                case "$dynamicAnchor":
                case "$comment":
                case "$vocabulary":
                case "xml":
                case "externalDocs":
                case "minContains":
                case "maxContains":
                case "dependentRequired":
                case "contentSchema":
                    break;
                default:
                    if (p.Name.StartsWith("x-", StringComparison.Ordinal))
                    {
                        extensions.Set(p.Name, p.Value);
                    }

                    // JSON Schema permits unknown keywords; they are ignored without a diagnostic.
                    break;
            }
        }

        if (nullable)
        {
            schema.Types |= JsonSchemaTypes.Null;
        }

        if (exclusiveMinimumFlag == true)
        {
            schema.ExclusiveMinimum = schema.Minimum;
            schema.Minimum = null;
        }

        if (exclusiveMaximumFlag == true)
        {
            schema.ExclusiveMaximum = schema.Maximum;
            schema.Maximum = null;
        }

        schema.Extensions = extensions;
        schema.UnsupportedKeywords = unsupported;
        return schema;
    }

    private JsonSchemaTypes ReadSchemaTypes(DocNode node, string pointer)
    {
        if (node is ScalarNode { IsString: true } scalar)
        {
            return ParseSchemaType(scalar, pointer);
        }

        if (node is ArrayNode array)
        {
            if (Version == OpenApiSpecVersion.OpenApi3_0)
            {
                _diagnostics.Add(DiagnosticDescriptors.UnexpectedNodeType.Create(DiagnosticSeverity.Warning, node.Location, pointer,
                    "type", "a string in OpenAPI 3.0 (type arrays require 3.1); the array is honored", "an array"));
            }

            var types = JsonSchemaTypes.None;
            foreach (var item in array.Items)
            {
                if (item is ScalarNode { IsString: true } s)
                {
                    types |= ParseSchemaType(s, pointer);
                }
                else
                {
                    TypeError(item, pointer, "a string");
                }
            }

            return types;
        }

        TypeError(node, pointer, "a string or an array of strings");
        return JsonSchemaTypes.None;
    }

    private JsonSchemaTypes ParseSchemaType(ScalarNode scalar, string pointer)
    {
        switch (scalar.Value)
        {
            case "null":
                if (Version == OpenApiSpecVersion.OpenApi3_0)
                {
                    _diagnostics.Add(DiagnosticDescriptors.InvalidValue.Create(DiagnosticSeverity.Warning, scalar.Location, pointer,
                        "null", "type", "the 'null' type requires OpenAPI 3.1 (use 'nullable: true' in 3.0); it is honored"));
                }

                return JsonSchemaTypes.Null;
            case "boolean": return JsonSchemaTypes.Boolean;
            case "integer": return JsonSchemaTypes.Integer;
            case "number": return JsonSchemaTypes.Number;
            case "string": return JsonSchemaTypes.String;
            case "array": return JsonSchemaTypes.Array;
            case "object": return JsonSchemaTypes.Object;
            default:
                Report(DiagnosticDescriptors.InvalidValue, scalar.Location, pointer, scalar.Value, "type",
                    "expected null, boolean, integer, number, string, array or object");
                return JsonSchemaTypes.None;
        }
    }

    private IReadOnlyList<OpenApiSchema> ReadSchemaList(DocNode node, string pointer)
    {
        if (node is not ArrayNode array)
        {
            TypeError(node, pointer, "an array of schemas");
            return [];
        }

        var list = new List<OpenApiSchema>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            list.Add(ReadSchema(array[i], pointer + "/" + i.ToString(CultureInfo.InvariantCulture)));
        }

        return list;
    }

    private OpenApiDiscriminator? ReadDiscriminator(DocNode node, string pointer)
    {
        if (Expect(node, pointer, "a discriminator") is not ObjectNode obj)
        {
            return null;
        }

        var discriminator = Init(new OpenApiDiscriminator(), obj, pointer);
        var extensions = new OrderedMap<DocNode>();
        foreach (var p in obj.Properties)
        {
            var path = Child(pointer, p.Name);
            switch (p.Name)
            {
                case "propertyName": discriminator.PropertyName = ReadString(p.Value, path) ?? string.Empty; break;
                case "mapping": discriminator.Mapping = ReadMap<string>(p.Value, path, (n, ptr, _) => ReadString(n, ptr)); break;
                case "defaultMapping": break; // OpenAPI 3.2
                default: Unknown(p, path, "the Discriminator Object", extensions); break;
            }
        }

        discriminator.Extensions = extensions;
        RequireField(obj, pointer, "The Discriminator Object", "propertyName");
        return discriminator;
    }

    // ------------------------------------------------------------------ references

    /// <summary>
    /// Reads a Reference Object into <paramref name="element"/> when the node has <c>$ref</c>. Returns true when it
    /// did (the remaining fields are then ignored, except the 3.1 summary/description overrides).
    /// </summary>
    private bool ReadReference<T>(OpenApiReferenceable<T> element, ObjectNode obj, string pointer)
        where T : OpenApiReferenceable<T>
    {
        var reference = obj.GetProperty("$ref");
        if (reference is null)
        {
            return false;
        }

        element.Reference = ReadString(reference.Value, Child(pointer, "$ref")) ?? string.Empty;
        foreach (var p in obj.Properties)
        {
            switch (p.Name)
            {
                case "summary": element.ReferenceSummary = ReadString(p.Value, Child(pointer, p.Name)); break;
                case "description": element.ReferenceDescription = ReadString(p.Value, Child(pointer, p.Name)); break;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ primitive helpers

    private T Init<T>(T element, DocNode node, string pointer)
        where T : OpenApiElement
    {
        element.Location = node.Location;
        element.Pointer = _pointerPrefix + pointer;
        element.Node = node;
        return element;
    }

    private bool TryGetRegistered<T>(DocNode node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? element)
        where T : OpenApiElement
    {
        if (_registry.TryGetValue((node, typeof(T)), out var found))
        {
            element = (T)found;
            return true;
        }

        element = null;
        return false;
    }

    private T Register<T>(T element, DocNode node)
        where T : OpenApiElement
    {
        _registry[(node, typeof(T))] = element;
        return element;
    }

    private DocNode? Expect(DocNode node, string pointer, string what)
    {
        if (node is ObjectNode)
        {
            return node;
        }

        Report(DiagnosticDescriptors.UnexpectedNodeType, node.Location, pointer, LastSegment(pointer) is { Length: > 0 } s ? s : what, "an object", node.KindDescription);
        return null;
    }

    private string? ReadString(DocNode node, string pointer)
    {
        if (node is ScalarNode scalar)
        {
            return scalar.IsNull ? null : scalar.Text;
        }

        TypeError(node, pointer, "a string");
        return null;
    }

    private bool ReadBool(DocNode node, string pointer)
    {
        if (node is ScalarNode scalar && scalar.TryGetBoolean(out var value))
        {
            return value;
        }

        TypeError(node, pointer, "a boolean");
        return false;
    }

    private decimal? ReadDecimal(DocNode node, string pointer)
    {
        if (node is ScalarNode scalar && scalar.TryGetDecimal(out var value))
        {
            return value;
        }

        TypeError(node, pointer, "a number");
        return null;
    }

    private int? ReadCount(DocNode node, string pointer)
    {
        if (node is ScalarNode scalar && scalar.TryGetInt64(out var value) && value >= 0)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        TypeError(node, pointer, "a non-negative integer");
        return null;
    }

    private IReadOnlyList<string> ReadStringList(DocNode node, string pointer)
    {
        if (node is not ArrayNode array)
        {
            TypeError(node, pointer, "an array of strings");
            return [];
        }

        var list = new List<string>(array.Count);
        foreach (var item in array.Items)
        {
            if (ReadString(item, pointer) is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    private IReadOnlyList<T> ReadList<T>(DocNode node, string pointer, Func<DocNode, string, T?> read)
        where T : class
    {
        if (node is not ArrayNode array)
        {
            TypeError(node, pointer, "an array");
            return [];
        }

        var list = new List<T>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (read(array[i], pointer + "/" + i.ToString(CultureInfo.InvariantCulture)) is { } item)
            {
                list.Add(item);
            }
        }

        return list;
    }

    private OrderedMap<T> ReadMap<T>(DocNode node, string pointer, Func<DocNode, string, string, T?> read)
    {
        var map = new OrderedMap<T>();
        if (node is not ObjectNode obj)
        {
            if (node is not ScalarNode { IsNull: true })
            {
                TypeError(node, pointer, "an object");
            }

            return map;
        }

        foreach (var p in obj.Properties)
        {
            if (read(p.Value, Child(pointer, p.Name), p.Name) is { } value)
            {
                map.Set(p.Name, value);
            }
        }

        return map;
    }

    private IReadOnlyList<string> KeysOf(DocNode node, string pointer)
    {
        if (node is ObjectNode obj)
        {
            var keys = obj.Properties.Where(p => !p.Name.StartsWith("x-", StringComparison.Ordinal)).Select(p => p.Name).ToArray();
            foreach (var key in keys)
            {
                Report(DiagnosticDescriptors.FeatureIgnored, obj.GetProperty(key)!.KeyLocation, Child(pointer, key), $"'{LastSegment(pointer)}' entry '{key}'");
            }

            return keys;
        }

        TypeError(node, pointer, "an object");
        return [];
    }

    private void Unknown(PropertyNode property, string pointer, string owner, OrderedMap<DocNode> extensions)
    {
        if (property.Name.StartsWith("x-", StringComparison.Ordinal))
        {
            extensions.Set(property.Name, property.Value);
            return;
        }

        Report(DiagnosticDescriptors.UnknownField, property.KeyLocation, pointer, property.Name, owner);
    }

    private void RequireField(ObjectNode obj, string pointer, string owner, string field)
    {
        if (!obj.ContainsKey(field))
        {
            Report(DiagnosticDescriptors.RequiredFieldMissing, obj.Location, pointer, owner, field);
        }
    }

    private void TypeError(DocNode node, string pointer, string expected) =>
        Report(DiagnosticDescriptors.UnexpectedNodeType, node.Location, pointer, LastSegment(pointer), expected, node.KindDescription);

    private void Report(DiagnosticDescriptor descriptor, SourceLocation location, string pointer, params object?[] args) =>
        _diagnostics.Report(descriptor, location, _pointerPrefix + pointer, args);

    internal static string Child(string pointer, string key) =>
        pointer + "/" + key.Replace("~", "~0").Replace("/", "~1");

    private static string LastSegment(string pointer)
    {
        var slash = pointer.LastIndexOf('/');
        var segment = slash >= 0 ? pointer.Substring(slash + 1) : pointer;
        return segment.Replace("~1", "/").Replace("~0", "~");
    }
}
