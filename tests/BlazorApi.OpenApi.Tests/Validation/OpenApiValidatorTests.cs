using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Validation;

namespace BlazorApi.OpenApi.Tests.Validation;

public class OpenApiValidatorTests
{
    private static IReadOnlyList<string> Validate(string paths, string? components = null, string version = "3.0.3") =>
        TestContracts.LoadYaml(TestContracts.Document(paths, components, version)).Ids(DiagnosticSeverity.Info);

    [Fact]
    public void Duplicate_operation_ids()
    {
        var ids = Validate("""
            /a:
              get: {operationId: same, responses: {'200': {description: ok}}}
            /b:
              get: {operationId: same, responses: {'200': {description: ok}}}
            """);

        Assert.Equal(["BAPI3001"], ids);
    }

    [Fact]
    public void Path_template_and_path_parameters_must_match()
    {
        var ids = Validate("""
            /a/{id}/{other}:
              get:
                parameters:
                  - {name: id, in: path, required: true, schema: {type: string}}
                  - {name: extra, in: path, required: true, schema: {type: string}}
                responses: {'200': {description: ok}}
            """);

        Assert.Equal(["BAPI3002", "BAPI3003"], ids.OrderBy(x => x));
    }

    [Fact]
    public void Path_parameters_must_be_required()
    {
        Assert.Equal(["BAPI3004"], Validate("""
            /a/{id}:
              get:
                parameters: [{name: id, in: path, schema: {type: string}}]
                responses: {'200': {description: ok}}
            """));
    }

    [Fact]
    public void Operation_parameters_override_path_item_parameters()
    {
        Assert.Empty(Validate("""
            /a/{id}:
              parameters: [{name: id, in: path, required: true, schema: {type: string}}]
              get:
                parameters: [{name: id, in: path, required: true, schema: {type: integer}}]
                responses: {'200': {description: ok}}
            """));
    }

    [Fact]
    public void Duplicate_parameters_in_one_list()
    {
        Assert.Contains("BAPI3005", Validate("""
            /a:
              get:
                parameters:
                  - {name: q, in: query, schema: {type: string}}
                  - {name: q, in: query, schema: {type: string}}
                responses: {'200': {description: ok}}
            """));
    }

    [Fact]
    public void Header_parameter_names_are_case_insensitive()
    {
        Assert.Contains("BAPI3005", Validate("""
            /a:
              get:
                parameters:
                  - {name: X-Id, in: header, schema: {type: string}}
                  - {name: x-id, in: header, schema: {type: string}}
                responses: {'200': {description: ok}}
            """));
    }

    [Fact]
    public void Equivalent_and_malformed_paths()
    {
        var ids = Validate("""
            /a/{id}:
              get:
                parameters: [{name: id, in: path, required: true, schema: {type: string}}]
                responses: {'200': {description: ok}}
            /a/{name}:
              get:
                parameters: [{name: name, in: path, required: true, schema: {type: string}}]
                responses: {'200': {description: ok}}
            /b/{x:
              get: {responses: {'200': {description: ok}}}
            """);

        Assert.Contains("BAPI3011", ids);
        Assert.Contains("BAPI3008", ids);
    }

    [Fact]
    public void Undefined_security_schemes()
    {
        Assert.Equal(["BAPI3007"], Validate("""
            /a:
              get:
                security: [{oauth: [read]}]
                responses: {'200': {description: ok}}
            """));
    }

    [Fact]
    public void Security_scheme_required_fields()
    {
        Assert.Equal(["BAPI1007", "BAPI1007", "BAPI1007"], Validate("{}", """
            securitySchemes:
              key: {type: apiKey}
              http: {type: http}
            """));
    }

    [Fact]
    public void Required_property_not_defined()
    {
        Assert.Equal(["BAPI3009"], Validate("{}", """
            schemas:
              A:
                type: object
                required: [missing]
                properties: {present: {type: string}}
            """));
    }

    [Fact]
    public void Required_properties_of_allOf_members_are_not_flagged()
    {
        Assert.Empty(Validate("{}", """
            schemas:
              Base:
                type: object
                properties: {id: {type: string}}
              Derived:
                allOf:
                  - $ref: '#/components/schemas/Base'
                  - type: object
                    required: [id]
                    properties: {name: {type: string}}
            """));
    }

    [Fact]
    public void Discriminator_property_must_exist()
    {
        Assert.Equal(["BAPI3010"], Validate("{}", """
            schemas:
              A:
                oneOf:
                  - {$ref: '#/components/schemas/B'}
                discriminator: {propertyName: kind}
              B:
                type: object
                properties: {other: {type: string}}
            """));
    }

    [Fact]
    public void Discriminator_found_in_variants_or_inherited_properties()
    {
        Assert.Empty(Validate("{}", """
            schemas:
              Base:
                type: object
                required: [kind]
                properties: {kind: {type: string}}
                discriminator: {propertyName: kind}
              A:
                oneOf:
                  - {$ref: '#/components/schemas/B'}
                discriminator: {propertyName: kind}
              B:
                allOf: [{$ref: '#/components/schemas/Base'}]
            """));
    }

    [Fact]
    public void Parameter_needs_exactly_one_of_schema_or_content()
    {
        var ids = Validate("""
            /a:
              get:
                parameters:
                  - {name: a, in: query}
                  - name: b
                    in: query
                    schema: {type: string}
                    content: {application/json: {schema: {type: string}}}
                responses: {'200': {description: ok}}
            """);

        Assert.Equal(["BAPI3013", "BAPI3013"], ids);
    }

    [Fact]
    public void Parameter_styles_must_suit_their_location()
    {
        Assert.Equal(["BAPI3012"], Validate("""
            /a:
              get:
                parameters: [{name: h, in: header, style: form, schema: {type: string}}]
                responses: {'200': {description: ok}}
            """));
    }

    [Fact]
    public void Ignored_header_parameters_are_reported()
    {
        var ids = Validate("""
            /a:
              get:
                parameters: [{name: Accept, in: header, schema: {type: string}}]
                responses: {'200': {description: ok}}
            """);

        Assert.Equal(["BAPI3017"], ids);
    }

    [Fact]
    public void Server_variables_must_be_defined()
    {
        var result = TestContracts.LoadYaml("""
            openapi: 3.0.3
            info: {title: T, version: '1'}
            servers: [{url: 'https://{region}.example.com'}]
            paths: {}
            """);

        Assert.Equal(["BAPI3016"], result.Ids());
    }

    [Fact]
    public void Schema_constraints_and_values_are_checked()
    {
        var ids = Validate("{}", """
            schemas:
              A: {type: string, minLength: 5, maxLength: 2}
              B: {type: string, pattern: '[unclosed'}
              C: {type: integer, enum: [1, two]}
              D: {type: integer, default: 1.5}
              E: {type: number, multipleOf: 0}
            """);

        Assert.Equal(["BAPI3014", "BAPI3014", "BAPI3015", "BAPI3018", "BAPI3018"], ids.OrderBy(x => x));
    }

    [Fact]
    public void Missing_responses_warn_in_3_1()
    {
        Assert.Equal(["BAPI3006"], Validate("""
            /a:
              get: {operationId: a}
            """, version: "3.1.0"));
    }

    [Fact]
    public void Petstore_and_features_contracts_validate_cleanly()
    {
        Assert.Empty(TestContracts.LoadFile("petstore.yaml").Ids());
        Assert.Empty(TestContracts.LoadFile("features-3.1.json").Ids());
    }

    [Theory]
    [InlineData("/a/{b}/c/{d}", new[] { "b", "d" })]
    [InlineData("/plain", new string[0])]
    public void Parses_path_templates(string path, string[] expected)
    {
        Assert.Equal(expected, OpenApiValidator.ParsePathTemplate(path, out _));
    }
}
