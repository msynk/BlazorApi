using BlazorApi.CodeGeneration.Naming;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.CodeGeneration.CSharp;

/// <summary>Makes the generation decisions: which operations and types are generated, how, and under which names.</summary>
internal static class GenerationPlanner
{
    public static GenerationPlan Create(ApiModel model, GeneratorOptions options, DiagnosticBag diagnostics)
    {
        var plan = new GenerationPlan(model, options, diagnostics);
        var operations = SelectOperations(model, options, diagnostics);
        if (operations.Count == 0 && model.Operations.Count > 0)
        {
            diagnostics.Report(CodeGenerationDiagnostics.NothingGenerated, model.Document.Location, null);
        }

        MarkBodies(plan, operations);
        SelectTypes(plan, operations);
        PlanUnions(plan);
        PlanHierarchies(plan);
        NameTypes(plan);
        PlanClients(plan, operations);
        return plan;
    }

    // ------------------------------------------------------------------ operations

    private static List<ApiOperation> SelectOperations(ApiModel model, GeneratorOptions options, DiagnosticBag diagnostics)
    {
        bool Matches(IReadOnlyList<string> list, string? value) => value is not null && list.Contains(value, StringComparer.Ordinal);

        var selected = model.Operations.Where(o =>
            (options.IncludeOperations.Count == 0 || Matches(options.IncludeOperations, o.OperationId)) &&
            !Matches(options.ExcludeOperations, o.OperationId) &&
            (options.IncludeTags.Count == 0 || o.Tags.Any(t => options.IncludeTags.Contains(t, StringComparer.Ordinal))) &&
            !o.Tags.Any(t => options.ExcludeTags.Contains(t, StringComparer.Ordinal))).ToList();

        void Check(IReadOnlyList<string> values, string what, Func<ApiOperation, string, bool> matches)
        {
            foreach (var value in values.Where(v => !model.Operations.Any(o => matches(o, v))))
            {
                diagnostics.Report(CodeGenerationDiagnostics.FilterMatchedNothing, model.Document.Location, null, what, value);
            }
        }

        Check(options.IncludeOperations, "Included operation", (o, v) => o.OperationId == v);
        Check(options.ExcludeOperations, "Excluded operation", (o, v) => o.OperationId == v);
        Check(options.IncludeTags, "Included tag", (o, v) => o.Tags.Contains(v));
        Check(options.ExcludeTags, "Excluded tag", (o, v) => o.Tags.Contains(v));
        return selected;
    }

    private static void MarkBodies(GenerationPlan plan, List<ApiOperation> operations)
    {
        foreach (var operation in operations)
        {
            if (RequestContent(operation) is not { } content)
            {
                continue;
            }

            if (content.Type is ApiNamedTypeRef { Target: ApiObjectType obj })
            {
                if (content.Kind is ApiBodyKind.Multipart or ApiBodyKind.FormUrlEncoded && obj.Origin == ApiTypeOrigin.Inline)
                {
                    plan.ParameterBodies.Add(obj);
                }

                if (content.Kind == ApiBodyKind.Json && content.MediaType.StartsWith("application/merge-patch+json", StringComparison.OrdinalIgnoreCase))
                {
                    plan.MergePatchTypes.Add(obj);
                }
            }
        }
    }

    /// <summary>The request representation a generated method sends: JSON, then multipart, form, text, binary.</summary>
    public static ApiContent? RequestContent(ApiOperation operation)
    {
        if (operation.RequestBody is not { Contents.Count: > 0 } body)
        {
            return null;
        }

        int Rank(ApiContent c) => c.Kind switch
        {
            ApiBodyKind.Json => 0,
            ApiBodyKind.Multipart => 1,
            ApiBodyKind.FormUrlEncoded => 2,
            ApiBodyKind.Text => 3,
            ApiBodyKind.Binary => 4,
            _ => 5,
        };

        return body.Contents.OrderBy(Rank).First();
    }

    // ------------------------------------------------------------------ types

    private static void SelectTypes(GenerationPlan plan, List<ApiOperation> operations)
    {
        var selected = new HashSet<ApiNamedType>(IdentityComparer.Instance);
        var queue = new Queue<ApiNamedType>();

        void Visit(ApiTypeRef? type)
        {
            switch (type)
            {
                case ApiNamedTypeRef named when selected.Add(named.Target):
                    queue.Enqueue(named.Target);
                    break;
                case ApiArrayType array:
                    Visit(array.Items);
                    break;
                case ApiMapType map:
                    Visit(map.Values);
                    break;
            }
        }

        if (plan.Options.IncludeUnusedModels)
        {
            foreach (var type in plan.Model.Types.Where(t => !plan.ParameterBodies.Contains(t)))
            {
                if (selected.Add(type))
                {
                    queue.Enqueue(type);
                }
            }
        }

        foreach (var operation in operations)
        {
            foreach (var parameter in operation.Parameters)
            {
                Visit(parameter.Type);
            }

            if (RequestContent(operation) is { } request)
            {
                if (request.Type is ApiNamedTypeRef { Target: var target } && plan.ParameterBodies.Contains(target))
                {
                    foreach (var part in request.Parts)
                    {
                        Visit(part.Type);
                    }
                }
                else
                {
                    Visit(request.Type);
                }
            }

            foreach (var response in operation.Responses)
            {
                foreach (var content in response.Contents)
                {
                    Visit(content.Type);
                }

                foreach (var header in response.Headers)
                {
                    Visit(header.Type);
                }
            }
        }

        while (queue.Count > 0)
        {
            switch (queue.Dequeue())
            {
                case ApiObjectType obj:
                    foreach (var property in obj.Properties)
                    {
                        Visit(property.Type);
                    }

                    Visit(obj.AdditionalProperties);
                    if (obj.BaseType is not null)
                    {
                        Visit(new ApiNamedTypeRef(obj.BaseType));
                    }

                    foreach (var derived in obj.DerivedTypes)
                    {
                        Visit(new ApiNamedTypeRef(derived));
                    }

                    break;
                case ApiUnionType union:
                    foreach (var variant in union.Variants)
                    {
                        Visit(variant.Type);
                    }

                    break;
            }
        }

        plan.Types.AddRange(plan.Model.Types.Where(selected.Contains));
    }

    private static void PlanUnions(GenerationPlan plan)
    {
        var unions = plan.Types.OfType<ApiUnionType>().ToList();
        var variantUse = new Dictionary<ApiObjectType, int>(IdentityComparer.Instance);
        foreach (var union in unions)
        {
            foreach (var variant in union.Variants)
            {
                if (variant.Type is ApiNamedTypeRef { Target: ApiObjectType obj })
                {
                    variantUse[obj] = variantUse.TryGetValue(obj, out var count) ? count + 1 : 1;
                }
            }
        }

        foreach (var union in unions)
        {
            var inheritable = union.Dispatch == ApiUnionDispatch.Discriminator &&
                union.Variants.All(v => v.Type is ApiNamedTypeRef { Target: ApiObjectType obj, IsNullable: false } &&
                    obj.BaseType is null && obj.Discriminator is null && variantUse[obj] == 1);
            if (inheritable)
            {
                var polymorphic = new PolymorphicPlan(union, union.DiscriminatorPropertyName!);
                foreach (var variant in union.Variants)
                {
                    var obj = (ApiObjectType)((ApiNamedTypeRef)variant.Type).Target;
                    plan.UnionBase[obj] = union;
                    polymorphic.Mappings.Add((variant.DiscriminatorValue!, obj));
                }

                plan.Polymorphic[union] = polymorphic;
            }
            else
            {
                plan.Wrappers[union] = new UnionWrapperPlan(union);
            }
        }
    }

    private static void PlanHierarchies(GenerationPlan plan)
    {
        foreach (var root in plan.Types.OfType<ApiObjectType>().Where(o => o.Discriminator is not null && o.BaseType is null && o.DerivedTypes.Count > 0))
        {
            var discriminator = root.Discriminator!;
            var polymorphic = new PolymorphicPlan(root, discriminator.PropertyName)
            {
                RootProperty = root.Properties.FirstOrDefault(p => p.JsonName == discriminator.PropertyName &&
                    !p.Type.IsNullable && p.Type is ApiPrimitiveType { Kind: ApiPrimitiveKind.String, ConstValue: null } or ApiNamedTypeRef { Target: ApiEnumType }),
            };

            static int Depth(ApiObjectType type) => type.BaseType is null ? 0 : 1 + Depth(type.BaseType);

            foreach (var mapping in discriminator.Mappings
                .Where(m => !ReferenceEquals(m.Type, root) && m.Type is ApiObjectType && plan.Types.Contains(m.Type))
                .Select((m, index) => (m, index))
                .OrderByDescending(x => Depth((ApiObjectType)x.m.Type))
                .ThenBy(x => x.index))
            {
                polymorphic.Mappings.Add((mapping.m.Value, (ApiObjectType)mapping.m.Type));
            }

            if (polymorphic.Mappings.Count > 0)
            {
                plan.Polymorphic[root] = polymorphic;
            }
        }
    }

    private static void NameTypes(GenerationPlan plan)
    {
        foreach (var type in plan.Types)
        {
            plan.SetTypeName(type, plan.TypeScope.Allocate(Identifier.Pascal(type.NameHint)));
        }

        foreach (var type in plan.Types)
        {
            var name = plan.TypeName(type);
            if (type is ApiEnumType)
            {
                plan.TypeScope.Reserve(name + "JsonConverter");
            }

            if (plan.Polymorphic.TryGetValue(type, out var polymorphic))
            {
                polymorphic.ConverterName = plan.TypeScope.Allocate(name + "JsonConverter");
                polymorphic.UnrecognizedName = plan.TypeScope.Allocate("Unrecognized" + name);
            }

            if (type is ApiUnionType union && plan.Wrappers.TryGetValue(union, out var wrapper))
            {
                wrapper.ConverterName = plan.TypeScope.Allocate(name + "JsonConverter");
                var scope = new NameScope(reserved: [name, "Value", "Variant", "Json", "Equals", "GetHashCode", "ToString", "GetType"]);
                foreach (var variant in union.Variants)
                {
                    wrapper.Variants.Add((variant, scope.Allocate(VariantName(plan, variant.Type))));
                }
            }
        }
    }

    private static string VariantName(GenerationPlan plan, ApiTypeRef type) => type switch
    {
        ApiPrimitiveType p => Identifier.Pascal(plan.Primitive(p) switch
        {
            "string" => "String",
            "bool" => "Boolean",
            "int" => "Int32",
            "long" => "Int64",
            "double" => "Double",
            "float" => "Single",
            "decimal" => "Decimal",
            "byte[]" => "Bytes",
            var other => other.Substring(other.LastIndexOf('.') + 1),
        }),
        ApiArrayType a => VariantName(plan, a.Items) + "List",
        ApiMapType m => VariantName(plan, m.Values) + "Map",
        ApiNamedTypeRef n => plan.IsEmitted(n.Target) ? plan.TypeName(n.Target) : "Json",
        _ => "Value",
    };

    // ------------------------------------------------------------------ clients

    private static void PlanClients(GenerationPlan plan, List<ApiOperation> operations)
    {
        var defaultName = plan.Options.ClientName is { Length: > 0 } configured
            ? configured
            : Identifier.Pascal(StripApiSuffix(plan.Model.Title)) + "Client";
        if (defaultName == "ValueClient")
        {
            defaultName = "ApiClient";
        }

        var clients = new Dictionary<string, ClientPlan>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            var tag = plan.Options.Grouping == ClientGrouping.Tag && operation.Tags.Count > 0 ? operation.Tags[0] : null;
            var key = tag ?? string.Empty;
            if (!clients.TryGetValue(key, out var client))
            {
                var name = tag is null ? defaultName : Identifier.Pascal(tag) + "Client";
                name = plan.TypeScope.Allocate(name);
                plan.TypeScope.Reserve("I" + name);
                plan.TypeScope.Reserve(name + "ServiceCollectionExtensions");
                client = new ClientPlan(name, tag);
                clients[key] = client;
                plan.Clients.Add(client);
                client.Members.Reserve("Connection");
                client.Members.Reserve("DefaultBaseAddress");
            }

            var methodName = client.Members.Allocate(Identifier.Pascal(operation.NameHint));
            client.Members.Reserve(methodName + "Async");
            client.Members.Reserve(methodName + "ResultAsync");
            client.Operations.Add(new OperationPlan(operation, methodName));
        }
    }

    private static string StripApiSuffix(string title)
    {
        var words = Identifier.SplitWords(title).ToList();
        while (words.Count > 1 && words[words.Count - 1].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(" ", words);
    }
}
