using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.Inspection;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

public sealed class FrontMatterSchemaTests
{
    private enum FixturePriority
    {
        Low,
        High,
    }

    private sealed class FixtureChild
    {
        public string Name { get; set; } = "default";
    }

    private sealed class FixtureFrontMatter
    {
        public required string Title { get; init; }

        [YamlDotNet.Serialization.YamlMember(Alias = "custom_name")]
        public string? Renamed { get; set; }

        public FixturePriority Priority { get; set; } = FixturePriority.Low;

        [System.ComponentModel.Description("A noted field.")]
        public string? Noted { get; set; }

        [Obsolete("Use Noted.")]
        public string? Legacy { get; set; }

        public FixtureChild Nested { get; set; } = new();

        public List<string> Tags { get; set; } = [];

        public int Count { get; set; }
    }

    [Test]
    public async Task DocumentSchemaMatchesBinder()
    {
        var schema = FrontMatterSchemas.Document;

        await Assert.That(schema.Name).IsEqualTo("document");
        await Assert.That(schema.IsBuiltIn).IsEqualTo(true);
        await Assert.That(schema.FrontMatterType).IsEqualTo(typeof(DocumentFrontMatter));
        await Assert.That(schema.RejectUnknownFields).IsEqualTo(true);

        var byKey = schema.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        await Assert.That(byKey["title"].Type).IsEqualTo("string");
        await Assert.That(byKey["title"].Description).IsEqualTo("The page title.");
        await Assert.That(byKey["tags"].Type).IsEqualTo("array");
        await Assert.That(byKey["tags"].ItemType).IsEqualTo("string");
        await Assert.That(byKey["keywords"].Type).IsEqualTo("array");
        await Assert.That(byKey["keywords"].ItemType).IsEqualTo("string");
        await Assert.That(byKey["toc_max_heading_level"].Type).IsEqualTo("integer");
        await Assert.That(byKey["draft"].Type).IsEqualTo("boolean");
        await Assert.That(byKey["sidebar_position"].Type).IsEqualTo("number");
        await Assert.That(byKey["publish_from"].Type).IsEqualTo("date-time");

        var lastUpdate = byKey["last_update"];
        await Assert.That(lastUpdate.Type).IsEqualTo("object");
        var nested = lastUpdate.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        await Assert.That(nested["date"].Type).IsEqualTo("date-time");
        await Assert.That(nested["author"].Description).IsEqualTo("The public author display name.");

        foreach (var field in schema.Fields)
        {
            await Assert.That(string.IsNullOrWhiteSpace(field.Description)).IsEqualTo(false);
            await Assert.That(field.Deprecated).IsEqualTo(false);
            await Assert.That(field.DeprecationMessage).IsNull();
        }

        await CheckConsistencyAsync(schema, new ReflectionContentFrontMatterBinder<DocumentFrontMatter>());
    }

    [Test]
    public async Task PostSchemaMatchesBinder()
    {
        var schema = FrontMatterSchemas.Post;

        await Assert.That(schema.Name).IsEqualTo("post");
        await Assert.That(schema.IsBuiltIn).IsEqualTo(true);
        await Assert.That(schema.FrontMatterType).IsEqualTo(typeof(PostFrontMatter));

        var byKey = schema.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        await Assert.That(byKey["date"].Type).IsEqualTo("date-time");
        await Assert.That(byKey["sidebar_position"].Type).IsEqualTo("integer");
        await Assert.That(byKey["environments"].Type).IsEqualTo("array");
        await Assert.That(byKey["environments"].ItemType).IsEqualTo("string");
        await Assert.That(byKey["draft"].Description).IsEqualTo("Whether the post is a draft and stays unpublished.");

        var sources = byKey["sources"];
        await Assert.That(sources.Type).IsEqualTo("array");
        await Assert.That(sources.ItemType).IsEqualTo("object");
        var nested = sources.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        await Assert.That(nested["type"].Description).IsEqualTo("Kind of source (for example feed, article, or repo).");
        await Assert.That(nested["url"].AllowsNull).IsEqualTo(true);

        foreach (var field in schema.Fields)
        {
            await Assert.That(string.IsNullOrWhiteSpace(field.Description)).IsEqualTo(false);
        }

        await CheckConsistencyAsync(schema, new ReflectionContentFrontMatterBinder<PostFrontMatter>());
    }

    [Test]
    public async Task CustomSchemaExposesEnumDeprecatedDescriptionAndNested()
    {
        var schema = FrontMatterSchemas.For<FixtureFrontMatter>();

        await Assert.That(schema.IsBuiltIn).IsEqualTo(false);
        await Assert.That(schema.Name).IsEqualTo(nameof(FixtureFrontMatter));
        await Assert.That(schema.FrontMatterType).IsEqualTo(typeof(FixtureFrontMatter));

        var byKey = schema.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        await Assert.That(byKey["title"].Required).IsEqualTo(true);
        await Assert.That(byKey["custom_name"].Required).IsEqualTo(false);
        await Assert.That(byKey["custom_name"].AllowsNull).IsEqualTo(true);
        await Assert.That(byKey.ContainsKey("renamed")).IsEqualTo(false);

        var priority = byKey["priority"];
        await Assert.That(priority.Type).IsEqualTo("enum");
        await Assert.That(priority.EnumValues).IsEquivalentTo(["High", "Low"]);

        var noted = byKey["noted"];
        await Assert.That(noted.Description).IsEqualTo("A noted field.");
        await Assert.That(noted.Deprecated).IsEqualTo(false);

        var legacy = byKey["legacy"];
        await Assert.That(legacy.Deprecated).IsEqualTo(true);
        await Assert.That(legacy.DeprecationMessage).IsEqualTo("Use Noted.");
        await Assert.That(legacy.Description).IsNull();

        var nested = byKey["nested"];
        await Assert.That(nested.Type).IsEqualTo("object");
        await Assert.That(nested.Fields.Count).IsEqualTo(1);
        await Assert.That(nested.Fields[0].Key).IsEqualTo("name");

        await Assert.That(byKey["tags"].Type).IsEqualTo("array");
        await Assert.That(byKey["tags"].ItemType).IsEqualTo("string");
        await Assert.That(byKey["count"].Type).IsEqualTo("integer");

        await CheckConsistencyAsync(schema, new ReflectionContentFrontMatterBinder<FixtureFrontMatter>());
    }

    [Test]
    public async Task UserDefinedMdxBlogSchemaMatchesBinder()
    {
        var schema = FrontMatterSchemas.For<MdxBlogFrontMatter>();

        await Assert.That(schema.IsBuiltIn).IsEqualTo(false);
        var byKey = schema.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        await Assert.That(byKey["title"].Type).IsEqualTo("string");
        await Assert.That(byKey["authors"].Type).IsEqualTo("array");
        await Assert.That(byKey["image"].Type).IsEqualTo("string");
        await Assert.That(byKey["description"].Type).IsEqualTo("string");

        await CheckConsistencyAsync(schema, new ReflectionContentFrontMatterBinder<MdxBlogFrontMatter>());
    }

    [Test]
    public async Task BuiltInAndCustomAreDistinguished()
    {
        await Assert.That(FrontMatterSchemas.Document.IsBuiltIn).IsEqualTo(true);
        await Assert.That(FrontMatterSchemas.Post.IsBuiltIn).IsEqualTo(true);
        await Assert.That(FrontMatterSchemas.Document.Name).IsNotEqualTo(FrontMatterSchemas.Post.Name);
        await Assert.That(FrontMatterSchemas.For<FixtureFrontMatter>().IsBuiltIn).IsEqualTo(false);
    }

    private static async Task CheckConsistencyAsync<TFrontMatter>(
        FrontMatterSchema schema, ReflectionContentFrontMatterBinder<TFrontMatter> binder)
        where TFrontMatter : notnull
    {
        var full = MappingFromFields(schema.Fields);
        await Assert.That(binder.Bind(full).IsSuccess).IsEqualTo(true);

        foreach (var required in schema.Fields.Where(field => field.Required))
        {
            var missing = new Dictionary<string, object?>(full, StringComparer.Ordinal);
            missing.Remove(required.Key);
            var result = binder.Bind(missing);
            await Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Id == ContentFrontMatterDiagnosticIds.MissingRequiredField))
                .IsEqualTo(true);
        }

        var unknown = new Dictionary<string, object?>(full, StringComparer.Ordinal)
        {
            ["unknown_field_xyz"] = true,
        };
        var rejected = binder.Bind(unknown);
        await Assert.That(rejected.Diagnostics.Any(diagnostic => diagnostic.Id == ContentFrontMatterDiagnosticIds.UnknownField))
            .IsEqualTo(true);

        var empty = binder.Bind(new Dictionary<string, object?>(StringComparer.Ordinal));
        var expectedMissing = schema.Fields.Where(field => field.Required).Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        if (expectedMissing.Count == 0)
        {
            await Assert.That(empty.IsSuccess).IsEqualTo(true);
        }
        else
        {
            await Assert.That(empty.Diagnostics.Count(diagnostic => diagnostic.Id == ContentFrontMatterDiagnosticIds.MissingRequiredField))
                .IsEqualTo(expectedMissing.Count);
        }
    }

    private static Dictionary<string, object?> MappingFromFields(IReadOnlyList<FrontMatterFieldInfo> fields)
    {
        var mapping = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            mapping[field.Key] = DummyValue(field);
        }
        return mapping;
    }

    private static object? DummyValue(FrontMatterFieldInfo field) => field.Type switch
    {
        "boolean" => true,
        "integer" => 1,
        "number" => 1.5,
        "date-time" => "2026-01-02T03:04:05Z",
        "date" => "2026-01-02",
        "time" => "03:04:05",
        "uuid" => "12345678-1234-1234-1234-123456789012",
        "uri" => "https://example.test/docs/",
        "enum" => field.EnumValues[0],
        "array" => new List<object?> { DummyElement(field) },
        "dictionary" => new Dictionary<string, object?> { ["k"] = DummyByName(field.ItemType, field.Fields) },
        "object" => MappingFromFields(field.Fields),
        _ => "x",
    };

    private static object? DummyElement(FrontMatterFieldInfo field) =>
        field.Fields.Count != 0 ? MappingFromFields(field.Fields) : DummyByName(field.ItemType, []);

    private static object? DummyByName(string? type, IReadOnlyList<FrontMatterFieldInfo> fields) => type switch
    {
        "boolean" => true,
        "integer" => 1,
        "number" => 1.5,
        "date-time" => "2026-01-02T03:04:05Z",
        "date" => "2026-01-02",
        "time" => "03:04:05",
        "uuid" => "12345678-1234-1234-1234-123456789012",
        "uri" => "https://example.test/docs/",
        "enum" => "x",
        "array" => new List<object?> { "x" },
        "dictionary" => new Dictionary<string, object?> { ["k"] = "x" },
        "object" => MappingFromFields(fields),
        _ => "x",
    };
}
