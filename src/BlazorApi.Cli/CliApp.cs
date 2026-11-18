using System.Globalization;
using System.Reflection;
using System.Text;
using BlazorApi.CodeGeneration;
using BlazorApi.CodeGeneration.Configuration;
using BlazorApi.OpenApi;
using BlazorApi.OpenApi.Compatibility;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Model;
using BlazorApi.OpenApi.Syntax;

namespace BlazorApi.Cli;

/// <summary>The api-gen command-line application. Exit codes: 0 success, 1 failure (errors, out of date, breaking changes), 2 usage error.</summary>
internal sealed class CliApp
{
    private const string ManifestName = ".blazorapi-manifest";
    private const string DefaultConfig = "blazorapi.json";

    private readonly TextWriter _out;
    private readonly TextWriter _error;
    private readonly string _directory;
    private readonly HttpMessageHandler? _httpHandler;
    private HttpClient? _http;

    public CliApp(TextWriter output, TextWriter error, string currentDirectory, HttpMessageHandler? httpHandler = null)
    {
        _out = output;
        _error = error;
        _directory = currentDirectory;
        _httpHandler = httpHandler;
    }

    /// <summary>Stops <c>generate --watch</c> (Ctrl+C in the console).</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>How long changes must settle before <c>--watch</c> regenerates.</summary>
    internal TimeSpan WatchDebounce { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Loads a contract from a file, or downloads it (with the documents it references on the same origin) when it is
    /// an http or https URL.
    /// </summary>
    private OpenApiLoadResult Load(string contract, OpenApiLoadOptions? options)
    {
        options ??= OpenApiLoadOptions.Default;
        if (!RemoteDocumentResolver.IsUrl(contract))
        {
            return OpenApiLoader.LoadFile(contract, options);
        }

        _http ??= _httpHandler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) } // Redirects could leave the origin.
            : new HttpClient(_httpHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        var uri = new Uri(contract);
        var resolver = new RemoteDocumentResolver(_http, uri);
        if (!resolver.TryRead(uri, out var text, out var error))
        {
            throw new IOException($"Could not download '{contract}': {error}");
        }

        var policy = options.ExternalReferences == ExternalReferencePolicy.Disabled ? ExternalReferencePolicy.Disabled : ExternalReferencePolicy.LocalFilesAndRemote;
        return OpenApiLoader.Parse(text, uri.AbsoluteUri, options with { DocumentResolver = resolver, ExternalReferences = policy, ReferenceRoot = null });
    }

    /// <summary>Reads overlay files (missing files fail the command with an I/O error).</summary>
    private static IReadOnlyList<OpenApi.Overlays.OpenApiOverlay> ReadOverlays(IEnumerable<string> paths) =>
        paths.Select(p => new OpenApi.Overlays.OpenApiOverlay(p, File.ReadAllText(p))).ToArray();

    /// <summary>A contract argument as an absolute path, or unchanged when it is a URL.</summary>
    private static string ContractPath(string baseDirectory, string contract) =>
        RemoteDocumentResolver.IsUrl(contract) ? contract : Path.GetFullPath(Path.Combine(baseDirectory, contract));

    private static string ContractName(string contract)
    {
        var name = Path.GetFileNameWithoutExtension(RemoteDocumentResolver.IsUrl(contract) ? new Uri(contract).AbsolutePath : contract);
        return string.IsNullOrEmpty(name) ? "Api" : name;
    }

    public int Run(string[] args)
    {
        var line = CommandLine.Parse(args);
        if (line.Has("--version"))
        {
            _out.WriteLine(Version);
            return 0;
        }

        if (line.Command is null || line.Has("--help") || line.Has("-h") || line.Command == "help")
        {
            _out.WriteLine(Help);
            return line.Command is null && !line.Has("--help") && !line.Has("-h") ? 2 : 0;
        }

        if (line.Errors.Count > 0)
        {
            foreach (var problem in line.Errors)
            {
                _error.WriteLine("error: " + problem);
            }

            return 2;
        }

        try
        {
            return line.Command switch
            {
                "generate" when line.Has("--watch") => Watch(line),
                "generate" => Generate(line),
                "validate" => Validate(line),
                "diff" => Diff(line),
                "inspect" => Inspect(line),
                "mock" => Mock(line),
                _ => Usage($"Unknown command '{line.Command}'."),
            };
        }
        catch (IOException ex)
        {
            _error.WriteLine("error: " + ex.Message);
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            _error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    // ------------------------------------------------------------------ generate

    private int Generate(CommandLine line)
    {
        var jobs = ResolveJobs(line, out var configDiagnostics);
        if (jobs is null)
        {
            Print(configDiagnostics, line.Has("--quiet"));
            return configDiagnostics.Any(d => d.IsError) ? 1 : 2;
        }

        var failed = false;
        var outOfDate = false;
        foreach (var job in jobs)
        {
            var result = ApiClientGenerator.Generate(Load(job.Contract, job.LoadOptions), job.Configuration.Options);
            var diagnostics = configDiagnostics.Concat(result.Diagnostics).ToList();
            Print(diagnostics, line.Has("--quiet"));
            if (result.HasErrors || diagnostics.Any(d => d.IsError))
            {
                failed = true;
                continue;
            }

            var changes = Plan(job.Output, result.Files);
            if (line.Has("--dry-run") || line.Has("--check"))
            {
                foreach (var change in changes.Where(c => c.Action != "unchanged"))
                {
                    _out.WriteLine($"{change.Action,-9} {Path.GetRelativePath(_directory, change.Path)}");
                }

                var pending = changes.Count(c => c.Action != "unchanged");
                _out.WriteLine(pending == 0
                    ? $"{Relative(job.Output)}: up to date ({result.Files.Count} files)."
                    : $"{Relative(job.Output)}: {pending} file(s) would change.");
                outOfDate |= pending > 0;
                continue;
            }

            Apply(job.Output, changes, result.Files);
            var written = changes.Count(c => c.Action is "create" or "update");
            var deleted = changes.Count(c => c.Action == "delete");
            if (!line.Has("--quiet"))
            {
                _out.WriteLine($"Generated {result.Files.Count} files from {Relative(job.Contract)} into {Relative(job.Output)} ({written} written, {deleted} removed).");
            }
        }

        if (failed)
        {
            return 1;
        }

        if (line.Has("--check") && outOfDate)
        {
            _error.WriteLine("error: generated code is out of date; run 'api-gen generate' and commit the result.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Generates, then regenerates whenever a JSON or YAML file changes in the directories of the contracts, the
    /// configuration, the overlays and the reference roots, until cancelled. Generation errors are printed and
    /// watching continues.
    /// </summary>
    private int Watch(CommandLine line)
    {
        if (line.Has("--check") || line.Has("--dry-run"))
        {
            return Usage("--watch cannot be combined with --check or --dry-run.");
        }

        var exit = Generate(line);
        var jobs = ResolveJobs(line, out _);
        if (jobs is null)
        {
            return exit;
        }

        var directories = jobs
            .SelectMany(j => new[] { j.Contract, j.LoadOptions.ReferenceRoot is { } root ? Path.Combine(root, "x") : null }
                .Concat(j.LoadOptions.Overlays.Select(o => o.Path)))
            .Where(p => p is not null && !RemoteDocumentResolver.IsUrl(p))
            .Select(p => Path.GetDirectoryName(Path.GetFullPath(p!))!)
            .Concat(line.Value("--config") is { } config ? [Path.GetDirectoryName(Path.GetFullPath(Path.Combine(_directory, config)))!] : [])
            .Concat(File.Exists(Path.Combine(_directory, DefaultConfig)) ? [_directory] : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToList();
        // Nested directories are covered by their parents.
        directories = directories.Where(d => !directories.Any(o => o != d && d.StartsWith(o.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();

        using var changed = new SemaphoreSlim(0);
        var watchers = new List<FileSystemWatcher>();
        try
        {
            foreach (var directory in directories)
            {
                var watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                void OnChange(object sender, FileSystemEventArgs e)
                {
                    if (Path.GetExtension(e.FullPath).ToLowerInvariant() is ".json" or ".yaml" or ".yml")
                    {
                        changed.Release();
                    }
                }

                watcher.Changed += OnChange;
                watcher.Created += OnChange;
                watcher.Renamed += (s, e) => OnChange(s, e);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }

            _out.WriteLine($"Watching {string.Join(", ", directories.Select(Relative))} for changes (Ctrl+C to stop).");
            while (true)
            {
                try
                {
                    changed.Wait(Cancellation);
                    // Let a burst of events (editors write several times) settle, then drain it.
                    Task.Delay(WatchDebounce, Cancellation).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return exit;
                }

                while (changed.CurrentCount > 0)
                {
                    changed.Wait(0);
                }

                _out.WriteLine("Change detected; regenerating.");
                try
                {
                    exit = Generate(line);
                }
                catch (IOException ex)
                {
                    // A file that is still being written; the next change event retries.
                    _error.WriteLine("error: " + ex.Message);
                    exit = 1;
                }
            }
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
        }
    }

    private sealed record Job(string Contract, string Output, GenerationConfiguration Configuration, OpenApiLoadOptions LoadOptions);

    private sealed record FileChange(string Action, string Path);

    private List<Job>? ResolveJobs(CommandLine line, out List<ApiDiagnostic> diagnostics)
    {
        diagnostics = [];
        var bag = new DiagnosticBag();
        var configPath = line.Value("--config");
        var contractArgument = line.Positional.FirstOrDefault();
        if (configPath is null && contractArgument is null && File.Exists(Path.Combine(_directory, DefaultConfig)))
        {
            configPath = DefaultConfig;
        }

        var configurations = new List<(GenerationConfiguration Configuration, string BaseDirectory)>();
        if (configPath is not null)
        {
            var fullConfig = Path.GetFullPath(Path.Combine(_directory, configPath));
            var text = File.ReadAllText(fullConfig);
            var baseDirectory = Path.GetDirectoryName(fullConfig)!;
            var root = SyntaxParser.Parse(text, fullConfig, DocumentFormat.Json, bag);
            if (root is ObjectNode obj && obj["contracts"] is ArrayNode contracts)
            {
                // Top-level settings are defaults for every entry.
                var defaults = GeneratorOptionsReader.ReadJson(text, fullConfig, bag);
                foreach (var entry in contracts.Items)
                {
                    configurations.Add((GeneratorOptionsReader.ReadJson(entry.ToJson(), fullConfig, bag, defaults with { Contract = null, Output = null }), baseDirectory));
                }
            }
            else if (root is not null)
            {
                configurations.Add((GeneratorOptionsReader.ReadJson(text, fullConfig, bag), baseDirectory));
            }
        }
        else if (contractArgument is not null)
        {
            configurations.Add((new GenerationConfiguration(), _directory));
        }
        else
        {
            diagnostics.AddRange(bag);
            Usage($"Give a contract path or a configuration file (--config, or {DefaultConfig} in the current directory).");
            return null;
        }

        var jobs = new List<Job>();
        foreach (var (configuration, baseDirectory) in configurations)
        {
            var options = configuration.Options;
            if (line.Value("--namespace") is { } ns)
            {
                options = options with { Namespace = ns };
            }

            if (line.Value("--client-name") is { } clientName)
            {
                options = options with { ClientName = clientName };
            }

            foreach (var pair in line.Values("--option"))
            {
                options = GeneratorOptionsReader.ApplyPairs(options, pair, bag);
            }

            if (line.Has("--strict"))
            {
                options = options with { TreatWarningsAsErrors = true };
            }

            var contract = contractArgument is not null && configurations.Count == 1
                ? ContractPath(_directory, contractArgument)
                : configuration.Contract is { } relative ? ContractPath(baseDirectory, relative) : null;
            if (contract is null)
            {
                bag.Report(CodeGenerationDiagnostics.InvalidConfiguration, new SourceLocation(configPath, 0, 0), null, "The configuration does not name a 'contract'");
                continue;
            }

            if (options.Namespace == new GeneratorOptions().Namespace && configuration.Options.Namespace == options.Namespace && line.Value("--namespace") is null)
            {
                options = options with { Namespace = CodeGeneration.Naming.Identifier.Pascal(ContractName(contract)) };
            }

            var output = line.Value("--output") is { } outputArgument
                ? Path.GetFullPath(Path.Combine(_directory, outputArgument))
                : Path.GetFullPath(Path.Combine(baseDirectory, configuration.Output ?? "Generated"));
            var loadOptions = new OpenApiLoadOptions
            {
                ExternalReferences = configuration.ExternalReferences,
                ReferenceRoot = configuration.ReferenceRoot is { } root ? Path.GetFullPath(Path.Combine(baseDirectory, root)) : null,
                SeverityOverrides = options.SeverityOverrides,
                Overlays = ReadOverlays(configuration.Overlays.Select(o => Path.GetFullPath(Path.Combine(baseDirectory, o)))
                    .Concat(line.Values("--overlay").Select(o => Path.GetFullPath(Path.Combine(_directory, o))))),
            };
            jobs.Add(new Job(contract, output, configuration with { Options = options }, loadOptions));
        }

        diagnostics.AddRange(bag);
        return bag.HasErrors ? null : jobs;
    }

    private static List<FileChange> Plan(string output, IReadOnlyList<GeneratedFile> files)
    {
        var changes = new List<FileChange>();
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(output) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            var path = Path.GetFullPath(Path.Combine(output, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"The generated path '{file.Path}' leaves the output directory.");
            }

            generated.Add(path);
            if (!File.Exists(path))
            {
                changes.Add(new FileChange("create", path));
            }
            else
            {
                var existing = File.ReadAllText(path).Replace("\r\n", "\n");
                changes.Add(new FileChange(existing == file.Content ? "unchanged" : "update", path));
            }
        }

        var manifest = Path.Combine(output, ManifestName);
        if (File.Exists(manifest))
        {
            foreach (var entry in File.ReadAllLines(manifest).Where(l => l.Length > 0))
            {
                var path = Path.GetFullPath(Path.Combine(output, entry));
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !generated.Contains(path) && File.Exists(path))
                {
                    changes.Add(new FileChange("delete", path));
                }
            }
        }

        return changes;
    }

    private static void Apply(string output, List<FileChange> changes, IReadOnlyList<GeneratedFile> files)
    {
        Directory.CreateDirectory(output);
        var contents = files.ToDictionary(
            f => Path.GetFullPath(Path.Combine(output, f.Path.Replace('/', Path.DirectorySeparatorChar))),
            f => f.Content,
            StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            switch (change.Action)
            {
                case "create":
                case "update":
                    Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
                    File.WriteAllText(change.Path, contents[change.Path], new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    break;
                case "delete":
                    File.Delete(change.Path);
                    break;
            }
        }

        File.WriteAllLines(Path.Combine(output, ManifestName), files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ validate

    private int Validate(CommandLine line)
    {
        // Without contract paths (or with one contract and --config), the configuration file supplies the contracts,
        // reference policies, options and severity overrides, exactly as for 'generate'.
        var targets = new List<(string Path, GeneratorOptions Options, OpenApiLoadOptions? LoadOptions)>();
        if (line.Positional.Count == 0 || (line.Positional.Count == 1 && line.Value("--config") is not null))
        {
            var jobs = ResolveJobs(line, out var configDiagnostics);
            if (jobs is null)
            {
                Print(configDiagnostics, quiet: false);
                return configDiagnostics.Any(d => d.IsError) ? 1 : 2;
            }

            Print(configDiagnostics, quiet: false);
            targets.AddRange(jobs.Select(j => (j.Contract, j.Configuration.Options, (OpenApiLoadOptions?)j.LoadOptions)));
        }
        else
        {
            if (line.Value("--config") is not null)
            {
                return Usage("validate takes at most one contract path with --config.");
            }

            var optionDiagnostics = new DiagnosticBag();
            var options = new GeneratorOptions { TreatWarningsAsErrors = line.Has("--strict") };
            foreach (var pair in line.Values("--option"))
            {
                options = GeneratorOptionsReader.ApplyPairs(options, pair, optionDiagnostics);
            }

            if (optionDiagnostics.Count > 0)
            {
                Print(optionDiagnostics, quiet: false);
                if (optionDiagnostics.HasErrors)
                {
                    return 2;
                }
            }

            var overlays = ReadOverlays(line.Values("--overlay").Select(o => Path.GetFullPath(Path.Combine(_directory, o))));
            var loadOptions = overlays.Count == 0 ? null : new OpenApiLoadOptions { Overlays = overlays };
            targets.AddRange(line.Positional.Select(c => (ContractPath(_directory, c), options, loadOptions)));
        }

        var exit = 0;
        var reports = new List<string>();
        foreach (var (path, options, loadOptions) in targets)
        {
            var result = ApiClientGenerator.Generate(Load(path, loadOptions), options);
            var errors = result.Diagnostics.Count(d => d.IsError);
            var warnings = result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            if (line.Value("--format") == "json")
            {
                reports.Add(DiagnosticsJson(path, result.Diagnostics));
            }
            else
            {
                Print(result.Diagnostics, quiet: false);
                _out.WriteLine($"{Relative(path)}: {errors} error(s), {warnings} warning(s), {result.Model?.Operations.Count ?? 0} operation(s), {result.Model?.Types.Count ?? 0} type(s).");
            }

            if (errors > 0)
            {
                exit = 1;
            }
        }

        if (reports.Count > 0)
        {
            _out.WriteLine(reports.Count == 1 ? reports[0] : "[" + string.Join(",", reports) + "]");
        }

        return exit;
    }

    // ------------------------------------------------------------------ diff

    private int Diff(CommandLine line)
    {
        if (line.Positional.Count != 2)
        {
            return Usage("diff needs the previous and the current contract.");
        }

        var previous = Model(line.Positional[0], out var previousDiagnostics);
        var current = Model(line.Positional[1], out var currentDiagnostics);
        if (previous is null || current is null)
        {
            Print(previousDiagnostics.Concat(currentDiagnostics), quiet: false);
            return 1;
        }

        var report = ContractComparer.Compare(previous, current);
        var ignore = line.Values("--ignore").SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        report = report.Without(ignore);
        var text = line.Value("--format") switch
        {
            "json" => report.ToJson(),
            "markdown" or "md" => report.ToMarkdown(),
            _ => report.ToText(),
        };
        if (line.Value("--report") is { } reportPath)
        {
            File.WriteAllText(Path.GetFullPath(Path.Combine(_directory, reportPath)), text);
        }

        _out.WriteLine(text);
        ChangeSeverity? failOn;
        switch (line.Value("--fail-on") ?? "breaking")
        {
            case "none": failOn = null; break;
            case "non-breaking" or "any": failOn = ChangeSeverity.NonBreaking; break;
            case "potentially-breaking" or "potential": failOn = ChangeSeverity.PotentiallyBreaking; break;
            case "breaking": failOn = ChangeSeverity.Breaking; break;
            default: return Usage($"Unknown --fail-on value '{line.Value("--fail-on")}'; use breaking, potentially-breaking, non-breaking or none.");
        }
        return failOn is { } level && report.HasChangesAtLeast(level) ? 1 : 0;
    }

    private ApiModel? Model(string contract, out List<ApiDiagnostic> diagnostics)
    {
        var loaded = Load(ContractPath(_directory, contract), new OpenApiLoadOptions { Validate = false });
        diagnostics = loaded.Diagnostics.Where(d => d.IsError).ToList();
        if (loaded.Document is null || loaded.HasErrors)
        {
            return null;
        }

        return ApiModelBuilder.Build(loaded.Document, new DiagnosticBag());
    }

    // ------------------------------------------------------------------ mock

    /// <summary>Raised with the bound address once the mock server listens (tests use port 0).</summary>
    internal Action<System.Net.IPEndPoint>? MockStarted { get; init; }

    private int Mock(CommandLine line)
    {
        if (line.Positional.Count != 1)
        {
            return Usage("mock needs a contract path.");
        }

        var portText = line.Value("--port") ?? "4010";
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
        {
            return Usage($"'{portText}' is not a valid port.");
        }

        var overlays = ReadOverlays(line.Values("--overlay").Select(o => Path.GetFullPath(Path.Combine(_directory, o))));
        var loaded = Load(ContractPath(_directory, line.Positional[0]), new OpenApiLoadOptions { Overlays = overlays });
        Print(loaded.Diagnostics.Where(d => d.IsError), quiet: false);
        if (loaded.Document is null || loaded.HasErrors)
        {
            return 1;
        }

        var model = ApiModelBuilder.Build(loaded.Document, new DiagnosticBag());
        var host = line.Value("--host") ?? "localhost";
        var address = host == "localhost" ? System.Net.IPAddress.Loopback
            : System.Net.IPAddress.TryParse(host, out var parsed) ? parsed : null;
        if (address is null)
        {
            return Usage($"'{host}' is not 'localhost' or an IP address.");
        }

        var listener = new System.Net.Sockets.TcpListener(address, port);
        try
        {
            listener.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            _error.WriteLine($"error: cannot listen on {host}:{port}: {ex.Message}");
            return 1;
        }

        var endpoint = (System.Net.IPEndPoint)listener.LocalEndpoint;
        var listeners = new List<System.Net.Sockets.TcpListener> { listener };
        if (host == "localhost" && System.Net.Sockets.Socket.OSSupportsIPv6)
        {
            // 'localhost' resolves to ::1 first on many systems; answer there too instead of letting clients time out.
            var v6 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Loopback, endpoint.Port);
            try
            {
                v6.Start();
                listeners.Add(v6);
            }
            catch (System.Net.Sockets.SocketException)
            {
                // IPv6 loopback unavailable or the port is taken there: IPv4 only.
            }
        }

        _out.WriteLine($"Mocking {model.Title} ({model.Operations.Count} operations) on http://{(host == "localhost" ? "localhost" : endpoint.Address.ToString())}:{endpoint.Port}/ (Ctrl+C to stop).");
        _out.WriteLine("Choose a response with the header 'Prefer: code=404' or 'Prefer: example=<name>'.");
        MockStarted?.Invoke(endpoint);
        var loopback = System.Net.IPAddress.IsLoopback(address);
        if (!loopback)
        {
            _error.WriteLine($"warning: the mock server listens on {host}, reachable from other machines, with open CORS.");
        }

        var server = new MockServer(model, _out, loopback ? ["localhost", "127.0.0.1", "[::1]"] : null, validate: !line.Has("--no-validate"));
        Task.WhenAll(listeners.Select(l => server.RunAsync(l, Cancellation))).GetAwaiter().GetResult();
        return 0;
    }

    // ------------------------------------------------------------------ inspect

    private int Inspect(CommandLine line)
    {
        if (line.Positional.Count != 1)
        {
            return Usage("inspect needs a contract path.");
        }

        var model = Model(line.Positional[0], out var diagnostics);
        if (model is null)
        {
            Print(diagnostics, quiet: false);
            return 1;
        }

        if (line.Value("--format") == "json")
        {
            _out.WriteLine(InspectJson(model));
            return 0;
        }

        _out.WriteLine($"{model.Title} {model.Version} (OpenAPI {model.Document.OpenApiVersion})");
        foreach (var server in model.Servers)
        {
            _out.WriteLine($"  server  {server.DefaultUrl}");
        }

        _out.WriteLine();
        _out.WriteLine($"Operations ({model.Operations.Count})");
        foreach (var operation in model.Operations)
        {
            var success = operation.SuccessResponses.SelectMany(r => r.Contents).FirstOrDefault();
            var returns = success?.Type?.ToString() ?? (success is null ? "-" : success.Kind.ToString().ToLowerInvariant());
            var flags = new List<string>();
            if (operation.Pagination is { } pagination)
            {
                flags.Add("paged:" + pagination.Style.ToString().ToLowerInvariant());
            }

            if (operation.Security.Count > 0)
            {
                flags.Add("auth:" + string.Join("|", operation.Security.Select(r => r.IsAnonymous ? "anonymous" : string.Join("+", r.Items.Select(i => i.Scheme.Name)))));
            }

            if (operation.Deprecated)
            {
                flags.Add("deprecated");
            }

            _out.WriteLine($"  {operation.Method,-7} {operation.Path,-40} {operation.OperationId ?? "-",-24} -> {returns}{(flags.Count > 0 ? "  [" + string.Join(", ", flags) + "]" : string.Empty)}");
        }

        _out.WriteLine();
        _out.WriteLine($"Types ({model.Types.Count}): {model.Types.OfType<ApiObjectType>().Count()} objects, {model.Types.OfType<ApiEnumType>().Count()} enums, {model.Types.OfType<ApiUnionType>().Count()} unions");
        foreach (var type in model.Types)
        {
            var detail = type switch
            {
                ApiObjectType o => $"object, {o.Properties.Count} properties" + (o.BaseType is null ? string.Empty : $", extends {o.BaseType.Name}") + (o.Discriminator is null ? string.Empty : $", discriminator '{o.Discriminator.PropertyName}'"),
                ApiEnumType e => $"enum of {e.Members.Count} {e.UnderlyingKind.ToString().ToLowerInvariant()} values",
                ApiUnionType u => $"{u.Kind.ToString().ToLowerInvariant()} of {u.Variants.Count}, dispatch by {u.Dispatch.ToString().ToLowerInvariant()}",
                _ => string.Empty,
            };
            _out.WriteLine($"  {type.Name,-40} {detail}");
        }

        if (model.SecuritySchemes.Count > 0)
        {
            _out.WriteLine();
            _out.WriteLine("Security schemes");
            foreach (var scheme in model.SecuritySchemes)
            {
                _out.WriteLine($"  {scheme.Name,-24} {scheme.Type}{(scheme.Source.Scheme is { } s ? " " + s : string.Empty)}");
            }
        }

        return 0;
    }

    private static string InspectJson(ApiModel model)
    {
        var builder = new StringBuilder("{\"title\":");
        DocNodeJson(builder, model.Title);
        builder.Append(",\"version\":");
        DocNodeJson(builder, model.Version);
        builder.Append(",\"operations\":[");
        builder.Append(string.Join(",", model.Operations.Select(o =>
        {
            var item = new StringBuilder("{\"method\":");
            DocNodeJson(item, o.Method);
            item.Append(",\"path\":");
            DocNodeJson(item, o.Path);
            item.Append(",\"operationId\":");
            if (o.OperationId is null)
            {
                item.Append("null");
            }
            else
            {
                DocNodeJson(item, o.OperationId);
            }

            item.Append(",\"pagination\":").Append(o.Pagination is null ? "null" : "\"" + o.Pagination.Style.ToString().ToLowerInvariant() + "\"");
            return item.Append('}').ToString();
        })));
        builder.Append("],\"types\":[");
        builder.Append(string.Join(",", model.Types.Select(t =>
        {
            var item = new StringBuilder("{\"name\":");
            DocNodeJson(item, t.Name);
            item.Append(",\"kind\":\"").Append(t switch { ApiObjectType => "object", ApiEnumType => "enum", _ => "union" }).Append("\"}");
            return item.ToString();
        })));
        return builder.Append("]}").ToString();
    }

    // ------------------------------------------------------------------ helpers

    private void Print(IEnumerable<ApiDiagnostic> diagnostics, bool quiet)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (quiet && diagnostic.Severity == DiagnosticSeverity.Info)
            {
                continue;
            }

            _error.WriteLine(diagnostic.ToString());
        }
    }

    private static string DiagnosticsJson(string path, IReadOnlyList<ApiDiagnostic> diagnostics)
    {
        var builder = new StringBuilder("{\"contract\":");
        DocNodeJson(builder, path);
        builder.Append(",\"errors\":").Append(diagnostics.Count(d => d.IsError).ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"warnings\":").Append(diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning).ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"diagnostics\":[");
        builder.Append(string.Join(",", diagnostics.Select(d =>
        {
            var item = new StringBuilder("{\"id\":\"").Append(d.Id).Append("\",\"severity\":\"").Append(d.Severity.ToString().ToLowerInvariant()).Append("\",\"message\":");
            DocNodeJson(item, d.Message);
            item.Append(",\"file\":");
            DocNodeJson(item, d.Location.Source ?? string.Empty);
            item.Append(",\"line\":").Append(d.Location.Line.ToString(CultureInfo.InvariantCulture));
            item.Append(",\"column\":").Append(d.Location.Column.ToString(CultureInfo.InvariantCulture));
            item.Append(",\"pointer\":");
            if (d.Pointer is null)
            {
                item.Append("null");
            }
            else
            {
                DocNodeJson(item, d.Pointer);
            }

            return item.Append('}').ToString();
        })));
        return builder.Append("]}").ToString();
    }

    private static void DocNodeJson(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            if (c == '"' || c == (char)92)
            {
                builder.Append((char)92).Append(c);
            }
            else if (c < (char)0x20)
            {
                builder.Append((char)92).Append('u').Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        builder.Append('"');
    }

    private string Relative(string path) => RemoteDocumentResolver.IsUrl(path) ? path : Path.GetRelativePath(_directory, path);

    private int Usage(string message)
    {
        _error.WriteLine("error: " + message);
        _error.WriteLine("Run 'api-gen --help' for usage.");
        return 2;
    }

    private static string Version =>
        typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private const string Help = """
        api-gen: typed C# clients from OpenAPI 3.0/3.1 contracts.
        Contracts are file paths or http(s) URLs (documents referenced on the same origin are downloaded too).

        Usage:
          api-gen generate [<contract>] [--config <blazorapi.json>] [--output <dir>] [--namespace <ns>]
                           [--client-name <name>] [--option key=value]... [--overlay <file>]... [--dry-run] [--check]
                           [--strict] [--quiet] [--watch]
          api-gen validate [<contract>...] [--config <blazorapi.json>] [--strict] [--format text|json]
                           [--option key=value]... [--overlay <file>]...
          api-gen diff <previous> <current> [--format text|markdown|json] [--fail-on breaking|potentially-breaking|non-breaking|none]
                       [--ignore <kind>[,<kind>...]] [--report <file>]
          api-gen inspect <contract> [--format text|json]
          api-gen mock <contract> [--port 4010] [--host localhost] [--overlay <file>]... [--no-validate]

        --overlay applies an OpenAPI Overlay (1.0-1.2) to the contract before it is read; repeatable, applied in order
                  after the configuration's "overlays".
        generate  Writes the generated files (stale files from the previous run are removed). Without arguments it reads
                  blazorapi.json in the current directory, which may list several contracts under "contracts".
                  --dry-run lists the changes without writing; --check fails when the output is out of date (for CI);
                  --watch regenerates whenever a contract, overlay or configuration file changes (Ctrl+C stops).
        validate  Loads, validates and plans generation, reporting every diagnostic. Fails on errors.
        diff      Reports changes between two versions of a contract and fails on breaking changes (by default).
        inspect   Summarizes operations, types, pagination and security.
        mock      Serves the contract's examples (or values synthesized from schemas) over HTTP with open CORS, for
                  front-end development; 'Prefer: code=404' or 'Prefer: example=name' choose the response. Requests
                  are validated first (401 without credentials, 415 for other content types, 400 listing the problems);
                  --no-validate turns that off.

        Exit codes: 0 success, 1 failure, 2 usage error.
        """;
}
