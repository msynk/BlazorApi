using System.Globalization;
using System.Text;

namespace BlazorApi.CodeGeneration.Naming;

/// <summary>Turns arbitrary API names into valid, conventional C# identifiers.</summary>
public static class Identifier
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        // Contextual keywords that are risky as parameter or local names.
        "value", "var", "dynamic", "await", "async", "nameof", "record", "required", "scoped", "file", "field",
    };

    /// <summary>
    /// Splits text into words at non-alphanumeric characters, lower-to-upper case changes, acronym boundaries
    /// (<c>HTTPServer</c> becomes <c>HTTP</c>, <c>Server</c>) and letter/digit boundaries after letters.
    /// </summary>
    public static IReadOnlyList<string> SplitWords(string text)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return words;
        }

        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                var previous = text[i - 1];
                var lowerToUpper = char.IsLower(previous) && char.IsUpper(c);
                var acronymEnd = char.IsUpper(previous) && char.IsUpper(c) && i + 1 < text.Length && char.IsLower(text[i + 1]);
                var digitAfterLetter = char.IsLetter(previous) && char.IsDigit(c);
                var letterAfterDigit = char.IsDigit(previous) && char.IsUpper(c);
                if (lowerToUpper || acronymEnd || digitAfterLetter || letterAfterDigit)
                {
                    Flush();
                }
            }

            current.Append(c);
        }

        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }

    /// <summary>Converts parts to a PascalCase identifier (<c>pet_id</c> becomes <c>PetId</c>, <c>HTTP</c> becomes <c>Http</c>).</summary>
    public static string Pascal(params string[] parts) => Pascal((IEnumerable<string>)parts);

    /// <summary>Converts parts to a PascalCase identifier.</summary>
    public static string Pascal(IEnumerable<string> parts)
    {
        var builder = new StringBuilder();
        foreach (var word in parts.SelectMany(SplitWords))
        {
            builder.Append(Capitalize(word));
        }

        return MakeValid(builder.ToString(), "Value");
    }

    /// <summary>Converts parts to a camelCase identifier, escaped with <c>@</c> when it is a keyword.</summary>
    public static string Camel(params string[] parts) => Camel((IEnumerable<string>)parts);

    /// <summary>Converts parts to a camelCase identifier, escaped with <c>@</c> when it is a keyword.</summary>
    public static string Camel(IEnumerable<string> parts)
    {
        var pascal = Pascal(parts);
        if (pascal.Length == 0 || char.IsDigit(pascal[0]) || pascal[0] == '_')
        {
            return EscapeKeyword(pascal);
        }

        // Lower-case the leading acronym or word: "XRequestId" -> "xRequestId", "IOStream" -> "ioStream".
        var end = 1;
        while (end < pascal.Length && char.IsUpper(pascal[end]) && (end + 1 >= pascal.Length || char.IsUpper(pascal[end + 1])))
        {
            end++;
        }

        var camel = pascal.Substring(0, end).ToLowerInvariant() + pascal.Substring(end);
        return EscapeKeyword(camel);
    }

    /// <summary>Escapes a C# keyword with <c>@</c>.</summary>
    public static string EscapeKeyword(string identifier) =>
        Keywords.Contains(identifier) ? "@" + identifier : identifier;

    /// <summary>Whether the identifier is a C# keyword.</summary>
    public static bool IsKeyword(string identifier) => Keywords.Contains(identifier);

    private static string Capitalize(string word)
    {
        if (word.Length == 0)
        {
            return word;
        }

        // All-caps words longer than two letters follow .NET casing (HTTP -> Http); "ID" -> "Id".
        var allUpper = word.All(c => !char.IsLetter(c) || char.IsUpper(c));
        if (allUpper && word.Count(char.IsLetter) > 1)
        {
            return word.Substring(0, 1) + word.Substring(1).ToLowerInvariant();
        }

        return char.ToUpperInvariant(word[0]) + word.Substring(1);
    }

    /// <summary>Makes an identifier valid: non-empty and not starting with a digit.</summary>
    private static string MakeValid(string identifier, string fallback)
    {
        if (identifier.Length == 0)
        {
            return fallback;
        }

        return char.IsDigit(identifier[0]) ? "_" + identifier : identifier;
    }

    /// <summary>Creates an enum member name for a value (<c>-1</c> becomes <c>Minus1</c>, <c>2</c> becomes <c>Value2</c>).</summary>
    public static string EnumMember(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0 && (char.IsDigit(trimmed[0]) || trimmed[0] == '-' || trimmed[0] == '+') &&
            decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            var digits = trimmed.Replace("-", "Minus").Replace("+", string.Empty).Replace(".", "_");
            return char.IsDigit(digits[0]) ? "Value" + digits : digits;
        }

        var pascal = Pascal(trimmed);
        return pascal == "Value" && trimmed.Length == 0 ? "Empty" : pascal;
    }
}

/// <summary>Allocates unique identifiers within a scope (a namespace, a type's members, a method's parameters).</summary>
public sealed class NameScope
{
    private readonly HashSet<string> _used;

    /// <summary>Creates a scope; names compare case-sensitively unless <paramref name="ignoreCase"/> is set.</summary>
    public NameScope(bool ignoreCase = false, IEnumerable<string>? reserved = null)
    {
        _used = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (reserved is not null)
        {
            foreach (var name in reserved)
            {
                _used.Add(name);
            }
        }
    }

    /// <summary>Reserves a name without allocating it.</summary>
    public void Reserve(string name) => _used.Add(name);

    /// <summary>Whether a name is taken.</summary>
    public bool IsUsed(string name) => _used.Contains(name);

    /// <summary>Returns <paramref name="name"/>, or the name with the smallest numeric suffix (from 2) that is free.</summary>
    public string Allocate(string name)
    {
        var bare = name.TrimStart('@');
        if (_used.Add(bare))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var candidate = bare + i.ToString(CultureInfo.InvariantCulture);
            if (_used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
