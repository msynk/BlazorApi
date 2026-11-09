using BlazorApi.OpenApi.Diagnostics;
using BlazorApi.OpenApi.Document;
using BlazorApi.OpenApi.Model;

namespace BlazorApi.OpenApi.Tests.Model;

public class ApiModelBuilderTests
{
    private static (ApiModel Model, IReadOnlyList<ApiDiagnostic> Diagnostics) BuildFile(string contract)
    {
        var result = TestContracts.LoadFile(contract);
        Assert.False(result.HasErrors, result.Describe());
        var diagnostics = new DiagnosticBag();
        return (ApiModelBuilder.Build(result.Document!, diagnostics), diagnostics);
    }

    private static (ApiModel Model, IReadOnlyList<ApiDiagnostic> Diagnostics) BuildYaml(string paths, string? components = null, string version = "3.0.3")
    {
        var result = TestContracts.LoadYaml(TestContracts.Document(paths, components, version));
        Assert.False(result.HasErrors, result.Describe());
        var diagnostics = new DiagnosticBag();
        return (ApiModelBuilder.Build(result.Document!, diagnostics), diagnostics);
    }

    private static T Type<T>(ApiModel model, string name)
        where T : ApiNamedType => Assert.IsType<T>(model.Types.Single(t => t.Name == name));

    private static ApiProperty Property(ApiObjectType type, string name) => type.Properties.Single(p => p.JsonName == name);

    [Fact]
    public void Petstore_builds_without_diagnostics()
    {
        var (model, diagnostics) = BuildFile("petstore.yaml");

        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics));
        Assert.Equal("Petstore", model.Title);
        Assert.Equal(11, model.Operations.Count);
        Assert.Equal(
            ["PetStatus", "Pet", "Owner", "Dog", "Cat", "NewPet", "PetPatch", "PetPage", "PhotoInfo", "Order", "Error", "ProblemDetails", "uploadPetPhoto.Request"],
            model.Types.Select(t => t.Name));
        Assert.Equal("https://api.example.com/v1", model.Servers[0].DefaultUrl);
    }

    [Fact]
    public void Maps_required_optional_and_nullable_independently()
    {
        var (model, _) = BuildFile("petstore.yaml");
        var pet = Type<ApiObjectType>(model, "Pet");

        var id = Property(pet, "id");
        Assert.True(id.Required);
        Assert.True(id.ReadOnly);
        Assert.False(id.Type.IsNullable);
        Assert.Equal("integer(int64)", id.Type.ToString());

        var tag = Property(pet, "tag");
        Assert.False(tag.Required);
        Assert.True(tag.Type.IsNullable);

        var owner = Property(pet, "owner");
        Assert.False(owner.Required);
        Assert.True(owner.Type.IsNullable);
        Assert.Same(Type<ApiObjectType>(model, "Owner"), Assert.IsType<ApiNamedTypeRef>(owner.Type).Target);

        Assert.Equal("date(date)", Property(pet, "birthDate").Type.ToString());
        Assert.Equal("map<string>", Property(pet, "attributes").Type.ToString());
        Assert.Equal(1, Property(pet, "name").Constraints.MinLength);
        Assert.Equal(100, Property(pet, "name").Constraints.MaxLength);
    }

    [Fact]
    public void Builds_discriminated_inheritance_hierarchies()
    {
        var (model, _) = BuildFile("petstore.yaml");
        var pet = Type<ApiObjectType>(model, "Pet");
        var dog = Type<ApiObjectType>(model, "Dog");
        var cat = Type<ApiObjectType>(model, "Cat");

        Assert.Same(pet, dog.BaseType);
        Assert.Same(pet, cat.BaseType);
        Assert.Equal([dog, cat], pet.DerivedTypes);
        Assert.Equal("kind", pet.Discriminator!.PropertyName);
        Assert.Equal(["dog", "cat"], pet.Discriminator.Mappings.Select(m => m.Value));
        Assert.Equal("dog", dog.DiscriminatorValue);
        Assert.True(Property(pet, "kind").IsDiscriminator);
        Assert.Equal(["barkVolume"], dog.Properties.Select(p => p.JsonName));
        Assert.True(Property(cat, "indoor").Required);
        Assert.Equal(10, cat.AllProperties.Count());
    }

    [Fact]
    public void Handles_recursive_schemas_and_enums()
    {
        var (model, _) = BuildFile("petstore.yaml");
        var owner = Type<ApiObjectType>(model, "Owner");
        var status = Type<ApiEnumType>(model, "PetStatus");

        Assert.Same(owner, Assert.IsType<ApiNamedTypeRef>(Property(owner, "manager").Type).Target);
        Assert.Equal(ApiPrimitiveKind.String, status.UnderlyingKind);
        Assert.Equal(["available", "pending", "sold"], status.Members.Select(m => m.ValueText));
        Assert.Equal("uuid(uuid)", Property(owner, "id").Type.ToString());
    }

    [Fact]
    public void Merges_parameters_with_defaults_applied()
    {
        var (model, _) = BuildFile("petstore.yaml");
        var list = model.Operations.Single(o => o.OperationId == "listPets");

        Assert.Equal(["limit", "cursor", "tags", "status", "X-Request-Id"], list.Parameters.Select(p => p.Name));
        var limit = list.Parameters[0];
        Assert.Equal(ParameterLocation.Query, limit.Location);
        Assert.Equal(ApiParameterStyle.Form, limit.Style);
        Assert.True(limit.Explode);
        Assert.False(limit.Required);
        Assert.Equal("20", limit.Default!.ToJson());
        Assert.Equal(100m, limit.Constraints.Maximum);
        Assert.Equal("array<string>", list.Parameters[2].Type.ToString());
        Assert.Equal(ApiParameterStyle.Simple, list.Parameters[4].Style);

        var get = model.Operations.Single(o => o.OperationId == "getPet");
        var petId = Assert.Single(get.Parameters);
        Assert.True(petId.Required);
        Assert.Equal(ParameterLocation.Path, petId.Location);
    }

    [Fact]
    public void Orders_and_classifies_responses()
    {
        var (model, _) = BuildFile("petstore.yaml");
        var list = model.Operations.Single(o => o.OperationId == "listPets");
        var create = model.Operations.Single(o => o.OperationId == "createPet");

        Assert.Equal(["200", "default"], list.Responses.Select(r => r.StatusCode));
        Assert.True(list.Responses[0].IsSuccess);
        Assert.False(list.Responses[1].IsSuccess);
        Assert.Equal("X-Total-Count", Assert.Single(list.Responses[0].Headers).Name);
        Assert.Equal("PetPage", list.Responses[0].Contents[0].Type!.ToString());
        Assert.Equal(["201", "422"], create.Responses.Select(r => r.StatusCode));
        Assert.Equal(ApiBodyKind.Json, create.Responses[1].Contents[0].Kind);
        Assert.Equal("application/problem+json", create.Responses[1].Contents[0].MediaType);
        Assert.True(create.RequestBody!.Required);
        Assert.Equal("NewPet", create.RequestBody.Contents[0].Type!.ToString());
    }

    [Fact]
    public void Classifies_binary_multipart_form_and_text_bodies()
    {
        var (model, _) = BuildFile("petstore.yaml");

        var download = model.Operations.Single(o => o.OperationId == "downloadPetPhoto").Responses[0].Contents[0];
        Assert.Equal(ApiBodyKind.Binary, download.Kind);
        Assert.Equal("binary", download.Type!.ToString());

        var upload = model.Operations.Single(o => o.OperationId == "uploadPetPhoto").RequestBody!.Contents[0];
        Assert.Equal(ApiBodyKind.Multipart, upload.Kind);
        Assert.Equal(["file", "caption"], upload.Parts.Select(p => p.Name));
        Assert.True(upload.Parts[0].IsFile);
        Assert.True(upload.Parts[0].Required);

        var order = model.Operations.Single(o => o.OperationId == "placeOrder").RequestBody!.Contents[0];
        Assert.Equal(ApiBodyKind.FormUrlEncoded, order.Kind);
        Assert.Equal(4, order.Parts.Count);

        var health = model.Operations.Single(o => o.OperationId == "getHealth").Responses[0].Contents[0];
        Assert.Equal(ApiBodyKind.Text, health.Kind);
    }

    [Fact]
    public void Resolves_effective_security()
    {
        var (model, _) = BuildFile("petstore.yaml");

        Assert.Equal("bearerAuth", model.Operations.Single(o => o.OperationId == "listPets").Security.Single().Items.Single().Scheme.Name);
        Assert.Equal("apiKey", model.Operations.Single(o => o.OperationId == "getInventory").Security.Single().Items.Single().Scheme.Name);
        Assert.Empty(model.Operations.Single(o => o.OperationId == "getHealth").Security);
        Assert.Equal(["bearerAuth", "apiKey"], model.SecuritySchemes.Select(s => s.Name));
    }

    [Fact]
    public void OpenApi_3_1_unions_nullability_and_encodings()
    {
        var (model, diagnostics) = BuildFile("features-3.1.json");

        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics));
        var shape = Type<ApiUnionType>(model, "Shape");
        Assert.Equal(ApiUnionDispatch.Discriminator, shape.Dispatch);
        Assert.Equal("shapeType", shape.DiscriminatorPropertyName);
        Assert.Equal(["Circle", "Square"], shape.Variants.Select(v => v.DiscriminatorValue));

        var circle = Type<ApiObjectType>(model, "Circle");
        var shapeType = Assert.IsType<ApiPrimitiveType>(Property(circle, "shapeType").Type);
        Assert.Equal("\"Circle\"", shapeType.ConstValue!.ToJson());

        var document = Type<ApiObjectType>(model, "Document");
        var summary = Property(document, "summary");
        Assert.True(summary.Required);
        Assert.True(summary.Type.IsNullable);
        var parent = Property(document, "parent");
        Assert.True(parent.Type.IsNullable);
        Assert.Same(document, Assert.IsType<ApiNamedTypeRef>(parent.Type).Target);
        Assert.Equal("base64(base64)", Property(document, "content").Type.ToString());
        var labels = Assert.IsType<ApiArrayType>(Property(document, "labels").Type);
        Assert.True(labels.UniqueItems);
        Assert.Equal("Document labels.", Property(document, "labels").Description);
    }

    [Fact]
    public void Inline_types_get_contextual_name_hints()
    {
        var (model, _) = BuildYaml("""
            /users:
              get:
                operationId: listUsers
                responses:
                  '200':
                    description: ok
                    content:
                      application/json:
                        schema:
                          type: object
                          properties:
                            total: {type: integer}
                            items:
                              type: array
                              items:
                                type: object
                                properties:
                                  address:
                                    type: object
                                    properties: {city: {type: string}}
            """);

        Assert.Equal(
            ["listUsers.Response", "listUsers.Response.items.Item", "listUsers.Response.items.Item.address"],
            model.Types.Select(t => t.Name));
        Assert.All(model.Types, t => Assert.Equal(ApiTypeOrigin.Inline, t.Origin));
    }

    [Fact]
    public void Operation_name_hint_falls_back_to_method_and_path()
    {
        var (model, _) = BuildYaml("""
            /pets/{petId}/photo:
              get:
                parameters: [{name: petId, in: path, required: true, schema: {type: string}}]
                responses: {'200': {description: ok}}
            """);

        Assert.Equal(["get", "pets", "petId", "photo"], model.Operations[0].NameHint);
    }

    [Fact]
    public void Flattens_multiple_allOf_references_and_reports_conflicts()
    {
        var (model, diagnostics) = BuildYaml("{}", """
            schemas:
              A:
                type: object
                properties: {a: {type: string}, shared: {type: string}}
              B:
                type: object
                required: [b]
                properties: {b: {type: integer}, shared: {type: integer}}
              C:
                allOf:
                  - $ref: '#/components/schemas/A'
                  - $ref: '#/components/schemas/B'
            """);

        var c = Type<ApiObjectType>(model, "C");
        Assert.Null(c.BaseType);
        Assert.Equal(["a", "shared", "b"], c.Properties.Select(p => p.JsonName));
        Assert.True(Property(c, "b").Required);
        Assert.Equal("BAPI4003", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void Single_allOf_with_nullable_is_a_nullable_reference()
    {
        var (model, _) = BuildYaml("{}", """
            schemas:
              Owner: {type: object, properties: {id: {type: string}}}
              Holder:
                type: object
                properties:
                  owner:
                    allOf: [{$ref: '#/components/schemas/Owner'}]
                    nullable: true
                    description: The owner.
                  required:
                    allOf: [{$ref: '#/components/schemas/Owner'}]
            """);

        var holder = Type<ApiObjectType>(model, "Holder");
        Assert.True(Property(holder, "owner").Type.IsNullable);
        Assert.False(Property(holder, "required").Type.IsNullable);
        Assert.Equal("The owner.", Property(holder, "owner").Description);
        Assert.Equal(2, model.Types.Count);
    }

    [Fact]
    public void Reference_with_structural_siblings_composes_in_3_1()
    {
        var (model, _) = BuildYaml("{}", """
            schemas:
              Base:
                type: object
                properties: {id: {type: string}}
              Extended:
                $ref: '#/components/schemas/Base'
                properties: {extra: {type: integer}}
            """, "3.1.0");

        var extended = Type<ApiObjectType>(model, "Extended");
        Assert.Same(Type<ApiObjectType>(model, "Base"), extended.BaseType);
        Assert.Equal(["extra"], extended.Properties.Select(p => p.JsonName));
    }

    [Fact]
    public void Value_kind_unions_and_ambiguous_unions()
    {
        var (model, diagnostics) = BuildYaml("{}", """
            schemas:
              IdOrName:
                oneOf: [{type: integer}, {type: string}]
              Either:
                anyOf:
                  - {type: object, properties: {a: {type: string}}}
                  - {type: object, properties: {b: {type: string}}}
            """);

        Assert.Equal(ApiUnionDispatch.ValueKind, Type<ApiUnionType>(model, "IdOrName").Dispatch);
        var either = Type<ApiUnionType>(model, "Either");
        Assert.Equal(ApiUnionDispatch.Ambiguous, either.Dispatch);
        Assert.Equal(ApiUnionKind.AnyOf, either.Kind);
        Assert.Equal(["Either.Variant1", "Either.Variant2"], either.Variants.Select(v => v.Type.ToString()));
        Assert.Equal("BAPI4002", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void Enums_with_null_unnamed_and_invalid_values()
    {
        var (model, diagnostics) = BuildYaml("{}", """
            schemas:
              Level:
                type: integer
                enum: [1, 2, 3, null]
                nullable: true
                x-enum-varnames: [Low, Medium, High]
              Mixed:
                enum: [1, a]
              Holder:
                type: object
                properties:
                  kind: {type: string, enum: [holder]}
            """);

        var level = Type<ApiEnumType>(model, "Level");
        Assert.Equal(ApiPrimitiveKind.Integer, level.UnderlyingKind);
        Assert.Equal(["Low", "Medium", "High"], level.Members.Select(m => m.NameHint));
        Assert.DoesNotContain(model.Types, t => t.Name == "Mixed");
        Assert.Equal("BAPI4006", Assert.Single(diagnostics).Id);
        var kind = Assert.IsType<ApiPrimitiveType>(Property(Type<ApiObjectType>(model, "Holder"), "kind").Type);
        Assert.Equal("\"holder\"", kind.ConstValue!.ToJson());
        Assert.DoesNotContain(model.Types, t => t.Name.Contains("kind", StringComparison.Ordinal));
    }

    [Fact]
    public void Free_form_objects_maps_and_additional_properties()
    {
        var (model, _) = BuildYaml("{}", """
            schemas:
              Free: {type: object}
              Counts: {type: object, additionalProperties: {type: integer}}
              Strict:
                type: object
                additionalProperties: false
                properties: {a: {type: string}}
              Extensible:
                type: object
                additionalProperties: {type: string}
                properties: {a: {type: string}}
              Holder:
                type: object
                properties:
                  free: {$ref: '#/components/schemas/Free'}
                  counts: {$ref: '#/components/schemas/Counts'}
            """);

        var holder = Type<ApiObjectType>(model, "Holder");
        Assert.Equal("map<any>", Property(holder, "free").Type.ToString());
        Assert.Equal("map<integer>", Property(holder, "counts").Type.ToString());
        Assert.True(Type<ApiObjectType>(model, "Strict").AdditionalPropertiesForbidden);
        Assert.Equal("string", Type<ApiObjectType>(model, "Extensible").AdditionalProperties!.ToString());
        Assert.DoesNotContain(model.Types, t => t.Name is "Free" or "Counts");
    }

    [Fact]
    public void Reports_types_without_a_typed_representation()
    {
        var (model, diagnostics) = BuildYaml("{}", """
            schemas:
              Multi: {type: [string, integer]}
              Tuple:
                type: array
                prefixItems: [{type: string}, {type: integer}]
              Odd: {type: string, format: zip-code}
              Loop:
                type: array
                items: {$ref: '#/components/schemas/Loop'}
            """, "3.1.0");

        Assert.Equal(["BAPI4001", "BAPI4001", "BAPI4005", "BAPI4007"], diagnostics.Select(d => d.Id).OrderBy(i => i));
        Assert.Empty(model.Types);
    }

    [Fact]
    public void Parameter_serialization_problems_are_reported()
    {
        var (model, diagnostics) = BuildYaml("""
            /a:
              get:
                parameters:
                  - {name: filter, in: query, style: deepObject, schema: {type: string}}
                  - {name: ids, in: query, style: pipeDelimited, schema: {type: array, items: {type: integer}}}
                  - name: json
                    in: query
                    content:
                      application/json:
                        schema: {type: object, properties: {a: {type: string}}}
                responses: {'200': {description: ok}}
            """);

        var parameters = model.Operations[0].Parameters;
        Assert.Equal(ApiParameterStyle.Form, parameters[0].Style);
        Assert.Equal(ApiParameterStyle.PipeDelimited, parameters[1].Style);
        Assert.False(parameters[1].Explode);
        Assert.Equal("application/json", parameters[2].ContentMediaType);
        Assert.Equal("BAPI4008", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public void Default_response_is_success_only_without_2xx()
    {
        var (model, _) = BuildYaml("""
            /a:
              get:
                responses:
                  default: {description: any, content: {application/json: {schema: {type: string}}}}
            """);

        Assert.True(model.Operations[0].Responses.Single().IsSuccess);
    }

    [Fact]
    public void External_component_schemas_keep_their_names()
    {
        var (model, diagnostics) = BuildFile("external/main.yaml");

        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics));
        Assert.Equal(["item", "Error"], model.Types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).Reverse());
        Assert.All(model.Types, t => Assert.Equal(ApiTypeOrigin.Component, t.Origin));
    }

    [Fact]
    public void Model_building_is_deterministic()
    {
        string Describe(ApiModel m) => string.Join("\n",
            m.Types.Select(t => t.Name + ":" + string.Join(",", (t as ApiObjectType)?.Properties.Select(p => p.ToString()) ?? []))
                .Concat(m.Operations.Select(o => o + " " + string.Join(",", o.Parameters) + " " + string.Join(",", o.Responses))));

        var first = Describe(BuildFile("petstore.yaml").Model);
        var second = Describe(BuildFile("petstore.yaml").Model);

        Assert.Equal(first, second);
    }
}
