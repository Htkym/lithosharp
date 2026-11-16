using System.Text;
using LithoSharp.Documentation;

namespace LithoSharp.Tests;

public sealed class OpenApiReferenceTests
{
    [Test]
    public async Task StructuredOperationsMergeParametersAndResolveRequestResponseAndEscapedLocalSchemas()
    {
        var member = Read("""
            {
              "openapi":"3.1.0",
              "paths":{
                "/pets/{id}":{
                  "$ref":"#/components/pathItems/Pets",
                  "post":{
                    "summary":"Save pet",
                    "description":"Save <pet> safely.",
                    "deprecated":true,
                    "parameters":[{"name":"limit","in":"query","description":"Maximum results","schema":{"type":"integer","default":20}}],
                    "requestBody":{"$ref":"#/components/requestBodies/Pet"},
                    "responses":{"201":{"$ref":"#/components/responses/Pet","description":"Response description override"}}
                  }
                }
              },
              "components":{
                "pathItems":{"Pets":{"parameters":[
                  {"$ref":"#/components/parameters/Id"},
                  {"name":"limit","in":"query","description":"Overridden description","schema":{"type":"string"}}
                ]}},
                "parameters":{"Id":{"name":"id","in":"path","required":true,"description":"Pet identity","schema":{"type":"integer","format":"int64"}}},
                "requestBodies":{"Pet":{"required":true,"description":"New pet","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Pet~1~0%20name"}}}}},
                "responses":{"Pet":{"description":"Created pet","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Pet~1~0%20name"}}}}},
                "schemas":{"Pet/~ name":{"type":"object","required":["name"],"default":{"name":"Default pet","$ref":"literal default value","$id":"literal resource name","$dynamicRef":"literal dynamic name"},"properties":{"name":{"type":"string","description":"Display <name>"},"$ref":{"type":"string"},"$id":{"type":"string"}}}}
              }
            }
            """).Single();

        await Assert.That(member.Id).IsEqualTo("O:POST /pets/{id}");
        await Assert.That(member.Title).IsEqualTo("Save pet");
        await Assert.That(member.Deprecated).IsTrue();
        await Assert.That(member.BodyHtml).Contains("<h2>Parameters</h2>");
        await Assert.That(member.BodyHtml).Contains("<code>id</code> (path, required)");
        await Assert.That(member.BodyHtml).Contains("<code>limit</code> (query, optional)");
        await Assert.That(member.BodyHtml).Contains("Maximum results");
        await Assert.That(member.BodyHtml).DoesNotContain("Overridden description");
        await Assert.That(member.BodyHtml).Contains("default: <code>20</code>");
        await Assert.That(member.BodyHtml).Contains("<h2>Request body</h2><p>Required</p>");
        await Assert.That(member.BodyHtml).Contains("<h2>Responses</h2><div><h3>201</h3>");
        await Assert.That(member.BodyHtml).Contains("Response description override");
        await Assert.That(member.BodyHtml).DoesNotContain("Created pet");
        await Assert.That(member.BodyHtml).Contains("<h3>application/json</h3>");
        await Assert.That(member.BodyHtml).Contains("Reference: <code>#/components/schemas/Pet~1~0%20name</code>");
        await Assert.That(member.BodyHtml).Contains("<code>name</code> (required)");
        await Assert.That(member.BodyHtml).Contains("Display &lt;name&gt;");
        await Assert.That(member.BodyHtml).Contains("literal default value");
        await Assert.That(member.BodyHtml).Contains("Save &lt;pet&gt; safely.");
        await Assert.That(member.BodyHtml).DoesNotContain("<pre>");
    }

    [Test]
    public async Task RecursiveSchemaReferencesRemainVisibleAndTerminate()
    {
        var member = Read("""
            {
              "openapi":"3.1.0",
              "paths":{"/tree":{"get":{"responses":{"200":{"description":"Tree","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Node"}}}}}}}},
              "components":{"schemas":{"Node":{"type":"object","properties":{"children":{"type":"array","items":{"$ref":"#/components/schemas/Node","description":"Nested node annotation"}}}}}}
            }
            """).Single();

        await Assert.That(member.BodyHtml).Contains("<code>children</code>");
        await Assert.That(member.BodyHtml).Contains("<strong>Items</strong>");
        await Assert.That(member.BodyHtml).Contains("Recursive schema reference.");
        await Assert.That(member.BodyHtml).Contains("Nested node annotation");
        await Assert.That(member.BodyHtml.Length < 3000).IsTrue();
    }

    [Test]
    public async Task InvalidReferencesFailWithExplicitDiagnostics()
    {
        foreach (var keyword in new[] { "$id", "$dynamicRef" })
            await Assert.That(() => Read("{\"openapi\":\"3.1.0\",\"paths\":{},\"components\":{\"schemas\":{\"Scoped\":{\"" + keyword + "\":\"#scoped\",\"type\":\"object\"}}}}"))
                .Throws<ArgumentException>().WithMessageContaining("Unsupported OpenAPI schema keyword " + keyword);
        foreach (var (reference, components, diagnostic) in new[]
        {
            ("https://example.invalid/schema.json", "{}", "External OpenAPI reference"),
            ("other.json#/Pet", "{}", "external APIs are not fetched"),
            ("#/components/schemas/Missing", "{}", "Unresolved OpenAPI reference"),
            ("#/components/schemas/Pet~2", "{}", "Invalid OpenAPI JSON pointer"),
            ("#/components/schemas/Pet%2", "{}", "Invalid OpenAPI JSON pointer"),
            ("#/components/schemas/A", "{\"schemas\":{\"A\":{\"$ref\":\"#/components/schemas/B\"},\"B\":{\"$ref\":\"#/components/schemas/A\"}}}", "Cyclic OpenAPI reference")
        })
        {
            var json = "{\"openapi\":\"3.1.0\",\"paths\":{\"/pets\":{\"get\":{\"responses\":{\"200\":{\"description\":\"OK\",\"content\":{\"application/json\":{\"schema\":{\"$ref\":\"" + reference
                + "\"}}}}}}}},\"components\":" + components + "}";
            await Assert.That(() => Read(json)).Throws<ArgumentException>().WithMessageContaining(diagnostic);
        }
        await Assert.That(() => Read("{\"openapi\":\"3.1.0\",\"paths\":{},\"components\":{\"examples\":{\"Pet\":{\"$ref\":\"https://example.invalid/pet.json\"}}}}"))
            .Throws<ArgumentException>().WithMessageContaining("External OpenAPI reference");
        await Assert.That(() => Read("{\"openapi\":\"3.1.0\",\"paths\":{\"/pets\":{\"get\":{\"responses\":{\"200\":{\"description\":\"OK\",\"content\":{\"application/json\":{\"examples\":{\"Pet\":{\"$ref\":\"https://example.invalid/pet.json\"}}}}}}}}}}"))
            .Throws<ArgumentException>().WithMessageContaining("External OpenAPI reference");
    }

    [Test]
    public async Task DuplicateKeysAndInvalidParameterShapesAreRejected()
    {
        await Assert.That(() => Read("{\"openapi\":\"3.1.0\",\"paths\":{},\"paths\":{}}"))
            .Throws<ArgumentException>().WithMessageContaining("Duplicate OpenAPI key");
        await Assert.That(() => Read("""{"openapi":"3.1.0","paths":{"/pets/{id}":{"get":{"parameters":[{"name":"id","in":"path","schema":{"type":"string"}}],"responses":{"200":{"description":"OK"}}}}}}"""))
            .Throws<ArgumentException>().WithMessageContaining("path parameter must be required");
        await Assert.That(() => Read("{\"openapi\":\"3.1.0\",\"paths\":{},\"components\":{\"schemas\":{\"Pet\":{\"$ref\":42}}}}"))
            .Throws<ArgumentException>().WithMessageContaining("$ref must be a string");
        foreach (var (operation, diagnostic) in new[]
        {
            ("\"parameters\":[{\"name\":\"q\",\"in\":\"query\"}],\"responses\":{\"200\":{\"description\":\"OK\"}}", "exactly one of schema or content"),
            ("\"parameters\":[{\"name\":\"q\",\"in\":\"query\",\"schema\":{},\"content\":{}}],\"responses\":{\"200\":{\"description\":\"OK\"}}", "exactly one of schema or content"),
            ("\"requestBody\":{},\"responses\":{\"200\":{\"description\":\"OK\"}}", "request body content is required"),
            ("\"responses\":{\"200\":{}}", "description is required"),
            ("\"responses\":{\"200\":{\"description\":\"OK\",\"content\":{\"application/json\":{\"schema\":{\"type\":\"invented\"}}}}}", "JSON schema type names"),
            ("\"responses\":{\"200\":{\"description\":\"OK\",\"content\":{\"application/json\":{\"schema\":{\"enum\":42}}}}}", "enum must be an array")
        })
            await Assert.That(() => Read("{\"openapi\":\"3.1.0\",\"paths\":{\"/pets\":{\"get\":{" + operation + "}}}}"))
                .Throws<ArgumentException>().WithMessageContaining(diagnostic);
        await Assert.That(() => Read("{\"openapi\":\"3.1.0\",\"paths\":{\"/pets\":{\"$ref\":\"#/components/pathItems/Pets\",\"parameters\":[]}},\"components\":{\"pathItems\":{\"Pets\":{\"parameters\":[]}}}}"))
            .Throws<ArgumentException>().WithMessageContaining("Ambiguous OpenAPI path reference sibling");
    }

    [Test]
    public async Task BooleanUnionAndComposedSchemasAreDisplayedWithoutInventingTypes()
    {
        var member = Read("""
            {
              "openapi":"3.1.0",
              "paths":{"/choice":{"get":{"responses":{"200":{"description":"Choice","content":{"application/json":{"schema":{"oneOf":[{"type":["string","null"],"enum":["a",null],"minLength":1},false,{"description":"Constraints supplied elsewhere"}]}}}}}}}}
            }
            """).Single();

        await Assert.That(member.BodyHtml).Contains("<strong>oneOf</strong>");
        await Assert.That(member.BodyHtml).Contains("<code>string | null</code>");
        await Assert.That(member.BodyHtml).Contains("No value is allowed.");
        await Assert.That(member.BodyHtml).Contains("Constraints supplied elsewhere");
        await Assert.That(member.BodyHtml).Contains("<dt>minLength</dt><dd><code>1</code>");
    }

    [Test]
    public async Task HttpDifferencesSeparatePublicContractsFromDocumentation()
    {
        const string baseline = """
            {
              "openapi":"3.1.0",
              "paths":{"/pets":{"get":{
                "summary":"Find pets",
                "parameters":[{"name":"name","in":"query","required":false,"description":"Filter name","schema":{"type":"string"}}],
                "responses":{"200":{"description":"Matching pets","content":{"application/json":{"schema":{"$ref":"#/components/schemas/Pet"},"example":{"$ref":"literal data, not a remote reference"}}}}}
              }}},
              "components":{"schemas":{"Pet":{"type":"object","properties":{"description":{"type":"string"}}}}}
            }
            """;
        var before = Read(baseline);
        var documentation = ApiReferenceSite.Compare(before, Read(baseline.Replace("Filter name", "Filter by display name", StringComparison.Ordinal)));
        await Assert.That(documentation.Select(change => change.Kind).ToArray()).IsEquivalentTo(new[] { "documentation" });
        var required = ApiReferenceSite.Compare(before, Read(baseline.Replace("\"required\":false", "\"required\":true", StringComparison.Ordinal)));
        await Assert.That(required.Select(change => change.Kind).ToArray()).IsEquivalentTo(new[] { "signature" });
        var response = ApiReferenceSite.Compare(before, Read(baseline.Replace("\"description\":{\"type\":\"string\"}", "\"description\":{\"type\":\"integer\"}", StringComparison.Ordinal)));
        await Assert.That(response.Select(change => change.Kind).ToArray()).IsEquivalentTo(new[] { "signature" });
        var obsolete = ApiReferenceSite.Compare(before, Read(baseline.Replace("\"required\":false", "\"required\":false,\"deprecated\":true", StringComparison.Ordinal)));
        await Assert.That(obsolete.Select(change => change.Kind).ToArray()).IsEquivalentTo(new[] { "obsolete" });
        var deprecated = ApiReferenceSite.Compare(before, Read(baseline.Replace("\"summary\":\"Find pets\"", "\"summary\":\"Find pets\",\"deprecated\":true", StringComparison.Ordinal)));
        await Assert.That(deprecated.Select(change => change.Kind).ToArray()).IsEquivalentTo(new[] { "deprecated" });
        var tags = ApiReferenceSite.Compare(before, Read(baseline.Replace("\"summary\":\"Find pets\"", "\"summary\":\"Find pets\",\"tags\":[\"pets\"]", StringComparison.Ordinal)));
        await Assert.That(tags.Select(change => change.Kind).ToArray()).IsEquivalentTo(new[] { "documentation" });
        var inline = ApiReferenceSite.Compare(before, Read(baseline.Replace("{\"$ref\":\"#/components/schemas/Pet\"}", "{\"type\":\"object\",\"properties\":{\"description\":{\"type\":\"string\"}}}", StringComparison.Ordinal)));
        await Assert.That(inline.Count).IsEqualTo(0);
        var reordered = ApiReferenceSite.Compare(
            Read("{\"openapi\":\"3.1.0\",\"paths\":{\"/pets\":{\"get\":{\"summary\":\"Pets\",\"responses\":{\"200\":{\"description\":\"OK\"}}}}}}"),
            Read("{\"paths\":{\"/pets\":{\"get\":{\"responses\":{\"200\":{\"description\":\"OK\"}},\"summary\":\"Pets\"}}},\"openapi\":\"3.1.0\"}"));
        await Assert.That(reordered.Count).IsEqualTo(0);
    }

    private static ApiMember[] Read(string json) => OpenApiReferenceReader.Read(Encoding.UTF8.GetBytes(json));
}
