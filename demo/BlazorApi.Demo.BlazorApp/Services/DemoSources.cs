using System.Reflection;

namespace Petstore.BlazorApp.Services;

/// <summary>A file of the samples, embedded in this assembly (see the "Sample sources" item group of the project).</summary>
public sealed record SampleFile(string Path, string Language)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public string Folder => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : string.Empty;
}

/// <summary>
/// The source files of the samples, read from embedded resources. The guide and the "How this page works" panels link
/// here, so what you read is exactly what runs.
/// </summary>
public sealed class SampleSources
{
    private const string Prefix = "sample/";
    private static readonly Assembly Assembly = typeof(SampleSources).Assembly;
    private readonly Dictionary<string, string> _resources;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public SampleSources()
    {
        _resources = Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .ToDictionary(n => n[Prefix.Length..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);
        Files = _resources.Keys
            .Select(p => new SampleFile(p, LanguageOf(p)))
            .OrderBy(f => f.Folder.Count(c => c == '/'))
            .ThenBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<SampleFile> Files { get; }

    public bool Exists(string path) => _resources.ContainsKey(path);

    /// <summary>The text of a file, or null when it is not embedded.</summary>
    public string? Read(string path)
    {
        if (!_resources.TryGetValue(path, out var resource))
        {
            return null;
        }

        lock (_lock)
        {
            if (!_cache.TryGetValue(path, out var text))
            {
                using var stream = Assembly.GetManifestResourceStream(resource)!;
                using var reader = new StreamReader(stream);
                _cache[path] = text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            }

            return text;
        }
    }

    /// <summary>The text of a file, which must exist.</summary>
    public string Get(string path) => Read(path) ?? throw new FileNotFoundException($"'{path}' is not an embedded sample file.");

    public static string LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".razor" => "razor",
        ".csproj" or ".props" or ".targets" or ".xml" => "xml",
        ".yaml" or ".yml" => "yaml",
        ".json" => "json",
        ".css" => "css",
        _ => "text",
    };
}
