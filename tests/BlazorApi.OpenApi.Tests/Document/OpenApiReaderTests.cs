using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Document;

namespace BlazorApi.OpenApi.Tests.Document;

public class OpenApiReaderTests
{
    [Fact]
    public void Loads_petstore_without_diagnostics()
    {
        var result = TestContracts.LoadFile("petstore.yaml");

        Assert.Empty(result.Ids());
        var doc = result.Document!;
        Assert.Equal(OpenApiSpecVersion.OpenApi3_0, doc.SpecVersion);
        Assert.Equal("Petstore", doc.Info.Title);
        Assert.Equal("1.0.0", doc.Info.Version);
        Assert.Equal(["/pets", "/pets/{petId}", "/pets/{petId}/photo", "/store/orders", "/store/inventory", "/health"], doc.Paths.Keys);
        Assert.Equal(11, doc.Operations.Count());
        Assert.Equal("api", doc.Servers[0].Variables["environment"].Default);
        Assert.Equal(["bearerAuth", "apiKey"], doc.Components.SecuritySchemes.Keys);
    }

    [Fact]
    public void Reads_operations_parameters_and_responses()
    {
        var doc = TestContracts.LoadFile("petstore.yaml").Document!;
        var list = doc.Paths["/pets"].Operations["get"];

        Assert.Equal("listPets", list.OperationId);
        Assert.Equal(["pets"], list.Tags);
        Assert.Equal(5, list.Parameters.Count);
        var limit = list.Parameters[0];
        Assert.True(limit.IsReference);
        Assert.Equal("limit", limit.Resolved.Name);
        Assert.Equal(ParameterLocation.Query, limit.Resolved.In);
        Assert.Equal(1m, limit.Resolved.Schema!.Minimum);
        Assert.Equal("form", list.Parameters[2].Style);
        Assert.True(list.Parameters[2].Explode);
        Assert.Equal(["200", "default"], list.Responses.Keys);
        Assert.Equal("X-Total-Count", list.Responses["200"].Headers.Keys.Single());
        Assert.Equal("Unexpected error", list.Responses["default"].Resolved.Description);

        var delete = doc.Paths["/pets/{petId}"].Operations["delete"];
        Assert.True(delete.Deprecated);
        Assert.Equal("petId", doc.Paths["/pets/{petId}"].Parameters.Single().Name);
        Assert.Empty(doc.Paths["/health"].Operations["get"].Security!);
        Assert.Null(list.Security);
    }

    [Fact]
    public void Normalizes_schema_nullability_and_records_composition()
    {
        var doc = TestContracts.LoadFile("petstore.yaml").Document!;
        var pet = doc.Components.Schemas["Pet"];

        Assert.Equal(JsonSchemaTypes.Object, pet.Types);
        Assert.Equal(["id", "name", "kind"], pet.Required);
        Assert.True(pet.Properties["tag"].IsNullable);
        Assert.Equal(JsonSchemaTypes.String | JsonSchemaTypes.Null, pet.Properties["tag"].Types);
        Assert.True(pet.Properties["id"].ReadOnly);
        Assert.Equal("kind", pet.Discriminator!.PropertyName);
        Assert.Equal("#/components/schemas/Dog", pet.Discriminator.Mapping["dog"]);
        var owner = pet.Properties["owner"];
        Assert.True(owner.IsNullable);
        Assert.Same(doc.Components.Schemas["Owner"], owner.AllOf.Single().Resolved);
        Assert.Same(pet, doc.Components.Schemas["Dog"].AllOf[0].Resolved);
        Assert.Equal("#/components/schemas/Pet/properties/tag", pet.Properties["tag"].Pointer);
    }

    [Fact]
    public void Reads_openapi_3_1_type_arrays_const_and_defs()
    {
        var result = TestContracts.LoadFile("features-3.1.json");

        Assert.Empty(result.Ids());
        var doc = result.Document!;
        Assert.Equal(OpenApiSpecVersion.OpenApi3_1, doc.SpecVersion);
        var document = doc.Components.Schemas["Document"];
        Assert.Equal(JsonSchemaTypes.String | JsonSchemaTypes.Null, document.Properties["summary"].Types);
        Assert.Equal("base64", document.Properties["content"].ContentEncoding);
        var labels = document.Properties["labels"];
        Assert.Equal("Document labels.", labels.Description);
        Assert.Same(document.Definitions["Labels"], labels.ReferenceTarget);
        Assert.Equal("Circle", ((global::BlazorApi.OpenApi.Syntax.ScalarNode)doc.Components.Schemas["Circle"].Properties["shapeType"].Const!).Value);
        Assert.Equal(0m, doc.Components.Schemas["Square"].Properties["side"].ExclusiveMinimum);
        Assert.Equal(["shapeCreated"], doc.Webhooks.Keys);
    }

    [Fact]
    public void Converts_openapi_3_0_boolean_exclusive_bounds()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document("{}", """
            schemas:
              Range:
                type: integer
                minimum: 1
                exclusiveMinimum: true
                maximum: 10
            """));

        var schema = result.Document!.Components.Schemas["Range"];
        Assert.Null(schema.Minimum);
        Assert.Equal(1m, schema.ExclusiveMinimum);
        Assert.Equal(10m, schema.Maximum);
    }

    [Fact]
    public void Swagger_2_is_rejected_with_guidance()
    {
        var result = TestContracts.LoadYaml("swagger: '2.0'\ninfo: {title: T, version: '1'}\npaths: {}");

        Assert.Null(result.Document);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("BAPI1003", error.Id);
        Assert.Contains("convert Swagger 2.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenApi_3_2_is_processed_as_3_1_with_a_warning()
    {
        var result = TestContracts.LoadYaml("openapi: 3.2.0\ninfo: {title: T, version: '1'}\npaths: {}");

        Assert.NotNull(result.Document);
        Assert.Equal(OpenApiSpecVersion.OpenApi3_1, result.Document!.SpecVersion);
        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(result.Diagnostics).Severity);
    }

    [Fact]
    public void Missing_version_and_unknown_versions_are_errors()
    {
        Assert.Equal(["BAPI1004"], TestContracts.LoadYaml("info: {title: T, version: '1'}").Ids());
        Assert.Equal(["BAPI1003"], TestContracts.LoadYaml("openapi: 4.0.0\ninfo: {title: T, version: '1'}").Ids());
    }

    [Fact]
    public void Reports_structural_problems_with_pointers()
    {
        var result = TestContracts.LoadYaml("""
            openapi: 3.0.3
            info:
              title: T
            paths:
              /a:
                get:
                  operationId: [not, a, string]
                  responses:
                    '200': {description: ok}
                  summery: typo
                  x-custom: kept
            """);

        var ids = result.Ids();
        Assert.Contains("BAPI1007", ids); // info.version missing
        Assert.Contains("BAPI1006", ids); // operationId must be a string
        Assert.Contains("BAPI1008", ids); // unknown field 'summery'
        var unknown = result.Diagnostics.Single(d => d.Id == "BAPI1008");
        Assert.Equal("#/paths/~1a/get/summery", unknown.Pointer);
        Assert.Equal(10, unknown.Location.Line);
        Assert.Equal("kept", result.Document!.Paths["/a"].Operations["get"].GetExtensionString("x-custom"));
    }

    [Fact]
    public void Invalid_response_keys_are_reported()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document("""
            /a:
              get:
                responses:
                  '2XX': {description: ok}
                  '600': {description: bad}
                  default: {description: other}
            """));

        Assert.Equal(["BAPI1009"], result.Ids());
        Assert.Equal(["2XX", "default"], result.Document!.Paths["/a"].Operations["get"].Responses.Keys);
    }

    [Fact]
    public void Nullable_keyword_in_3_1_is_honored_with_a_warning()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document("{}", """
            schemas:
              A:
                type: string
                nullable: true
            """, "3.1.0"));

        Assert.True(result.Document!.Components.Schemas["A"].IsNullable);
        Assert.Equal(["BAPI1009"], result.Ids());
    }

    [Fact]
    public void Records_unsupported_schema_keywords()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document("{}", """
            schemas:
              A:
                type: object
                patternProperties:
                  '^x': {type: string}
                not: {type: string}
                unevaluatedProperties: false
            """, "3.1.0"));

        var schema = result.Document!.Components.Schemas["A"];
        Assert.Equal(["patternProperties", "not"], schema.UnsupportedKeywords);
        Assert.False(schema.AdditionalPropertiesAllowed);
    }

    [Fact]
    public void Yaml_aliases_share_one_model_instance()
    {
        var result = TestContracts.LoadYaml(TestContracts.Document("{}", """
            schemas:
              A:
                type: object
                properties:
                  x: &shared {type: string, maxLength: 3}
                  y: *shared
            """));

        var a = result.Document!.Components.Schemas["A"];
        Assert.Same(a.Properties["x"], a.Properties["y"]);
    }

    [Fact]
    public void Syntax_errors_produce_no_document()
    {
        var result = TestContracts.LoadYaml("openapi: 3.0.3\ninfo: [unclosed");

        Assert.Null(result.Document);
        Assert.Equal("BAPI1001", Assert.Single(result.Diagnostics).Id);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Severity_overrides_change_or_suppress_diagnostics()
    {
        var yaml = TestContracts.Document("""
            /a:
              get:
                responses:
                  '200': {description: ok}
                bogus: 1
            """);

        var suppressed = TestContracts.LoadYaml(yaml, new OpenApiLoadOptions
        {
            SeverityOverrides = new Dictionary<string, DiagnosticSeverity?> { ["BAPI1008"] = null },
        });
        var escalated = TestContracts.LoadYaml(yaml, new OpenApiLoadOptions
        {
            SeverityOverrides = new Dictionary<string, DiagnosticSeverity?> { ["BAPI1008"] = DiagnosticSeverity.Error },
        });

        Assert.Empty(suppressed.Diagnostics);
        Assert.True(escalated.HasErrors);
    }

    [Fact]
    public void Diagnostics_use_msbuild_format()
    {
        var result = TestContracts.LoadYaml("openapi: 3.0.3\ninfo: [unclosed");

        Assert.StartsWith("test.yaml(2,", result.Diagnostics[0].ToString(), StringComparison.Ordinal);
        Assert.Contains(": error BAPI1001: ", result.Diagnostics[0].ToString(), StringComparison.Ordinal);
    }
}
