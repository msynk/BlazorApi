namespace BlazorApi.Cli;

/// <summary>Parsed command-line arguments: a command, positional arguments and options (<c>--name value</c>).</summary>
internal sealed class CommandLine
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "--dry-run", "--check", "--strict", "--quiet", "--help", "-h", "--version",
    };

    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    private CommandLine(string? command, List<string> positional)
    {
        Command = command;
        Positional = positional;
    }

    public string? Command { get; }

    public List<string> Positional { get; }

    public List<string> Errors { get; } = [];

    public static CommandLine Parse(string[] args)
    {
        var positional = new List<string>();
        string? command = null;
        var options = new List<(string Name, string? Value)>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1)
            {
                var name = arg;
                string? value = null;
                var equals = arg.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0 && arg.StartsWith("--", StringComparison.Ordinal))
                {
                    name = arg.Substring(0, equals);
                    value = arg.Substring(equals + 1);
                }
                else if (!Flags.Contains(arg) && i + 1 < args.Length)
                {
                    value = args[++i];
                }

                options.Add((name, value));
            }
            else if (command is null)
            {
                command = arg;
            }
            else
            {
                positional.Add(arg);
            }
        }

        var line = new CommandLine(command, positional);
        foreach (var (name, value) in options)
        {
            if (!line._options.TryGetValue(name, out var values))
            {
                values = [];
                line._options[name] = values;
            }

            if (value is not null)
            {
                values.Add(value);
            }
            else if (!Flags.Contains(name))
            {
                line.Errors.Add($"Option '{name}' needs a value.");
            }
        }

        return line;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Value(string name) => _options.TryGetValue(name, out var values) && values.Count > 0 ? values[^1] : null;

    public IReadOnlyList<string> Values(string name) => _options.TryGetValue(name, out var values) ? values : [];

    public IEnumerable<string> OptionNames => _options.Keys;
}
