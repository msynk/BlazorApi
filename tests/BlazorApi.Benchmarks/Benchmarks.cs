using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BlazorApi.CodeGeneration;
using BlazorApi.OpenApi;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Model;
using BlazorApi.OpenApi.Syntax;
using Petstore.Client;

BenchmarkSwitcher.FromAssembly(typeof(EngineBenchmarks).Assembly).Run(args);

/// <summary>A synthetic contract large enough to show scaling: N schemas with 12 properties and N operations.</summary>
internal static class LargeContract
{
    public static string Create(int size)
    {
        var builder = new StringBuilder("openapi: 3.1.0\ninfo: {title: Large, version: '1'}\npaths:\n");
        for (var i = 0; i < size; i++)
        {
            builder.Append($"  /items{i}/{{id}}:\n    get:\n      operationId: getItem{i}\n      parameters: [{{name: id, in: path, required: true, schema: {{type: integer, format: int64}}}}, {{name: q, in: query, schema: {{type: string}}}}]\n      responses:\n        '200': {{description: ok, content: {{application/json: {{schema: {{$ref: '#/components/schemas/Item{i}'}}}}}}}}\n");
        }

        builder.Append("components:\n  schemas:\n");
        for (var i = 0; i < size; i++)
        {
            builder.Append($"    Item{i}:\n      type: object\n      required: [id, name]\n      properties:\n");
            builder.Append("        id: {type: integer, format: int64}\n        name: {type: string, maxLength: 100}\n        tags: {type: array, items: {type: string}}\n");
            builder.Append("        created: {type: string, format: date-time}\n        price: {type: number, format: double}\n        active: {type: boolean}\n");
            builder.Append("        status: {type: string, enum: [new, used, broken]}\n        note: {type: [string, 'null']}\n        meta: {type: object, additionalProperties: {type: string}}\n");
            builder.Append($"        next: {{$ref: '#/components/schemas/Item{(i + 1) % size}'}}\n        uuid: {{type: string, format: uuid}}\n        rank: {{type: integer}}\n");
        }

        return builder.ToString();
    }
}

[MemoryDiagnoser]
public class EngineBenchmarks
{
    private string _petstoreYaml = string.Empty;
    private string _petstoreJson = string.Empty;
    private string _large = string.Empty;
    private OpenApiLoadResult _loaded = null!;

    [Params(500)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _petstoreYaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "petstore.yaml"));
        _petstoreJson = SyntaxParser.Parse(_petstoreYaml, "petstore.yaml", DocumentFormat.Yaml, new DiagnosticBag())!.ToJson();
        _large = LargeContract.Create(Size);
        _loaded = OpenApiLoader.Parse(_large, "large.yaml");
    }

    [Benchmark]
    public DocNode? ParsePetstoreYaml() => SyntaxParser.Parse(_petstoreYaml, "petstore.yaml", DocumentFormat.Yaml, new DiagnosticBag());

    [Benchmark]
    public DocNode? ParsePetstoreJson() => SyntaxParser.Parse(_petstoreJson, "petstore.json", DocumentFormat.Json, new DiagnosticBag());

    [Benchmark]
    public OpenApiLoadResult LoadLargeContract() => OpenApiLoader.Parse(_large, "large.yaml");

    [Benchmark]
    public ApiModel BuildLargeModel() => ApiModelBuilder.Build(_loaded.Document!, new DiagnosticBag());

    [Benchmark]
    public GenerationResult GenerateLargeClient() => ApiClientGenerator.Generate(_loaded, new GeneratorOptions { Namespace = "Large", GenerateQueries = true });

    [Benchmark]
    public GenerationResult GeneratePetstoreClient() => ApiClientGenerator.Generate(_petstoreYaml, "petstore.yaml", new GeneratorOptions { Namespace = "Petstore", GenerateQueries = true, GenerateMocks = true });
}

[MemoryDiagnoser]
public class ClientBenchmarks
{
    private PetstoreClient _client = null!;
    private PetstoreClient _largePageClient = null!;

    [GlobalSetup]
    public void Setup()
    {
        const string dog = """{"id":42,"name":"Rex","kind":"dog","status":"available","birthDate":"2020-05-01","barkVolume":7.5,"owner":{"id":"7c9e6679-7425-40de-944b-e07fc1f90ae7"}}""";
        _client = new PetstoreClient(new HttpClient(new StaticHandler(dog)));
        var page = "{\"items\":[" + string.Join(",", Enumerable.Repeat(dog, 1000)) + "],\"nextCursor\":null}";
        _largePageClient = new PetstoreClient(new HttpClient(new StaticHandler(page)));
    }

    [Benchmark]
    public Task<Pet> GetPolymorphicPet() => _client.GetPetAsync(42);

    [Benchmark]
    public Task<PetPage> ListPetsWithParameters() => _largePageClient.ListPetsAsync(limit: 1000, cursor: "abc", tags: ["a", "b"], status: PetStatus.Available);

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly byte[] _body;

        public StaticHandler(string body) => _body = Encoding.UTF8.GetBytes(body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        }
    }
}
