using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Petstore.BlazorApp.Services;

/// <summary>A property of a generated model, as compiled.</summary>
public sealed record ModelProperty(string Name, string JsonName, string Type, bool Required, bool DeclaredHere, IReadOnlyList<string> Attributes);

/// <summary>Reads generated models by reflection, so the guide shows what the generator actually produced.</summary>
public static class ModelInspector
{
    private static readonly NullabilityInfoContext Nullability = new();

    public static IReadOnlyList<ModelProperty> Describe(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
            .OrderBy(p => p.DeclaringType == type ? 1 : 0)
            .Select(p => new ModelProperty(
                p.Name,
                p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name,
                FormatType(p),
                p.GetCustomAttribute<RequiredMemberAttribute>() is not null,
                p.DeclaringType == type,
                p.GetCustomAttributes<ValidationAttribute>().Select(Describe).ToList()))
            .ToList();

    private static string Describe(ValidationAttribute attribute) => attribute switch
    {
        RequiredAttribute { AllowEmptyStrings: true } => "[Required(AllowEmptyStrings = true)]",
        RequiredAttribute => "[Required]",
        StringLengthAttribute length => $"[StringLength({length.MaximumLength}, MinimumLength = {length.MinimumLength})]",
        RangeAttribute range => $"[Range({range.Minimum}, {range.Maximum})]",
        RegularExpressionAttribute regex => $"[RegularExpression(\"{regex.Pattern}\")]",
        MinLengthAttribute min => $"[MinLength({min.Length})]",
        MaxLengthAttribute max => $"[MaxLength({max.Length})]",
        _ => $"[{attribute.GetType().Name.Replace("Attribute", string.Empty, StringComparison.Ordinal)}]",
    };

    private static string FormatType(PropertyInfo property)
    {
        var info = Nullability.Create(property);
        return Format(property.PropertyType, info);
    }

    private static string Format(Type type, NullabilityInfo? info)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Format(underlying, null) + "?";
        }

        var name = type switch
        {
            _ when type == typeof(string) => "string",
            _ when type == typeof(int) => "int",
            _ when type == typeof(long) => "long",
            _ when type == typeof(double) => "double",
            _ when type == typeof(bool) => "bool",
            { IsGenericType: true } => $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select((t, i) => Format(t, info?.GenericTypeArguments.ElementAtOrDefault(i))))}>",
            _ => type.Name,
        };

        return !type.IsValueType && info?.ReadState == NullabilityState.Nullable ? name + "?" : name;
    }
}
