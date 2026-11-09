namespace BlazorApi.CodeGeneration;

/// <summary>A generated source file.</summary>
public sealed class GeneratedFile
{
    /// <summary>Creates a file.</summary>
    public GeneratedFile(string path, string content)
    {
        Path = path;
        Content = content;
    }

    /// <summary>The relative path, with <c>/</c> separators (for example <c>Models/Pet.g.cs</c>).</summary>
    public string Path { get; }

    /// <summary>The file content (UTF-8, <c>\n</c> line endings).</summary>
    public string Content { get; }

    /// <inheritdoc />
    public override string ToString() => Path;
}
