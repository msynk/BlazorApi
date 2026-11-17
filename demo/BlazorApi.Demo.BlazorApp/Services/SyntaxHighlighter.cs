using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace BlazorApi.Demo.BlazorApp.Services;

/// <summary>
/// A small regex-based highlighter for the code shown in the guide (C#, Razor, XML, YAML, JSON, CSS, shell). It only
/// wraps tokens in spans; the text is HTML-encoded, so any code is safe to render as markup.
/// </summary>
public static partial class SyntaxHighlighter
{
    private const string Keywords =
        "abstract|as|async|await|base|bool|break|byte|case|catch|char|class|const|continue|decimal|default|delegate|do|" +
        "double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|get|global|goto|if|implicit|in|init|" +
        "int|interface|internal|is|lock|long|nameof|namespace|new|not|null|object|operator|or|and|out|override|params|partial|" +
        "private|protected|public|readonly|record|ref|required|return|sbyte|sealed|set|short|sizeof|static|string|struct|" +
        "switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|value|var|virtual|void|volatile|when|" +
        "where|while|with|yield";

    public static string Highlight(string code, string? language)
    {
        var pattern = language?.ToLowerInvariant() switch
        {
            "csharp" or "cs" or "c#" => CSharp(),
            "razor" => Razor(),
            "xml" or "html" => Xml(),
            "yaml" or "yml" => Yaml(),
            "json" => Json(),
            "css" => Css(),
            "shell" or "bash" or "powershell" => Shell(),
            _ => null,
        };

        if (pattern is null)
        {
            return WebUtility.HtmlEncode(code);
        }

        var html = new StringBuilder(code.Length * 2);
        var last = 0;
        foreach (Match match in pattern.Matches(code))
        {
            var group = match.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success && !char.IsDigit(g.Name[0]));
            if (group is null)
            {
                continue;
            }

            html.Append(WebUtility.HtmlEncode(code[last..group.Index]));
            html.Append("<span class=\"tk-").Append(group.Name).Append("\">")
                .Append(WebUtility.HtmlEncode(group.Value))
                .Append("</span>");
            last = group.Index + group.Length;
        }

        html.Append(WebUtility.HtmlEncode(code[last..]));
        return html.ToString();
    }

    // c: comment, s: string, k: keyword, t: type, n: number, a: attribute or key, d: directive, g: tag.

    [GeneratedRegex(
        @"(?<c>//[^\n]*|/\*[\s\S]*?\*/)" +
        @"|(?<s>\$*""""""[\s\S]*?""""""|\$?@""(?:[^""]|"""")*""|@?\$""(?:[^""\\\n]|\\.)*""|""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)')" +
        @"|(?<d>^[ \t]*#(?:region|endregion|pragma|nullable|if|endif|else)[^\n]*)" +
        @"|(?<k>\b(?:" + Keywords + @")\b)" +
        @"|(?<t>\b[A-Z][A-Za-z0-9_]*\b)" +
        @"|(?<n>\b\d[\d_]*(?:\.\d+)?[mMdDfFLu]?\b)",
        RegexOptions.Multiline)]
    private static partial Regex CSharp();

    [GeneratedRegex(
        @"(?<c>@\*[\s\S]*?\*@|<!--[\s\S]*?-->|(?<![:""])//[^\n]*)" +
        @"|(?<d>@(?:page|inject|using|code|implements|inherits|attribute|rendermode|layout|typeparam|namespace)\b|@(?:bind-Value|bind|onclick|onchange|oninput|key|ref|formname)(?::\w+)?|@(?=[\w(]))" +
        @"|(?<g>(?<![\w)\]])</?[A-Za-z][\w.:-]*|(?<![=\-])/?>)" +
        @"|(?<s>""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)')" +
        @"|(?<a>(?<=\s)[\w:.-]+(?==""))" +
        @"|(?<k>\b(?:" + Keywords + @")\b)" +
        @"|(?<n>\b\d+(?:\.\d+)?\b)",
        RegexOptions.Multiline)]
    private static partial Regex Razor();

    [GeneratedRegex(
        @"(?<c><!--[\s\S]*?-->)" +
        @"|(?<g></?[A-Za-z][\w.:-]*|/?>)" +
        @"|(?<a>(?<=\s)[\w:.-]+(?==))" +
        @"|(?<s>""[^""]*""|'[^']*')")]
    private static partial Regex Xml();

    [GeneratedRegex(
        @"(?<c>(?<=^|\s)#[^\n]*)" +
        @"|(?<a>(?<=^[ \t]*(?:-[ \t]+)?)(?:'[^'\n]*'|""[^""\n]*""|[\w$./{}~-]+)(?=:(?:[ \t]|$)))" +
        @"|(?<s>'[^'\n]*'|""(?:[^""\\\n]|\\.)*"")" +
        @"|(?<k>(?<=:[ \t]+|-[ \t]+|\[|,[ \t]*)(?:true|false|null)\b)" +
        @"|(?<n>(?<=:[ \t]+|-[ \t]+)\d+(?:\.\d+)?\b)",
        RegexOptions.Multiline)]
    private static partial Regex Yaml();

    [GeneratedRegex(
        @"(?<a>""(?:[^""\\]|\\.)*""(?=\s*:))" +
        @"|(?<s>""(?:[^""\\]|\\.)*"")" +
        @"|(?<k>\b(?:true|false|null)\b)" +
        @"|(?<n>-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)")]
    private static partial Regex Json();

    [GeneratedRegex(
        @"(?<c>/\*[\s\S]*?\*/)" +
        @"|(?<s>""[^""]*""|'[^']*')" +
        @"|(?<a>(?<=[{;\s])[\w-]+(?=\s*:[^:]))" +
        @"|(?<n>-?\b\d+(?:\.\d+)?(?:px|rem|em|%|s|ms|vh|vw|fr)?\b|#[0-9a-fA-F]{3,8}\b)" +
        @"|(?<g>[.#]?[A-Za-z][\w-]*(?=[^{};]*\{))",
        RegexOptions.Multiline)]
    private static partial Regex Css();

    [GeneratedRegex(
        @"(?<c>(?<=^|\s)#[^\n]*)" +
        @"|(?<s>""[^""\n]*""|'[^'\n]*')" +
        @"|(?<k>^(?:dotnet|api-gen|curl|git|npm)\b)" +
        @"|(?<a>(?<=\s)--?[\w-]+)",
        RegexOptions.Multiline)]
    private static partial Regex Shell();
}
