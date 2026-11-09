using BlazorApi.OpenApi.Compatibility;
using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.OpenApi.Tests.Compatibility;

public class ContractComparerTests
{
    private static readonly string Petstore = File.ReadAllText(TestContracts.PathOf("petstore.yaml")).Replace("\r\n", "\n");

    private static ApiModel Model(string yaml)
    {
        var result = OpenApiLoader.Parse(yaml, "petstore.yaml");
        Assert.False(result.HasErrors, result.Describe());
        return ApiModelBuilder.Build(result.Document!, new DiagnosticBag());
    }

    private static CompatibilityReport Compare(Func<string, string> change)
    {
        var updated = change(Petstore);
        Assert.NotEqual(Petstore, updated);
        return ContractComparer.Compare(Model(Petstore), Model(updated));
    }

    private static ContractChange Single(CompatibilityReport report, string kind) =>
        Assert.Single(report.Changes, c => c.Kind == kind);

    [Fact]
    public void Identical_contracts_have_no_changes()
    {
        var report = ContractComparer.Compare(Model(Petstore), Model(Petstore));

        Assert.Empty(report.Changes);
        Assert.Null(report.MaxSeverity);
        Assert.Equal("No changes.", report.ToText());
    }

    [Fact]
    public void Removed_operations_are_breaking_and_added_ones_are_not()
    {
        var report = Compare(y => y.Replace("  /health:\n", "  /status:\n"));

        Assert.Equal(ChangeSeverity.Breaking, report.MaxSeverity);
        Assert.Equal("GET /health", Single(report, "operation-moved").Path);
    }

    [Fact]
    public void Parameter_changes_are_classified()
    {
        var required = Compare(y => y.Replace("        - name: cursor\n          in: query\n", "        - name: cursor\n          in: query\n          required: true\n"));
        var added = Compare(y => y.Replace("        - name: cursor\n", "        - name: sort\n          in: query\n          schema:\n            type: string\n        - name: cursor\n"));

        Assert.Equal(ChangeSeverity.Breaking, Single(required, "parameter-now-required").Severity);
        Assert.Equal(ChangeSeverity.NonBreaking, Single(added, "optional-parameter-added").Severity);
    }

    [Fact]
    public void Response_property_changes_follow_the_response_direction()
    {
        var removed = Compare(y => y.Replace("      required: [code, message]\n      properties:\n        code:\n          type: integer\n          format: int32\n        message:\n          type: string\n",
            "      required: [code]\n      properties:\n        code:\n          type: integer\n          format: int32\n"));
        var widened = Compare(y => y.Replace("        size:\n          type: integer\n          format: int64", "        size:\n          type: number"));
        var nullable = Compare(y => y.Replace("    PhotoInfo:\n      type: object\n      required: [url, size]\n      properties:\n        url:\n          type: string\n          format: uri\n",
            "    PhotoInfo:\n      type: object\n      required: [url, size]\n      properties:\n        url:\n          type: string\n          format: uri\n          nullable: true\n"));

        Assert.Equal(ChangeSeverity.Breaking, removed.Changes.First(c => c.Kind == "property-removed").Severity);
        Assert.Equal(ChangeSeverity.Breaking, widened.Changes.First(c => c.Kind == "type-widened").Severity);
        Assert.Equal(ChangeSeverity.Breaking, nullable.Changes.First(c => c.Kind == "response-now-nullable").Severity);
    }

    [Fact]
    public void Enum_changes_depend_on_direction()
    {
        var report = Compare(y => y.Replace("enum: [available, pending, sold]", "enum: [available, sold, adopted]"));

        Assert.Contains(report.Changes, c => c.Kind == "enum-value-removed" && c.Severity == ChangeSeverity.Breaking);
        Assert.Contains(report.Changes, c => c.Kind == "enum-value-added" && c.Severity == ChangeSeverity.PotentiallyBreaking);
    }

    [Fact]
    public void Request_widening_is_safe_and_new_required_request_properties_are_not()
    {
        var widened = Compare(y => y.Replace("        quantity:\n          type: integer\n          format: int32", "        quantity:\n          type: integer\n          format: int64"));
        var requiredAdded = Compare(y => y.Replace("    NewPet:\n      type: object\n      required: [name, kind]\n", "    NewPet:\n      type: object\n      required: [name, kind, ownerId]\n")
            .Replace("        status:\n          $ref: '#/components/schemas/PetStatus'\n    PetPatch:", "        status:\n          $ref: '#/components/schemas/PetStatus'\n        ownerId:\n          type: string\n    PetPatch:"));

        Assert.Contains(widened.Changes, c => c.Kind == "type-widened" && c.Path.Contains("request", StringComparison.Ordinal) && c.Severity == ChangeSeverity.NonBreaking);
        Assert.Equal(ChangeSeverity.Breaking, Single(requiredAdded, "required-property-added").Severity);
    }

    [Fact]
    public void Security_requirements_added_to_an_open_operation_are_breaking()
    {
        var report = Compare(y => y.Replace("      operationId: getHealth\n      security: []\n", "      operationId: getHealth\n      security:\n        - apiKey: []\n"));

        Assert.Equal(ChangeSeverity.Breaking, Single(report, "security-added").Severity);
    }

    [Fact]
    public void Reports_render_as_text_markdown_and_json_and_policies_filter_kinds()
    {
        var report = Compare(y => y.Replace("  /health:\n    get:\n      operationId: getHealth\n", "  /health:\n    get:\n      operationId: health\n"));

        Assert.Contains("operation-id-changed", report.ToText(), StringComparison.Ordinal);
        Assert.Contains("| Potentially breaking |", report.ToMarkdown(), StringComparison.Ordinal);
        Assert.StartsWith("{\"maxSeverity\":\"potentially-breaking\"", report.ToJson(), StringComparison.Ordinal);
        Assert.Empty(report.Without(["operation-id-changed"]).Changes);
    }
}
