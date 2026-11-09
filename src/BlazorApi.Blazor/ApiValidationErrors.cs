using System.Reflection;
using BlazorApi.Runtime;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace BlazorApi.Blazor;

/// <summary>
/// Shows server-side validation errors of an API call in an <c>EditForm</c>: place it inside the form and set
/// <see cref="Error"/> to the failed result's error. Field errors (problem details <c>errors</c>) appear next to the
/// matching inputs through <c>ValidationMessage</c>; errors for unknown fields appear in the <c>ValidationSummary</c>.
/// A field's server messages disappear when the user edits it.
/// </summary>
public sealed class ApiValidationErrors : ComponentBase, IDisposable
{
    private ValidationMessageStore? _store;
    private EditContext? _subscribed;
    private ApiError? _shown;

    /// <summary>The form's edit context (cascaded by <c>EditForm</c>).</summary>
    [CascadingParameter]
    public EditContext? EditContext { get; set; }

    /// <summary>The error whose field errors to show; null clears them.</summary>
    [Parameter]
    public ApiError? Error { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (EditContext is null)
        {
            throw new InvalidOperationException($"{nameof(ApiValidationErrors)} must be placed inside an EditForm.");
        }

        if (!ReferenceEquals(_subscribed, EditContext))
        {
            Unsubscribe();
            _store = new ValidationMessageStore(EditContext);
            EditContext.OnFieldChanged += FieldChanged;
            EditContext.OnValidationRequested += ValidationRequested;
            _subscribed = EditContext;
        }

        if (!ReferenceEquals(_shown, Error))
        {
            _shown = Error;
            Apply(EditContext, _store!, Error);
            EditContext.NotifyValidationStateChanged();
        }
    }

    /// <summary>Adds the field errors of an API error to a message store, mapping keys to the model's properties.</summary>
    public static void Apply(EditContext editContext, ValidationMessageStore store, ApiError? error)
    {
        if (editContext is null)
        {
            throw new ArgumentNullException(nameof(editContext));
        }

        if (store is null)
        {
            throw new ArgumentNullException(nameof(store));
        }

        store.Clear();
        foreach (var field in ApiValidation.GetFieldErrors(error))
        {
            store.Add(FieldFor(editContext.Model, field.Key), field.Value);
        }
    }

    /// <summary>
    /// Maps an error key (<c>name</c>, <c>Name</c>, <c>$.name</c>, <c>owner.email</c>) to a field: the matching property
    /// of the model, or of a nested object for dotted paths; the model itself (form-level) when nothing matches.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Form models are bound by Blazor through their public properties, which are therefore preserved.")]
    public static FieldIdentifier FieldFor(object model, string key)
    {
        if (model is null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        var path = (key ?? string.Empty).TrimStart('$', '.').Split('.');
        object current = model;
        for (var i = 0; i < path.Length; i++)
        {
            var segment = path[i];
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0)
            {
                segment = segment.Substring(0, bracket);
            }

            var property = current.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
            {
                return new FieldIdentifier(model, string.Empty);
            }

            if (i == path.Length - 1 || bracket >= 0)
            {
                return new FieldIdentifier(current, property.Name);
            }

            if (property.GetValue(current) is not { } next)
            {
                return new FieldIdentifier(current, property.Name);
            }

            current = next;
        }

        return new FieldIdentifier(model, string.Empty);
    }

    private void FieldChanged(object? sender, FieldChangedEventArgs e)
    {
        _store?.Clear(e.FieldIdentifier);
        EditContext?.NotifyValidationStateChanged();
    }

    private void ValidationRequested(object? sender, ValidationRequestedEventArgs e) => _store?.Clear();

    private void Unsubscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.OnFieldChanged -= FieldChanged;
            _subscribed.OnValidationRequested -= ValidationRequested;
            _subscribed = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _store?.Clear();
        Unsubscribe();
    }
}
