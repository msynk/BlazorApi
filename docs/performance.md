# Performance

## Measurements

Measured with BenchmarkDotNet 0.15.8 (ShortRun job), .NET 10, Windows 11. "Large" is a synthetic contract with 500
schemas of 12 properties each (strings, numbers, enums, arrays, maps, nullable types, references) and 500 operations.
Memory is the managed allocation per operation.

| Benchmark | What it measures | Mean | Allocated |
|---|---|---|---|
| `ParsePetstoreYaml` | YAML parsing of `tests/Contracts/petstore.yaml` | 29.4 us | 98 KB |
| `ParsePetstoreJson` | JSON parsing of the same document | 15.5 us | 97 KB |
| `LoadLargeContract` | parse, resolve references and validate the large contract | 42.6 ms | 32.8 MB |
| `BuildLargeModel` | build the API model of the large contract | 8.2 ms | 10 MB |
| `GenerateLargeClient` | model, plan and emit the large client, including queries | 31.8 ms | 70.5 MB |
| `GeneratePetstoreClient` | full generation of the Petstore client with queries and mocks | 0.56 ms | 1.66 MB |
| `GetPolymorphicPet` | generated client call reading a polymorphic `Pet`, in-memory handler | 1.63 us | 3.7 KB |
| `ListPetsWithParameters` | generated client call with query, array and enum parameters reading a 1000-item page | 0.97 ms | 919 KB |

What this means in practice:

- A typical contract is generated in well under a millisecond, and the source generator only re-runs when a
  contract, its references or its options change, never on C# edits.
- A 500-operation contract loads and generates in under 100 ms.
- Client overhead per call is small compared with network latency: the polymorphic read costs about 1.6 us,
  including the discriminator lookup in the generated converter.

## Why it is fast

- Hand-written parsers that build a compact node tree in one pass, with no intermediate DOM conversion.
- The source generator caches per contract on value-equatable inputs (`EquatableArray`), so unrelated changes reuse
  the previous output.
- Generated clients describe responses with a static `ApiResponseHandler<T>` per operation (built once), read bodies
  with `HttpCompletionOption.ResponseHeadersRead` and deserialize directly from the response stream.
- Binary downloads are streamed (`FileResponse`), and error bodies are read only up to `MaxErrorBodyLength`.
- Polymorphic converters peek the discriminator on a copy of the `Utf8JsonReader` instead of buffering a
  `JsonDocument`.

## Running the benchmarks

The suite is in `tests/BlazorApi.Benchmarks` (`EngineBenchmarks` and `ClientBenchmarks`). It compiles the Petstore
golden client from `tests/Golden/petstore` and copies the test contracts next to the binaries.

```shell
cd tests/BlazorApi.Benchmarks
dotnet run -c Release                                   # pick benchmarks interactively
dotnet run -c Release -- --filter "*" --job short       # everything, ShortRun as above
dotnet run -c Release -- --filter "*ClientBenchmarks*"  # client execution only
```

Results are written to `BenchmarkDotNet.Artifacts/results` (Markdown, GitHub Markdown and CSV). The size of the
large contract is a `[Params]` value (`Size`, 500 by default) in `Benchmarks.cs`.

Run benchmarks on an idle machine with a Release build; numbers vary between hardware and runtime versions, so
compare runs on the same machine.
