using System.Globalization;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Overlays;

/// <summary>An OpenAPI Overlay document (JSON or YAML) to apply to a description before it is read.</summary>
/// <param name="Path">The overlay's path or URI (for diagnostics and format detection).</param>
/// <param name="Text">The overlay's text.</param>
public sealed record OpenApiOverlay(string Path, string Text);

/// <summary>
/// Applies OpenAPI Overlay documents (versions 1.0 to 1.2) to a parsed description: each action selects nodes with an
/// RFC 9535 JSONPath <c>target</c> and updates them (recursive merge, array concatenation, primitive replacement),
/// copies another node onto them (<c>copy</c>), or removes them. Reusable actions (<c>components.actions</c>,
/// 1.2) are supported. The description is copied first, so the parsed original is never changed.
/// </summary>
internal static class OverlayProcessor
{
    public static DocNode Apply(DocNode root, IReadOnlyList<OpenApiOverlay> overlays, DiagnosticBag diagnostics, ParserLimits limits)
    {
        var document = root.DeepClone();
        foreach (var overlay in overlays)
        {
            var parsed = SyntaxParser.Parse(overlay.Text, overlay.Path, DocumentFormat.Auto, diagnostics, limits);
            if (parsed is null)
            {
                continue;
            }

            if (parsed is not ObjectNode obj)
            {
                Invalid(diagnostics, parsed, "the overlay document must be an object");
                continue;
            }

            if (!CheckHeader(obj, diagnostics))
            {
                continue;
            }

            var reusable = obj["components"] is ObjectNode components && components["actions"] is ObjectNode actions ? actions : null;
            foreach (var item in ((ArrayNode)obj["actions"]!).Items)
            {
                if (item is not ObjectNode action)
                {
                    Invalid(diagnostics, item, "each action must be an object");
                    continue;
                }

                if (Resolve(action, reusable, diagnostics) is { } resolved)
                {
                    ApplyAction(document, resolved, diagnostics);
                }
            }
        }

        return document;
    }

    private static bool CheckHeader(ObjectNode overlay, DiagnosticBag diagnostics)
    {
        if (overlay["overlay"] is not ScalarNode { IsString: true } version)
        {
            Invalid(diagnostics, overlay, "the 'overlay' field (the Overlay Specification version) is required and must be a string");
            return false;
        }

        var parts = version.Value.Split('.');
        if (parts.Length < 2 || parts[0] != "1" || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            Invalid(diagnostics, version, $"overlay version '{version.Value}' is not supported; expected 1.x");
            return false;
        }

        if (minor > 2)
        {
            diagnostics.Report(DiagnosticDescriptors.UnsupportedOverlayVersion, version.Location, "/overlay", version.Value);
        }

        if (overlay["info"] is not ObjectNode info || info["title"] is not ScalarNode { IsString: true } || info["version"] is not ScalarNode { IsString: true })
        {
            Invalid(diagnostics, overlay["info"] ?? overlay, "the 'info' object with string 'title' and 'version' fields is required");
        }

        if (overlay["actions"] is not ArrayNode { Count: > 0 })
        {
            Invalid(diagnostics, overlay["actions"] ?? overlay, "the 'actions' field must be an array with at least one action");
            return false;
        }

        return true;
    }

    /// <summary>Resolves a reusable action reference (<c>$ref</c> to <c>#/components/actions/name</c>, 1.2).</summary>
    private static Action? Resolve(ObjectNode action, ObjectNode? reusable, DiagnosticBag diagnostics)
    {
        var fields = action;
        if (action["$ref"] is { } reference)
        {
            const string prefix = "#/components/actions/";
            if (reference is not ScalarNode { IsString: true } text || !text.Value.StartsWith(prefix, StringComparison.Ordinal))
            {
                Invalid(diagnostics, reference, $"an action '$ref' must point to '{prefix}<name>'");
                return null;
            }

            var name = text.Value.Substring(prefix.Length).Replace("~1", "/").Replace("~0", "~");
            if (reusable?[name] is not ObjectNode definition || definition["fields"] is not ObjectNode referenced)
            {
                Invalid(diagnostics, reference, $"the reusable action '{name}' is not defined (with 'fields') under components.actions");
                return null;
            }

            fields = referenced;
        }

        if (action["target"] is not ScalarNode { IsString: true } target)
        {
            Invalid(diagnostics, action, "each action needs a string 'target' (a JSONPath)");
            return null;
        }

        // Fields of the referencing action win over the reusable action's fields.
        DocNode? Field(string name) => action[name] ?? (ReferenceEquals(fields, action) ? null : fields[name]);
        var remove = Field("remove");
        if (remove is not null and not ScalarNode { Kind: NodeKind.Boolean })
        {
            Invalid(diagnostics, remove, "'remove' must be a boolean");
            return null;
        }

        var copy = Field("copy");
        if (copy is not null and not ScalarNode { IsString: true })
        {
            Invalid(diagnostics, copy, "'copy' must be a string (a JSONPath)");
            return null;
        }

        return new Action(action, target, Field("update"), (ScalarNode?)copy, remove is ScalarNode { Value: "true" });
    }

    private sealed record Action(ObjectNode Source, ScalarNode Target, DocNode? Update, ScalarNode? Copy, bool Remove);

    private static void ApplyAction(DocNode document, Action action, DiagnosticBag diagnostics)
    {
        if (!JsonPath.TryParse(action.Target.Value, out var path, out var error))
        {
            diagnostics.Report(DiagnosticDescriptors.InvalidJsonPath, action.Target.Location, null, action.Target.Value, error);
            return;
        }

        if (!action.Remove && action.Update is null && action.Copy is null)
        {
            Invalid(diagnostics, action.Source, "an action needs 'update', 'copy' or 'remove: true'");
            return;
        }

        if (!action.Remove && action.Update is not null && action.Copy is not null)
        {
            Invalid(diagnostics, action.Source, "an action cannot have both 'update' and 'copy'");
            return;
        }

        var selected = Distinct(path!.Select(document));
        if (selected.Count == 0)
        {
            diagnostics.Report(DiagnosticDescriptors.OverlayTargetMatchedNothing, action.Target.Location, null, action.Target.Value);
            return;
        }

        if (action.Remove)
        {
            Remove(selected, action, diagnostics);
            return;
        }

        var value = action.Update;
        if (action.Copy is { } copy)
        {
            if (!JsonPath.TryParse(copy.Value, out var copyPath, out var copyError))
            {
                diagnostics.Report(DiagnosticDescriptors.InvalidJsonPath, copy.Location, null, copy.Value, copyError);
                return;
            }

            var sources = Distinct(copyPath!.Select(document));
            if (sources.Count != 1)
            {
                Incompatible(diagnostics, action, $"'copy' must select exactly one node but '{copy.Value}' selects {sources.Count}");
                return;
            }

            value = sources[0].Node.DeepClone();
        }

        var categories = selected.Select(n => Category(n.Node)).Distinct().ToList();
        if (categories.Count > 1)
        {
            Incompatible(diagnostics, action, "the selected nodes must be all objects, all arrays or all primitive values");
            return;
        }

        foreach (var node in selected)
        {
            if (Update(node, value!, out var problem) is false)
            {
                Incompatible(diagnostics, action, problem!);
                return;
            }
        }
    }

    private static bool Update(JsonPathNode target, DocNode value, out string? problem)
    {
        problem = null;
        switch (target.Node)
        {
            case ObjectNode obj when value is ObjectNode update:
                return Merge(obj, update, "", out problem);
            case ObjectNode:
                problem = $"an object can only be updated with an object, not {value.KindDescription}";
                return false;
            case ArrayNode array:
                if (value is ArrayNode items)
                {
                    foreach (var item in items.Items)
                    {
                        array.Add(item.DeepClone());
                    }
                }
                else
                {
                    array.Add(value.DeepClone());
                }

                return true;
            default:
                if (value is not ScalarNode)
                {
                    problem = $"a primitive value can only be replaced by a primitive value, not {value.KindDescription}";
                    return false;
                }

                Replace(target, value);
                return true;
        }
    }

    /// <summary>
    /// Merges an update into an object: new properties are added, objects merged recursively, arrays concatenated and
    /// primitive values replaced; other combinations are incompatible.
    /// </summary>
    private static bool Merge(ObjectNode target, ObjectNode update, string path, out string? problem)
    {
        problem = null;
        foreach (var property in update.Properties)
        {
            var location = path + "/" + property.Name;
            var existing = target.GetProperty(property.Name);
            if (existing is null)
            {
                target.Add(property.Name, property.KeyLocation, property.Value.DeepClone());
                continue;
            }

            switch (existing.Value, property.Value)
            {
                case (ObjectNode inner, ObjectNode innerUpdate):
                    if (!Merge(inner, innerUpdate, location, out problem))
                    {
                        return false;
                    }

                    break;
                case (ArrayNode array, ArrayNode items):
                    foreach (var item in items.Items)
                    {
                        array.Add(item.DeepClone());
                    }

                    break;
                case (ScalarNode, ScalarNode):
                    target.Add(property.Name, existing.KeyLocation, property.Value);
                    break;
                default:
                    problem = $"'{location}' is {existing.Value.KindDescription} and cannot be merged with {property.Value.KindDescription}";
                    return false;
            }
        }

        return true;
    }

    private static void Replace(JsonPathNode target, DocNode value)
    {
        switch (target.Parent)
        {
            case ObjectNode obj:
                obj.Add(target.Name!, obj.GetProperty(target.Name!)!.KeyLocation, value);
                break;
            case ArrayNode array:
                array.Set(target.Index, value);
                break;
        }
    }

    private static void Remove(List<JsonPathNode> selected, Action action, DiagnosticBag diagnostics)
    {
        // Array items are removed from the highest index down, so earlier removals do not shift later ones.
        foreach (var node in selected.OrderByDescending(n => n.Index))
        {
            switch (node.Parent)
            {
                case null:
                    Incompatible(diagnostics, action, "the document root cannot be removed");
                    return;
                case ObjectNode obj:
                    obj.Remove(node.Name!);
                    break;
                case ArrayNode array when node.Index < array.Count && ReferenceEquals(array[node.Index], node.Node):
                    array.RemoveAt(node.Index);
                    break;
            }
        }
    }

    /// <summary>Drops nodes selected more than once (RFC 9535 allows duplicates, an overlay applies once).</summary>
    private static List<JsonPathNode> Distinct(IReadOnlyList<JsonPathNode> nodes)
    {
        var seen = new HashSet<(object, string?, int)>(PositionComparer.Instance);
        var result = new List<JsonPathNode>();
        foreach (var node in nodes)
        {
            if (seen.Add((node.Parent ?? node.Node, node.Name, node.Index)))
            {
                result.Add(node);
            }
        }

        return result;
    }

    /// <summary>Compares node positions: the parent by identity, then the member name or index.</summary>
    private sealed class PositionComparer : IEqualityComparer<(object Parent, string? Name, int Index)>
    {
        public static readonly PositionComparer Instance = new();

        public bool Equals((object Parent, string? Name, int Index) x, (object Parent, string? Name, int Index) y) =>
            ReferenceEquals(x.Parent, y.Parent) && x.Name == y.Name && x.Index == y.Index;

        public int GetHashCode((object Parent, string? Name, int Index) obj) =>
            (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Parent) * 397) ^ (obj.Name?.GetHashCode() ?? obj.Index);
    }

    private static int Category(DocNode node) => node switch
    {
        ObjectNode => 0,
        ArrayNode => 1,
        _ => 2,
    };

    private static void Invalid(DiagnosticBag diagnostics, DocNode node, string message) =>
        diagnostics.Report(DiagnosticDescriptors.InvalidOverlay, node.Location, null, message);

    private static void Incompatible(DiagnosticBag diagnostics, Action action, string message) =>
        diagnostics.Report(DiagnosticDescriptors.IncompatibleOverlayUpdate, action.Target.Location, null, action.Target.Value, message);
}
