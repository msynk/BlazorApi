using BlazorApi.CodeGeneration.Naming;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.CodeGeneration.CSharp;

/// <summary>Emits client interfaces, implementations and dependency injection registrations.</summary>
internal sealed class ClientEmitter
{
    private const string Http = "global::BlazorApi.Runtime.Http";
    private const string Runtime = "global::BlazorApi.Runtime";
    private const string Tasks = "global::System.Threading.Tasks";
    private const string CancellationToken = "global::System.Threading.CancellationToken";
    private const string JsonOptions = "_connection.JsonOptions";

    private readonly GenerationPlan _plan;
    private readonly Dictionary<ClientPlan, List<Method>> _methods = [];

    public ClientEmitter(GenerationPlan plan) => _plan = plan;

    /// <summary>The method shapes of a client (computed once, shared with the pagination and query emitters).</summary>
    public List<Method> MethodsFor(ClientPlan client)
    {
        if (!_methods.TryGetValue(client, out var methods))
        {
            methods = client.Operations.Select(o => BuildMethod(client, o)).ToList();
            _methods[client] = methods;
        }

        return methods;
    }

    public IEnumerable<GeneratedFile> Emit()
    {
        foreach (var client in _plan.Clients)
        {
            var methods = MethodsFor(client);
            var writer = FileWriter.Start(_plan, _plan.Namespace);
            if (_plan.Options.GenerateInterfaces)
            {
                EmitInterface(writer, client, methods);
                writer.Line();
            }

            EmitClass(writer, client, methods);
            yield return new GeneratedFile(client.ClassName + ".g.cs", writer.ToString());

            if (_plan.Options.GenerateDependencyInjection)
            {
                yield return new GeneratedFile(client.ClassName + "ServiceCollectionExtensions.g.cs", EmitRegistration(client));
            }
        }
    }

    // ------------------------------------------------------------------ method model

    internal sealed class Parameter
    {
        public Parameter(string name, string type, ApiTypeRef apiType, bool optional, string? description)
        {
            Name = name;
            Type = type;
            ApiType = apiType;
            Optional = optional;
            Description = description;
        }

        public string Name { get; }

        public string Type { get; }

        public ApiTypeRef ApiType { get; }

        public bool Optional { get; }

        public string? Description { get; }

        public ApiParameter? Source { get; init; }

        public ApiBodyPart? Part { get; init; }

        public bool IsBody { get; init; }

        public bool IsReferenceRequired { get; init; }
    }

    internal sealed class Method
    {
        public Method(OperationPlan operation) => Operation = operation;

        public OperationPlan Operation { get; }

        public List<Parameter> Parameters { get; } = [];

        public ApiContent? Request { get; set; }

        public bool RequestAsParts { get; set; }

        public string ResultType { get; set; } = "global::BlazorApi.Runtime.Http.ApiNoContent";

        public string? ValueType { get; set; }

        public bool IsFile { get; set; }

        public List<string> HandlerLines { get; } = [];

        public List<string> Accept { get; } = [];

        public string HandlerField { get; set; } = string.Empty;

        public string SecurityField { get; set; } = string.Empty;

        public string SecurityInitializer { get; set; } = string.Empty;

        public string? ReturnsDescription { get; set; }

        public string Signature => string.Join(", ", Parameters.Select(p => p.Optional ? $"{p.Type} {p.Name} = null" : $"{p.Type} {p.Name}")
            .Concat([$"{CancellationToken} cancellationToken = default"]));

        /// <summary>The parameter list without the cancellation token.</summary>
        public string ParameterList(Func<Parameter, bool>? include = null) =>
            string.Join(", ", Parameters.Where(p => include?.Invoke(p) ?? true).Select(p => p.Optional ? $"{p.Type} {p.Name} = null" : $"{p.Type} {p.Name}"));

        public string Arguments => string.Join(", ", Parameters.Select(p => p.Name).Concat(["cancellationToken"]));
    }

    private Method BuildMethod(ClientPlan client, OperationPlan plan)
    {
        var operation = plan.Operation;
        var method = new Method(plan);
        var scope = new NameScope(reserved: ["cancellationToken", "builder", "result", "item", "pair", "v_", "typed"]);
        var required = new List<Parameter>();
        var optional = new List<Parameter>();

        foreach (var parameter in operation.Parameters.Where(p => p.Location == ParameterLocation.Path))
        {
            required.Add(CreateParameter(scope, parameter));
        }

        method.Request = GenerationPlanner.RequestContent(operation);
        if (method.Request is { } request)
        {
            if (operation.RequestBody!.Contents.Count > 1)
            {
                _plan.Diagnostics.Report(CodeGenerationDiagnostics.RequestMediaTypesIgnored, operation.Location, operation.Source.Pointer,
                    operation.ToString(), request.MediaType);
            }

            var bodyRequired = operation.RequestBody.Required;
            if (request.Type is ApiNamedTypeRef { Target: var target } && _plan.ParameterBodies.Contains(target))
            {
                method.RequestAsParts = true;
                foreach (var part in request.Parts)
                {
                    var partRequired = bodyRequired && part.Required;
                    var parameter = new Parameter(
                        scope.Allocate(Identifier.Camel(part.Name)),
                        _plan.CSharpType(part.Type, TypeUse.Parameter, nullable: !partRequired || part.Type.IsNullable),
                        part.Type,
                        optional: !partRequired,
                        part.Description)
                    {
                        Part = part,
                        IsReferenceRequired = partRequired && !_plan.IsValueType(part.Type) && !part.Type.IsNullable,
                    };
                    (partRequired ? required : optional).Add(parameter);
                }
            }
            else
            {
                var apiType = request.Type ?? new ApiPrimitiveType(request.Kind == ApiBodyKind.Binary ? ApiPrimitiveKind.Binary : ApiPrimitiveKind.Any);
                if (request.Kind is ApiBodyKind.Text or ApiBodyKind.Other)
                {
                    apiType = new ApiPrimitiveType(ApiPrimitiveKind.String);
                }
                else if (request.Kind == ApiBodyKind.Binary)
                {
                    apiType = new ApiPrimitiveType(ApiPrimitiveKind.Binary);
                }

                var parameter = new Parameter(
                    scope.Allocate("body"),
                    _plan.CSharpType(apiType, TypeUse.Model, nullable: !bodyRequired || apiType.IsNullable),
                    apiType,
                    optional: !bodyRequired,
                    operation.RequestBody.Description ?? "The request body.")
                {
                    IsBody = true,
                    IsReferenceRequired = bodyRequired && !_plan.IsValueType(apiType) && !apiType.IsNullable,
                };
                (bodyRequired ? required : optional).Add(parameter);
            }
        }

        foreach (var parameter in operation.Parameters.Where(p => p.Location != ParameterLocation.Path))
        {
            var created = CreateParameter(scope, parameter);
            (created.Optional ? optional : required).Add(created);
        }

        method.Parameters.AddRange(required);
        method.Parameters.AddRange(optional);
        BuildResponses(method, operation);
        method.HandlerField = client.Members.Allocate(plan.Name + "Responses");
        method.SecurityField = client.Members.Allocate(plan.Name + "Security");
        method.SecurityInitializer = SecurityInitializer(operation);
        return method;
    }

    private Parameter CreateParameter(NameScope scope, ApiParameter parameter)
    {
        var optional = !parameter.Required;
        return new Parameter(
            scope.Allocate(Identifier.Camel(parameter.Name)),
            _plan.CSharpType(parameter.Type, TypeUse.Parameter, nullable: optional || parameter.Type.IsNullable),
            parameter.Type,
            optional,
            parameter.Description)
        {
            Source = parameter,
            IsReferenceRequired = !optional && !_plan.IsValueType(parameter.Type) && !parameter.Type.IsNullable,
        };
    }

    private void BuildResponses(Method method, ApiOperation operation)
    {
        static int Rank(ApiContent c) => c.Kind switch
        {
            ApiBodyKind.Json => 0,
            ApiBodyKind.Text => 1,
            ApiBodyKind.Binary => 2,
            _ => 3,
        };

        var successes = operation.Responses.Where(r => r.IsSuccess).ToList();
        var primary = successes.FirstOrDefault(r => r.Contents.Count > 0);
        var primaryContent = primary?.Contents.OrderBy(Rank).First();
        string? reader = null;
        if (primaryContent is not null)
        {
            switch (primaryContent.Kind)
            {
                case ApiBodyKind.Binary:
                    method.ValueType = $"{Runtime}.FileResponse";
                    method.IsFile = true;
                    reader = $"{Http}.ApiBody.ReadFile";
                    break;
                case ApiBodyKind.Text:
                case ApiBodyKind.Other:
                    method.ValueType = "string";
                    reader = $"{Http}.ApiBody.ReadText";
                    break;
                default:
                    var type = primaryContent.Type ?? new ApiPrimitiveType(ApiPrimitiveKind.Any);
                    method.ValueType = _plan.CSharpType(type);
                    reader = $"{Http}.ApiBody.ReadJson<{method.ValueType}>";
                    break;
            }

            method.ResultType = method.ValueType;
            method.ReturnsDescription = primary!.Description;
        }

        foreach (var response in operation.Responses)
        {
            var status = Literal.String(response.StatusCode);
            if (response.IsSuccess)
            {
                var content = response.Contents.OrderBy(Rank).FirstOrDefault();
                if (content is null)
                {
                    method.HandlerLines.Add($".Success({status})");
                }
                else if (ReferenceEquals(response, primary) || SameBody(content, primaryContent))
                {
                    method.HandlerLines.Add(method.IsFile ? $".Success({status}, {reader}, ownsResponse: true)" : $".Success({status}, {reader})");
                }
                else
                {
                    method.HandlerLines.Add($".Success({status})");
                    _plan.Diagnostics.Report(CodeGenerationDiagnostics.MultipleSuccessTypes, operation.Location, operation.Source.Pointer,
                        operation.ToString(), primary!.StatusCode, primaryContent!.MediaType);
                }
            }
            else
            {
                var content = response.Contents.OrderBy(Rank).FirstOrDefault();
                string? errorReader = content switch
                {
                    { Kind: ApiBodyKind.Json, Type: { } type } => $"{Http}.ApiBody.JsonError<{_plan.CSharpType(type, nullable: false)}>",
                    { Kind: ApiBodyKind.Json } c when c.MediaType.StartsWith("application/problem+json", StringComparison.OrdinalIgnoreCase) => $"{Http}.ApiBody.ProblemError",
                    { Kind: ApiBodyKind.Text or ApiBodyKind.Other } => $"{Http}.ApiBody.TextError",
                    _ => null,
                };
                method.HandlerLines.Add(errorReader is null ? $".Error({status})" : $".Error({status}, {errorReader})");
            }

            foreach (var content in response.Contents)
            {
                var mediaType = content.MediaType;
                if (!mediaType.Contains('*') && !method.Accept.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
                {
                    method.Accept.Add(mediaType);
                }
            }
        }
    }

    private bool SameBody(ApiContent content, ApiContent? primary) =>
        primary is not null && content.Kind == primary.Kind &&
        (content.Kind != ApiBodyKind.Json || (content.Type is not null && primary.Type is not null && _plan.CSharpType(content.Type) == _plan.CSharpType(primary.Type)));

    private static string SecurityInitializer(ApiOperation operation)
    {
        const string requirement = "global::BlazorApi.Runtime.Http.ApiSecurityRequirement";
        if (operation.Security.Count == 0)
        {
            return $"global::System.Array.Empty<{requirement}>()";
        }

        var items = operation.Security.Select(r =>
        {
            if (r.Items.All(i => i.Scopes.Count == 0))
            {
                return $"new {requirement}({string.Join(", ", r.Items.Select(i => Literal.String(i.Scheme.Name)))})";
            }

            var entries = r.Items.Select(i =>
                $"[{Literal.String(i.Scheme.Name)}] = new string[] {{ {string.Join(", ", i.Scopes.Select(Literal.String))} }}");
            return $"new {requirement}(new global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.IReadOnlyList<string>> {{ {string.Join(", ", entries)} }})";
        });
        return $"new {requirement}[] {{ {string.Join(", ", items)} }}";
    }

    // ------------------------------------------------------------------ interface and class

    private void EmitInterface(CodeWriter writer, ClientPlan client, List<Method> methods)
    {
        if (_plan.Options.GenerateDocumentation)
        {
            writer.Doc("summary", client.Tag is null
                ? $"The {_plan.Model.Title} API (version {_plan.Model.Version})."
                : $"The '{client.Tag}' operations of the {_plan.Model.Title} API." + (_plan.Model.Tags.FirstOrDefault(t => t.Name == client.Tag)?.Description is { } description ? "\n" + description : string.Empty));
        }

        using (writer.Block($"{_plan.Accessibility} partial interface {client.InterfaceName}"))
        {
            for (var i = 0; i < methods.Count; i++)
            {
                if (i > 0)
                {
                    writer.Line();
                }

                var method = methods[i];
                WriteMethodDocs(writer, method, throws: true);
                WriteObsolete(writer, method);
                writer.Line($"{ThrowingReturnType(method)} {method.Operation.Name}Async({method.Signature});");
                writer.Line();
                WriteMethodDocs(writer, method, throws: false);
                WriteObsolete(writer, method);
                writer.Line($"{ResultReturnType(method)} {method.Operation.Name}ResultAsync({method.Signature});");
            }
        }
    }

    private void EmitClass(CodeWriter writer, ClientPlan client, List<Method> methods)
    {
        var defaultBase = DefaultBaseAddress(out var pathPrefix);
        if (_plan.Options.GenerateDocumentation)
        {
            writer.Doc("summary", $"The {(client.Tag is null ? string.Empty : "'" + client.Tag + "' operations of the ")}{_plan.Model.Title} API client.");
            writer.Doc("remarks", "Register it with the generated Add method to use IHttpClientFactory, or create it with an HttpClient.");
        }

        var interfaces = _plan.Options.GenerateInterfaces ? " : " + client.InterfaceName : string.Empty;
        using (writer.Block($"{_plan.Accessibility} partial class {client.ClassName}{interfaces}"))
        {
            writer.Doc("summary", "The base address of the first server in the OpenAPI document, used when none is configured.");
            writer.Line($"public const string DefaultBaseAddress = {(defaultBase is null ? "\"\"" : Literal.String(defaultBase))};");
            writer.Line();
            writer.Line($"private readonly {Http}.ApiConnection _connection;");
            writer.Line();
            writer.Doc("summary", "Creates a client with default options.");
            writer.Line($"public {client.ClassName}(global::System.Net.Http.HttpClient httpClient)");
            using (writer.Indent())
            {
                writer.Line(": this(httpClient, null)");
            }

            writer.Line("{");
            writer.Line("}");
            writer.Line();
            writer.Doc("summary", "Creates a client.");
            using (writer.Block($"public {client.ClassName}(global::System.Net.Http.HttpClient httpClient, {Runtime}.ApiClientOptions? options)"))
            {
                writer.Line($"_connection = new {Http}.ApiConnection(httpClient, options, DefaultBaseAddress);");
            }

            writer.Line();
            writer.Doc("summary", "The connection that sends requests (HTTP client, options and JSON settings).");
            writer.Line($"public {Http}.ApiConnection Connection => _connection;");

            foreach (var method in methods)
            {
                writer.Line();
                EmitMethodPair(writer, method, pathPrefix);
            }

            foreach (var method in methods)
            {
                writer.Line();
                writer.Line($"private static readonly {Http}.ApiResponseHandler<{method.ResultType}> {method.HandlerField} = new {Http}.ApiResponseHandler<{method.ResultType}>()");
                using (writer.Indent())
                {
                    for (var i = 0; i < method.HandlerLines.Count; i++)
                    {
                        writer.Line(method.HandlerLines[i] + (i == method.HandlerLines.Count - 1 ? ";" : string.Empty));
                    }

                    if (method.HandlerLines.Count == 0)
                    {
                        writer.Line(";");
                    }
                }

                writer.Line();
                writer.Line($"private static readonly global::System.Collections.Generic.IReadOnlyList<{Http}.ApiSecurityRequirement> {method.SecurityField} = {method.SecurityInitializer};");
            }
        }
    }

    private void EmitMethodPair(CodeWriter writer, Method method, string pathPrefix)
    {
        var name = method.Operation.Name;
        writer.Line("/// <inheritdoc />");
        WriteObsolete(writer, method);
        using (writer.Block($"public async {ThrowingReturnType(method)} {name}Async({method.Signature})"))
        {
            writer.Line($"var result = await {name}ResultAsync({method.Arguments}).ConfigureAwait(false);");
            writer.Line(method.ValueType is null ? "result.EnsureSuccess();" : "return result.GetValueOrThrow();");
        }

        writer.Line();
        writer.Line("/// <inheritdoc />");
        WriteObsolete(writer, method);
        var isVoid = method.ValueType is null;
        using (writer.Block($"public {(isVoid ? "async " : string.Empty)}{ResultReturnType(method)} {name}ResultAsync({method.Signature})"))
        {
            foreach (var parameter in method.Parameters.Where(p => p.IsReferenceRequired))
            {
                writer.Line($"global::System.ArgumentNullException.ThrowIfNull({parameter.Name});");
            }

            var operation = method.Operation.Operation;
            var httpMethod = operation.Method switch
            {
                "GET" => "global::System.Net.Http.HttpMethod.Get",
                "POST" => "global::System.Net.Http.HttpMethod.Post",
                "PUT" => "global::System.Net.Http.HttpMethod.Put",
                "DELETE" => "global::System.Net.Http.HttpMethod.Delete",
                "PATCH" => "global::System.Net.Http.HttpMethod.Patch",
                "HEAD" => "global::System.Net.Http.HttpMethod.Head",
                "OPTIONS" => "global::System.Net.Http.HttpMethod.Options",
                "TRACE" => "global::System.Net.Http.HttpMethod.Trace",
                _ => $"new global::System.Net.Http.HttpMethod({Literal.String(operation.Method)})",
            };
            writer.Line($"var builder = new {Http}.ApiRequestBuilder({httpMethod}, {Literal.String(pathPrefix + operation.Path)}, {Literal.String(method.Operation.Id)})");
            writer.Line("{");
            using (writer.Indent())
            {
                writer.Line($"Security = {method.SecurityField},");
            }

            writer.Line("};");
            foreach (var parameter in method.Parameters.Where(p => p.Source is not null))
            {
                EmitParameter(writer, parameter);
            }

            EmitBody(writer, method);
            foreach (var mediaType in method.Accept)
            {
                writer.Line($"builder.Accept({Literal.String(mediaType)});");
            }

            writer.Line(isVoid
                ? $"return await _connection.SendAsync(builder, {method.HandlerField}, cancellationToken).ConfigureAwait(false);"
                : $"return _connection.SendAsync(builder, {method.HandlerField}, cancellationToken);");
        }
    }

    private void EmitParameter(CodeWriter writer, Parameter parameter)
    {
        var source = parameter.Source!;
        string value;
        if (source.ContentMediaType is not null)
        {
            value = parameter.Optional || parameter.ApiType.IsNullable
                ? $"{parameter.Name} is {{ }} {parameter.Name}Value ? {Http}.ParameterValue.Scalar(global::BlazorApi.Runtime.Json.ApiJson.Serialize({parameter.Name}Value, {JsonOptions})) : {Http}.ParameterValue.None"
                : $"{Http}.ParameterValue.Scalar(global::BlazorApi.Runtime.Json.ApiJson.Serialize({parameter.Name}, {JsonOptions}))";
        }
        else if (parameter.Optional || parameter.ApiType.IsNullable)
        {
            value = $"{parameter.Name} is {{ }} {parameter.Name}Value ? {_plan.ParameterValue(parameter.ApiType.WithNullable(false), parameter.Name + "Value", JsonOptions)} : {Http}.ParameterValue.None";
        }
        else
        {
            value = _plan.ParameterValue(parameter.ApiType, parameter.Name, JsonOptions);
        }

        var style = $"{Http}.ParameterStyle.{source.Style}";
        var explode = source.Explode ? "true" : "false";
        var name = Literal.String(source.Name);
        switch (source.Location)
        {
            case ParameterLocation.Path:
                writer.Line($"builder.Path({name}, {value}, {style}, explode: {explode});");
                break;
            case ParameterLocation.Query:
                writer.Line($"builder.Query({name}, {value}, {style}, explode: {explode}{(source.AllowReserved ? ", allowReserved: true" : string.Empty)});");
                break;
            case ParameterLocation.Header:
                writer.Line($"builder.Header({name}, {value}, explode: {explode});");
                break;
            case ParameterLocation.Cookie:
                writer.Line($"builder.Cookie({name}, {value}, explode: {explode});");
                break;
        }
    }

    private readonly struct Field
    {
        public Field(string name, ApiTypeRef type, string? contentType, string access, bool nullable, bool optionalWrapper)
        {
            Name = name;
            Type = type;
            ContentType = contentType;
            Access = access;
            Nullable = nullable;
            OptionalWrapper = optionalWrapper;
        }

        public string Name { get; }

        public ApiTypeRef Type { get; }

        public string? ContentType { get; }

        public string Access { get; }

        public bool Nullable { get; }

        public bool OptionalWrapper { get; }

        public bool IsFile => Type is ApiPrimitiveType { Kind: ApiPrimitiveKind.Binary } ||
            Type is ApiArrayType { Items: ApiPrimitiveType { Kind: ApiPrimitiveKind.Binary } };
    }

    private void EmitBody(CodeWriter writer, Method method)
    {
        if (method.Request is not { } request)
        {
            return;
        }

        var mediaType = Literal.String(request.MediaType);
        if (method.RequestAsParts)
        {
            var fields = method.Parameters.Where(p => p.Part is not null)
                .Select(p => new Field(p.Part!.Name, p.Part.Type, p.Part.ContentType, p.Name, p.Optional || p.ApiType.IsNullable, optionalWrapper: false))
                .ToList();
            EmitFields(writer, request, fields);
            return;
        }

        var body = method.Parameters.FirstOrDefault(p => p.IsBody);
        if (body is null)
        {
            return;
        }

        var nullable = body.Optional || body.ApiType.IsNullable;
        var access = nullable ? body.Name + "Value" : body.Name;
        string? content = request.Kind switch
        {
            ApiBodyKind.Json => $"{Http}.ApiBody.Json({access}, {JsonOptions}, {mediaType})",
            ApiBodyKind.Text or ApiBodyKind.Other => $"{Http}.ApiBody.Text({access}, {mediaType})",
            ApiBodyKind.Binary => $"{Http}.ApiBody.File({access}, {mediaType})",
            _ => null,
        };

        if (content is null)
        {
            // A form or multipart body described by a named model: the fields come from its properties.
            if (body.ApiType is not ApiNamedTypeRef { Target: ApiObjectType obj } || !_plan.IsEmitted(obj))
            {
                return;
            }

            var fields = obj.AllProperties.Where(p => !p.ReadOnly).Select(p =>
            {
                var shape = _plan.PropertyShape(p, obj);
                var encoding = request.Parts.FirstOrDefault(part => part.Name == p.JsonName)?.ContentType;
                return new Field(p.JsonName, p.Type, encoding, $"{access}.{_plan.PropertyName(obj, p)}", shape.CSharpNullable, shape.IsOptionalWrapper);
            }).ToList();

            if (nullable)
            {
                using (writer.Block($"if ({body.Name} is {{ }} {access})"))
                {
                    EmitFields(writer, request, fields);
                }
            }
            else
            {
                EmitFields(writer, request, fields);
            }

            return;
        }

        if (nullable)
        {
            using (writer.Block($"if ({body.Name} is {{ }} {access})"))
            {
                writer.Line($"builder.Content = {content};");
            }
        }
        else
        {
            writer.Line($"builder.Content = {content};");
        }
    }

    private void EmitFields(CodeWriter writer, ApiContent request, List<Field> fields)
    {
        if (request.Kind == ApiBodyKind.FormUrlEncoded)
        {
            writer.Line($"builder.Content = {Http}.ApiBody.Form(new global::System.Collections.Generic.KeyValuePair<string, string?>[]");
            writer.Line("{");
            using (writer.Indent())
            {
                for (var i = 0; i < fields.Count; i++)
                {
                    var field = fields[i];
                    writer.Line($"new({Literal.String(field.Name)}, {FieldText(field.Type, field.Access, field.Nullable, field.OptionalWrapper, "field" + i)}),");
                }
            }

            writer.Line("});");
            return;
        }

        writer.Line($"builder.Content = new {Http}.ApiMultipart({JsonOptions})");
        using (writer.Indent())
        {
            foreach (var field in fields)
            {
                var source = field.OptionalWrapper ? field.Access + ".GetValueOrDefault(default!)" : field.Access;
                var nullable = field.Nullable || field.OptionalWrapper;
                var contentType = field.ContentType is null ? string.Empty : ", " + Literal.String(field.ContentType);
                var name = Literal.String(field.Name);
                var type = field.Type.WithNullable(false);
                string line;
                if (field.IsFile)
                {
                    line = $".Add({name}, {source}{contentType})";
                }
                else if (type is ApiArrayType { Items: ApiPrimitiveType or ApiNamedTypeRef { Target: ApiEnumType } } array)
                {
                    var items = $"global::System.Linq.Enumerable.Select({source}{(nullable ? "!" : string.Empty)}, item => {_plan.FormatScalar(array.Items.WithNullable(false), "item!", JsonOptions)})";
                    line = nullable
                        ? $".Add({name}, {source} is null ? null : {items}{contentType})"
                        : $".Add({name}, {items}{contentType})";
                }
                else if (type is ApiArrayType or ApiMapType or ApiNamedTypeRef { Target: ApiObjectType or ApiUnionType })
                {
                    line = $".AddJson({name}, {source}{contentType})";
                }
                else
                {
                    line = $".Add({name}, {FieldText(field.Type, field.Access, field.Nullable, field.OptionalWrapper, "field" + fields.IndexOf(field))}{contentType})";
                }

                writer.Line(line);
            }

            writer.Line(".Build();");
        }
    }

    private string FieldText(ApiTypeRef type, string access, bool nullable, bool optionalWrapper, string variable)
    {
        if (optionalWrapper)
        {
            access += ".GetValueOrDefault(default!)";
            nullable = true;
        }

        var core = type.WithNullable(false);
        if (core is ApiArrayType or ApiMapType or ApiNamedTypeRef { Target: ApiObjectType or ApiUnionType })
        {
            return nullable
                ? $"{access} is {{ }} {variable} ? global::BlazorApi.Runtime.Json.ApiJson.Serialize({variable}, {JsonOptions}) : null"
                : $"global::BlazorApi.Runtime.Json.ApiJson.Serialize({access}, {JsonOptions})";
        }

        return nullable
            ? $"{access} is {{ }} {variable} ? {_plan.FormatScalar(core, variable, JsonOptions)} : null"
            : _plan.FormatScalar(core, access, JsonOptions);
    }

    // ------------------------------------------------------------------ docs and signatures

    private string ThrowingReturnType(Method method) =>
        method.ValueType is null ? $"{Tasks}.Task" : $"{Tasks}.Task<{method.ValueType}>";

    private string ResultReturnType(Method method) =>
        method.ValueType is null ? $"{Tasks}.Task<{Runtime}.ApiResult>" : $"{Tasks}.Task<{Runtime}.ApiResult<{method.ValueType}>>";

    private void WriteMethodDocs(CodeWriter writer, Method method, bool throws)
    {
        if (!_plan.Options.GenerateDocumentation)
        {
            return;
        }

        var operation = method.Operation.Operation;
        writer.Doc("summary", operation.Summary ?? operation.Description ?? $"{operation.Method} {operation.Path}");
        var remarks = new List<string>();
        if (operation.Summary is not null && operation.Description is not null)
        {
            remarks.Add(operation.Description);
        }

        remarks.Add($"{operation.Method} {operation.Path}" + (operation.OperationId is null ? string.Empty : $" ({operation.OperationId})"));
        if (!throws)
        {
            remarks.Add("Failures (error status codes, network errors, timeouts, unreadable bodies) are returned in the result instead of thrown; only cancellation throws.");
        }

        writer.Doc("remarks", string.Join("\n", remarks));
        foreach (var parameter in method.Parameters)
        {
            writer.Doc("param", parameter.Description ?? (parameter.Source is { } source ? $"The '{source.Name}' {source.Location.ToString().ToLowerInvariant()} parameter." : $"The '{parameter.Part?.Name}' field."), $"name=\"{parameter.Name.TrimStart('@')}\"");
        }

        writer.Line("/// <param name=\"cancellationToken\">Cancels the request.</param>");
        if (throws)
        {
            writer.Doc("returns", method.ValueType is null ? null : method.ReturnsDescription ?? "The response body.");
            writer.Line($"/// <exception cref=\"{Runtime}.ApiException\">The call failed; the exception's Error describes the status code, typed error body, network error, timeout or unreadable body.</exception>");
        }
        else
        {
            writer.Doc("returns", "The result: the value on success, or the error.");
        }
    }

    private static void WriteObsolete(CodeWriter writer, Method method)
    {
        if (method.Operation.Operation.Deprecated)
        {
            writer.Line("[global::System.Obsolete(\"This operation is deprecated by the API.\")]");
        }
    }

    private string? DefaultBaseAddress(out string pathPrefix)
    {
        pathPrefix = string.Empty;
        var server = _plan.Model.Servers.FirstOrDefault();
        if (server is null)
        {
            return null;
        }

        var url = server.DefaultUrl;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && (absolute.Scheme == "http" || absolute.Scheme == "https"))
        {
            return url;
        }

        if (url.StartsWith("/", StringComparison.Ordinal))
        {
            pathPrefix = url.TrimEnd('/');
        }

        return null;
    }

    // ------------------------------------------------------------------ dependency injection

    private string EmitRegistration(ClientPlan client)
    {
        var writer = FileWriter.Start(_plan, _plan.Namespace);
        var serviceType = _plan.Options.GenerateInterfaces ? client.InterfaceName : client.ClassName;
        var extensions = client.ClassName + "ServiceCollectionExtensions";
        writer.Doc("summary", $"Registers the {client.ClassName} with dependency injection.");
        using (writer.Block($"{_plan.Accessibility} static class {extensions}"))
        {
            writer.Doc("summary", $"Registers {serviceType} as a typed HttpClient client. Configure the base address, JSON options and timeout through <paramref name=\"configure\"/>; add authentication and resilience handlers to the returned builder.");
            writer.Line($"/// <param name=\"services\">The service collection.</param>");
            writer.Line($"/// <param name=\"configure\">Configures the client options.</param>");
            writer.Line($"/// <returns>The HTTP client builder, for adding message handlers.</returns>");
            writer.Line($"public static global::Microsoft.Extensions.DependencyInjection.IHttpClientBuilder Add{client.ClassName}(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services, global::System.Action<{Runtime}.ApiClientOptions>? configure = null) =>");
            using (writer.Indent())
            {
                writer.Line($"global::Microsoft.Extensions.DependencyInjection.ApiClientServiceCollectionExtensions.AddApiClient<{serviceType}>(services, {Literal.String(client.ClassName)}, static (httpClient, options) => new {client.ClassName}(httpClient, options), configure);");
            }
        }

        return writer.ToString();
    }
}
