using BlazorApi.OpenApi.Diagnostics;

namespace BlazorApi.OpenApi.Tests;

internal static class TestContracts
{
    public static string PathOf(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Contracts", relative.Replace('/', Path.DirectorySeparatorChar));

    public static OpenApiLoadResult LoadFile(string relative, OpenApiLoadOptions? options = null) =>
        OpenApiLoader.LoadFile(PathOf(relative), options);

    public static OpenApiLoadResult LoadYaml(string yaml, OpenApiLoadOptions? options = null) =>
        OpenApiLoader.Parse(yaml.Replace("\r\n", "\n"), "test.yaml", options);

    /// <summary>A minimal valid document with the given paths and components YAML (each indented as top-level blocks).</summary>
    public static string Document(string paths, string? components = null, string version = "3.0.3") =>
        $"openapi: {version}\ninfo:\n  title: T\n  version: '1'\npaths:\n{Indent(paths)}\n" +
        (components is null ? string.Empty : $"components:\n{Indent(components)}\n");

    public static IReadOnlyList<string> Ids(this OpenApiLoadResult result, DiagnosticSeverity minimum = DiagnosticSeverity.Warning) =>
        result.Diagnostics.Where(d => d.Severity >= minimum).Select(d => d.Id).ToArray();

    public static string Describe(this OpenApiLoadResult result) => string.Join("\n", result.Diagnostics);

    private static string Indent(string block) =>
        string.Join("\n", block.Replace("\r\n", "\n").Trim('\n').Split('\n').Select(l => "  " + l));
}
