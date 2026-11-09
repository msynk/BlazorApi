using System.Diagnostics.CodeAnalysis;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.OpenApi.Document;

/// <summary>The base of every OpenAPI document element: source position, JSON pointer and extensions.</summary>
public abstract class OpenApiElement
{
    /// <summary>Where the element starts in its source document.</summary>
    public SourceLocation Location { get; internal set; }

    /// <summary>
    /// The JSON pointer of the element, as a URI fragment (<c>#/components/schemas/Pet</c>). Elements read from
    /// another document are prefixed with that document's path (<c>common.yaml#/components/schemas/Error</c>).
    /// </summary>
    public string Pointer { get; internal set; } = "#";

    /// <summary>The specification extensions (<c>x-</c> fields), in document order.</summary>
    public OrderedMap<DocNode> Extensions { get; internal set; } = OrderedMap<DocNode>.Empty;

    /// <summary>The source node the element was read from.</summary>
    public DocNode? Node { get; internal set; }

    /// <summary>Gets an extension value by name (including the <c>x-</c> prefix).</summary>
    public bool TryGetExtension(string name, [NotNullWhen(true)] out DocNode? value) =>
        Extensions.TryGetValue(name, out value);

    /// <summary>Gets a string extension value, or <see langword="null"/>.</summary>
    public string? GetExtensionString(string name) =>
        Extensions.TryGetValue(name, out var value) && value is ScalarNode { IsNull: false } scalar ? scalar.Text : null;

    /// <summary>Gets a boolean extension value, or <see langword="null"/>.</summary>
    public bool? GetExtensionBoolean(string name) =>
        Extensions.TryGetValue(name, out var value) && value is ScalarNode scalar && scalar.TryGetBoolean(out var b) ? b : null;
}

/// <summary>
/// An element that may be a Reference Object (<c>$ref</c>). After reference resolution,
/// <see cref="ReferenceTarget"/> holds the final (non-reference) target and <see cref="Resolved"/> returns it.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public abstract class OpenApiReferenceable<T> : OpenApiElement
    where T : OpenApiReferenceable<T>
{
    /// <summary>The raw <c>$ref</c> value, when this element is a reference.</summary>
    public string? Reference { get; internal set; }

    /// <summary>Whether this element is a reference.</summary>
    [MemberNotNullWhen(true, nameof(Reference))]
    public bool IsReference => Reference is not null;

    /// <summary>
    /// The direct target of the reference, after reference resolution; <see langword="null"/> when unresolved. The
    /// target may itself be a reference (and, for 3.1 schemas, carry sibling keywords); see <see cref="Resolved"/>.
    /// </summary>
    public T? ReferenceTarget { get; internal set; }

    /// <summary>
    /// The <c>summary</c> sibling of a Reference Object (OpenAPI 3.1), which overrides the target's summary.
    /// </summary>
    public string? ReferenceSummary { get; internal set; }

    /// <summary>
    /// The <c>description</c> sibling of a Reference Object (OpenAPI 3.1), which overrides the target's description.
    /// </summary>
    public string? ReferenceDescription { get; internal set; }

    /// <summary>
    /// The element itself when it is not a reference; otherwise the end of its (resolved) reference chain. An
    /// unresolvable reference resolves to itself.
    /// </summary>
    public T Resolved
    {
        get
        {
            var current = (T)this;
            for (var guard = 0; current.ReferenceTarget is { } next && guard < 64; guard++)
            {
                current = next;
            }

            return current;
        }
    }
}
