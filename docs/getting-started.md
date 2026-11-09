# Getting started

This guide generates a typed client for an OpenAPI contract and calls it from a .NET or Blazor application.

## Requirements

- .NET 8 SDK or later. The source generator targets Roslyn 4.8 (the .NET 8 SDK compiler).
- A project targeting `net8.0` or `net10.0` (the runtime packages target both). Generated code uses C# 12
  features, which is the default language version for these frameworks.
- An OpenAPI 3.0 or 3.1 document in JSON or YAML. Swagger 2.0 documents must be converted first
  (see [OpenAPI support](openapi-support.md)).

## Installing the packages

The packages are not on nuget.org yet. Either pack them into a local feed, or reference the projects directly.

**Local feed.** From a clone of the repository:

```shell
dotnet pack src/BlazorApi.Runtime -c Release -o artifacts
dotnet pack src/BlazorApi.SourceGenerator -c Release -o artifacts
dotnet pack src/BlazorApi.Cli -c Release -o artifacts          # optional: the api-gen tool
```

Add the folder as a package source (for example in `nuget.config`) and reference the packages:

```xml
<ItemGroup>
  <PackageReference Include="Msynk.BlazorApi.SourceGenerator" Version="0.1.0-preview" PrivateAssets="all" />
  <PackageReference Include="Msynk.BlazorApi.Runtime" Version="0.1.0-preview" />
</ItemGroup>
```

The source generator package contains only the analyzer and its MSBuild files. The generated code needs
`Msynk.BlazorApi.Runtime`, so always reference it as well (the generator package does not declare the dependency).

**Project references.** This is what the samples in `samples/` do: import the generator's MSBuild files and reference
the projects.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\BlazorApi\src\BlazorApi.SourceGenerator\build\Msynk.BlazorApi.SourceGenerator.props" />
  <!-- ... -->
  <ItemGroup>
    <ProjectReference Include="..\BlazorApi\src\BlazorApi.SourceGenerator\BlazorApi.SourceGenerator.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    <ProjectReference Include="..\BlazorApi\src\BlazorApi.Runtime\BlazorApi.Runtime.csproj" />
  </ItemGroup>
  <Import Project="..\BlazorApi\src\BlazorApi.SourceGenerator\build\Msynk.BlazorApi.SourceGenerator.targets" />
</Project>
```

## Adding a contract

Add the contract as an `OpenApiContract` item:

```xml
<ItemGroup>
  <OpenApiContract Include="Contracts/petstore.yaml" Namespace="MyApp.Petstore" />
</ItemGroup>
```

Build the project. For a contract titled `Petstore` the generator produces, in namespace `MyApp.Petstore`:

- `IPetstoreClient` and `PetstoreClient`, with two methods per operation (`GetPetAsync`, `GetPetResultAsync`, ...);
- one class per schema (`Pet`, `Dog`, `NewPet`, ...), enums and their JSON converters;
- `PetstoreClientServiceCollectionExtensions.AddPetstoreClient(...)`;
- `PetstoreClientPagination` with page walkers when operations are paginated.

Without `Namespace`, the namespace is `$(RootNamespace).<ContractFileName>` (for example `MyApp.Petstore`). See
[Source generator](source-generator.md) for every item and metadata, and [Configuration](configuration.md) for options.

## Registering the client

With dependency injection (ASP.NET Core, Blazor Server or WebAssembly, worker services):

```csharp
builder.Services
    .AddPetstoreClient(options =>
    {
        options.BaseAddress = new Uri("https://api.example.com/v1/");
        options.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddBearerToken(_ => new StaticAccessTokenProvider("dev-token"), scheme: "bearerAuth");
```

`AddPetstoreClient` registers `IPetstoreClient` as a typed `HttpClient` client and returns the `IHttpClientBuilder`, so
authentication and other message handlers can be added. Without `BaseAddress`, the client uses
`HttpClient.BaseAddress`, then the first server URL of the contract (`PetstoreClient.DefaultBaseAddress`).

Without dependency injection:

```csharp
using var http = new HttpClient();
var api = new PetstoreClient(http, new ApiClientOptions { BaseAddress = new Uri("https://api.example.com/v1/") });
```

## Calling operations

```csharp
// Throws ApiException (or ApiNetworkException, ApiTimeoutException, ApiDeserializationException) on failure.
Pet pet = await api.GetPetAsync(42, cancellationToken);

// Returns the outcome instead of throwing.
ApiResult<Pet> result = await api.GetPetResultAsync(42, cancellationToken);
if (!result.IsSuccess)
{
    logger.LogWarning("getPet failed: {Kind} {Status}", result.Error.Kind, result.StatusCode);
}

// Paginated operations get IAsyncEnumerable helpers.
await foreach (Pet item in api.ListPetsItemsAsync(limit: 50))
{
    Console.WriteLine(item.Name);
}
```

Polymorphic responses are returned as their concrete types (`Dog`, `Cat`, or `UnrecognizedPet` for discriminator
values this client does not know). See [Generated code](generated-code.md) for the full shape of clients, models and
errors.

## Using the CLI instead

When generated code should be checked in (code review, other build systems, inspection), use the `api-gen` tool:

```shell
dotnet tool install --global Msynk.BlazorApi.Cli --version 0.1.0-preview --add-source ./artifacts
api-gen generate Contracts/petstore.yaml --output Generated --namespace MyApp.Petstore
```

Or describe the generation in `blazorapi.json` and run `api-gen generate` without arguments:

```json
{
  "contract": "Contracts/petstore.yaml",
  "output": "Generated",
  "namespace": "MyApp.Petstore",
  "generateQueries": true
}
```

The project then references only `Msynk.BlazorApi.Runtime` (plus `Msynk.BlazorApi.DotNetQuery` when queries are
generated). See [CLI](cli.md).

## Next steps

- [Authentication](authentication.md): tokens, API keys, Blazor WebAssembly.
- [Queries](queries.md): caching with dotnet-query (`GenerateQueries=true`).
- [Blazor](blazor.md): result and error components, server validation in `EditForm`.
- [Testing](testing.md): generated mocks, fixtures and `MockApiHandler` (`GenerateMocks=true`).
- [Troubleshooting](troubleshooting.md) if the build reports `BAPI` diagnostics.
