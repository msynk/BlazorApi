namespace BlazorApi.OpenApi.Document;

/// <summary>Enumerates the elements of an OpenAPI model that can contain or be references.</summary>
public static class OpenApiWalker
{
    /// <summary>
    /// The direct child elements of an element (not following references). Info, servers, tags and security
    /// requirements are not included because they cannot contain references.
    /// </summary>
    public static IEnumerable<OpenApiElement> Children(OpenApiElement element)
    {
        switch (element)
        {
            case OpenApiDocument document:
                foreach (var item in document.Paths.Values)
                {
                    yield return item;
                }

                foreach (var item in document.Webhooks.Values)
                {
                    yield return item;
                }

                yield return document.Components;
                break;
            case OpenApiComponents components:
                foreach (var e in components.Schemas.Values)
                {
                    yield return e;
                }

                foreach (var e in components.Responses.Values)
                {
                    yield return e;
                }

                foreach (var e in components.Parameters.Values)
                {
                    yield return e;
                }

                foreach (var e in components.Examples.Values)
                {
                    yield return e;
                }

                foreach (var e in components.RequestBodies.Values)
                {
                    yield return e;
                }

                foreach (var e in components.Headers.Values)
                {
                    yield return e;
                }

                foreach (var e in components.SecuritySchemes.Values)
                {
                    yield return e;
                }

                foreach (var e in components.PathItems.Values)
                {
                    yield return e;
                }

                break;
            case OpenApiPathItem pathItem:
                foreach (var parameter in pathItem.Parameters)
                {
                    yield return parameter;
                }

                foreach (var operation in pathItem.Operations.Values)
                {
                    yield return operation;
                }

                break;
            case OpenApiOperation operation:
                foreach (var parameter in operation.Parameters)
                {
                    yield return parameter;
                }

                if (operation.RequestBody is not null)
                {
                    yield return operation.RequestBody;
                }

                foreach (var response in operation.Responses.Values)
                {
                    yield return response;
                }

                break;
            case OpenApiParameter parameter:
                foreach (var child in ParameterChildren(parameter))
                {
                    yield return child;
                }

                break;
            case OpenApiHeader header:
                foreach (var child in ParameterChildren(header))
                {
                    yield return child;
                }

                break;
            case OpenApiRequestBody body:
                foreach (var media in body.Content.Values)
                {
                    yield return media;
                }

                break;
            case OpenApiResponse response:
                foreach (var header in response.Headers.Values)
                {
                    yield return header;
                }

                foreach (var media in response.Content.Values)
                {
                    yield return media;
                }

                break;
            case OpenApiMediaType media:
                if (media.Schema is not null)
                {
                    yield return media.Schema;
                }

                foreach (var example in media.Examples.Values)
                {
                    yield return example;
                }

                foreach (var encoding in media.Encoding.Values)
                {
                    yield return encoding;
                }

                break;
            case OpenApiEncoding encoding:
                foreach (var header in encoding.Headers.Values)
                {
                    yield return header;
                }

                break;
            case OpenApiSchema schema:
                foreach (var property in schema.Properties.Values)
                {
                    yield return property;
                }

                if (schema.AdditionalProperties is not null)
                {
                    yield return schema.AdditionalProperties;
                }

                if (schema.Items is not null)
                {
                    yield return schema.Items;
                }

                foreach (var s in schema.AllOf)
                {
                    yield return s;
                }

                foreach (var s in schema.OneOf)
                {
                    yield return s;
                }

                foreach (var s in schema.AnyOf)
                {
                    yield return s;
                }

                foreach (var s in schema.Definitions.Values)
                {
                    yield return s;
                }

                break;
        }
    }

    /// <summary>
    /// Enumerates every element reachable from <paramref name="root"/>, each once, following resolved references
    /// when <paramref name="followReferences"/> is set. The order is deterministic (depth-first, document order).
    /// </summary>
    public static IEnumerable<OpenApiElement> Descendants(OpenApiElement root, bool followReferences)
    {
        var visited = new HashSet<OpenApiElement>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<OpenApiElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            if (!visited.Add(element))
            {
                continue;
            }

            yield return element;
            var children = Children(element).ToList();
            if (followReferences && ReferenceTargetOf(element) is { } target)
            {
                children.Add(target);
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }
    }

    /// <summary>The direct reference target of an element, when it is a resolved reference.</summary>
    public static OpenApiElement? ReferenceTargetOf(OpenApiElement element) => element switch
    {
        OpenApiSchema s => s.ReferenceTarget,
        OpenApiParameter p => p.ReferenceTarget,
        OpenApiHeader h => h.ReferenceTarget,
        OpenApiResponse r => r.ReferenceTarget,
        OpenApiRequestBody b => b.ReferenceTarget,
        OpenApiExample e => e.ReferenceTarget,
        OpenApiSecurityScheme s => s.ReferenceTarget,
        OpenApiPathItem p => p.ReferenceTarget,
        _ => null,
    };

    /// <summary>The raw <c>$ref</c> of an element, when it is a reference.</summary>
    public static string? ReferenceOf(OpenApiElement element) => element switch
    {
        OpenApiSchema s => s.Reference,
        OpenApiParameter p => p.Reference,
        OpenApiHeader h => h.Reference,
        OpenApiResponse r => r.Reference,
        OpenApiRequestBody b => b.Reference,
        OpenApiExample e => e.Reference,
        OpenApiSecurityScheme s => s.Reference,
        OpenApiPathItem p => p.Reference,
        _ => null,
    };

    private static IEnumerable<OpenApiElement> ParameterChildren<T>(OpenApiParameterBase<T> parameter)
        where T : OpenApiParameterBase<T>
    {
        if (parameter.Schema is not null)
        {
            yield return parameter.Schema;
        }

        foreach (var media in parameter.Content.Values)
        {
            yield return media;
        }

        foreach (var example in parameter.Examples.Values)
        {
            yield return example;
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<OpenApiElement>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public bool Equals(OpenApiElement? x, OpenApiElement? y) => ReferenceEquals(x, y);

        public int GetHashCode(OpenApiElement obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
