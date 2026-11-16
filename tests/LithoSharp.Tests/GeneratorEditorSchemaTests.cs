using System.Text.Json.Nodes;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Inspection;

namespace LithoSharp.Tests;

public sealed partial class GeneratorTests
{
    private const string EditorSchemaModel = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using LithoSharp.Content;
        public enum Priority { Low, High }
        public sealed class Front {
            public Front() => throw new InvalidOperationException("Editor must not instantiate this type.");
            [ContentName("heading"), Description("The custom heading.")]
            public required string Title { get => throw new InvalidOperationException(); set => throw new InvalidOperationException(); }
            public Priority Priority { get; set; }
            public List<int> Numbers { get; set; } = [];
            public string ConstructorDependent { get; set; } = default!;
        }
        [StaticContentCollection(typeof(Front), typeof(string), "guides")]
        public static partial class Guides { }
        """;

    [Test]
    public async Task GeneratedSchemaDrivesStaticInspectionAndInvalidatesOnRevisionChanges()
    {
        var schemaJson = CompileEditorSchema(EditorSchemaModel);
        var path = Path.GetFullPath("editor-intro.md");
        var hash = "sha256:" + new string('1', 64);
        var schema = FrontMatterSchemaSnapshot.Create("project", 3, hash, "guides", schemaJson, [path]);
        var project = ProjectInspectionSnapshot.Create("project", 3, "guides", "markdown", "Front", "current", "en")
            .WithSchemas(hash, [schema]);
        var text = "---\nheading: Hello\npriority: Urgent\nnumbers:\n  - 1\n  - nope\nextra: rejected\n---\n# Body";
        await using var workspace = new DocumentWorkspace();
        var info = await workspace.InspectVersionedAsync(path, text, 1, 3, new() { Project = project });

        await Assert.That(info.SchemaInspectionScope).IsEqualTo("static-schema-build-required");
        await Assert.That(schema.Fields.Single(field => field.Key == "heading").Description).IsEqualTo("The custom heading.");
        await Assert.That(schema.Fields.Single(field => field.Key == "heading").Required).IsTrue();
        await Assert.That(schema.Fields.Single(field => field.Key == "constructor_dependent").RequiresBuildValidation).IsTrue();
        await Assert.That(info.Diagnostics.Where(diagnostic => diagnostic.Id == ContentFrontMatterDiagnosticIds.InvalidValue)
            .Select(diagnostic => diagnostic.Location!.Line)).IsEquivalentTo(new int?[] { 3, 6 });
        await Assert.That(info.Diagnostics.Single(diagnostic => diagnostic.Id == ContentFrontMatterDiagnosticIds.UnknownField).Location!.Line).IsEqualTo(7);

        var changedSchema = CompileEditorSchema(EditorSchemaModel.Replace("Low, High", "Low, High, Urgent", StringComparison.Ordinal));
        var nextHash = "sha256:" + new string('2', 64);
        var updated = ProjectInspectionSnapshot.Create("project", 4, "guides", "markdown", "Front", "current", "en")
            .WithSchemas(nextHash, [FrontMatterSchemaSnapshot.Create("project", 4, nextHash, "guides", changedSchema, [path])]);
        var next = await workspace.InspectVersionedAsync(path, text, 1, 4, new() { Project = updated });
        await Assert.That(next.Diagnostics.Count(diagnostic => diagnostic.Id == ContentFrontMatterDiagnosticIds.InvalidValue)).IsEqualTo(1);
        await Assert.That(next.StaticSchema!.SchemaHash == schema.SchemaHash).IsFalse();
    }

    [Test]
    public async Task SchemaSnapshotsRejectWrongRevisionHashesAndUnlistedInputs()
    {
        var path = Path.GetFullPath("editor-intro.md");
        var hash = "sha256:" + new string('1', 64);
        var schema = FrontMatterSchemaSnapshot.Create("project", 3, hash, "guides", CompileEditorSchema(EditorSchemaModel), [path]);
        var project = ProjectInspectionSnapshot.Create("project", 3, "guides", "markdown", "Front", "current", "en").WithSchemas(hash, [schema]);
        var restored = ProjectInspectionSnapshot.ParseJson(project.ToJson());
        await Assert.That(restored.Schemas.Single().InputSetHash).IsEqualTo(schema.InputSetHash);
        var outside = DocumentInspection.Inspect(Path.Combine(Path.GetDirectoryName(path)!, "other", "editor-intro.md"),
            "---\nextra: accepted without schema evidence\n---\nBody", new() { Project = restored });
        await Assert.That(outside.SchemaInspectionScope).IsEqualTo("build-required");
        await Assert.That(outside.Diagnostics.Any(diagnostic => diagnostic.Id == ContentFrontMatterDiagnosticIds.UnknownField)).IsFalse();

        foreach (var field in new[] { "schemaVersion", "schemaHash", "inputSetHash", "projectGeneration" })
        {
            var json = JsonNode.Parse(project.ToJson())!;
            json["schemas"]![0]![field] = field == "projectGeneration" ? JsonValue.Create(4) : JsonValue.Create("invalid");
            await Assert.That(() => ProjectInspectionSnapshot.ParseJson(json.ToJsonString())).Throws<ArgumentException>();
        }
        await Assert.That(() => FrontMatterSchemaSnapshot.Create("project", 3, hash, "guides", schema.SchemaJson, ["editor-intro.md"]))
            .Throws<ArgumentException>();
        await Assert.That(() => FrontMatterSchemaSnapshot.Create("project", 3, hash, "guides", schema.SchemaJson,
            [path, Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(path).ToUpperInvariant())]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task NullableObjectSchemasPreserveAnnotationsWithoutValueConstraints()
    {
        const string source = """
            #nullable enable
            using System;
            using System.ComponentModel;
            using LithoSharp.Content;
            public sealed class Front {
                public object Payload { get; set; } = new();
                public object? Optional { get; set; }
                [Description("An optional \"quoted\" object.")]
                public object? Described { get; set; }
                [Obsolete("Use Optional.")]
                public object? Legacy { get; set; }
                [Description("An optional \"quoted\" object."), Obsolete("Use Optional.")]
                public object? DescribedLegacy { get; set; }
                [ContentRequiredWhen("mode", "enabled")]
                public object? Conditional { get; set; }
                public string? Mode { get; set; }
            }
            [StaticContentCollection(typeof(Front), typeof(string), "guides")]
            public static partial class Guides { }
            """;
        using var document = System.Text.Json.JsonDocument.Parse(CompileEditorSchema(source));
        var properties = document.RootElement.GetProperty("$defs").EnumerateObject().Single().Value.GetProperty("properties");
        foreach (var name in new[] { "optional", "described", "legacy", "described_legacy", "conditional" })
        {
            var schema = properties.GetProperty(name);
            await Assert.That(schema.EnumerateObject().All(property => property.Name is
                "description" or "deprecated" or "x-lithosharp-requiredWhen" or "x-lithosharp-buildRequired")).IsTrue();
            await Assert.That(schema.GetProperty("x-lithosharp-buildRequired").GetBoolean()).IsFalse();
            await Assert.That(schema.GetProperty("x-lithosharp-requiredWhen").GetArrayLength()).IsEqualTo(name == "conditional" ? 1 : 0);
        }
        await Assert.That(properties.GetProperty("described").GetProperty("description").GetString()).IsEqualTo("An optional \"quoted\" object.");
        await Assert.That(properties.GetProperty("legacy").GetProperty("deprecated").GetBoolean()).IsTrue();
        await Assert.That(properties.GetProperty("described_legacy").GetProperty("description").GetString()).IsEqualTo("An optional \"quoted\" object.");
        await Assert.That(properties.GetProperty("described_legacy").GetProperty("deprecated").GetBoolean()).IsTrue();
        var condition = properties.GetProperty("conditional").GetProperty("x-lithosharp-requiredWhen")[0];
        await Assert.That(condition.GetProperty("field").GetString()).IsEqualTo("mode");
        await Assert.That(condition.GetProperty("equals").GetString()).IsEqualTo("enabled");
        await Assert.That(properties.GetProperty("payload").GetProperty("not").GetProperty("type").GetString()).IsEqualTo("null");
    }

    private static string CompileEditorSchema(string source)
    {
        var (compilation, diagnostics) = Generate(source);
        if (Errors(diagnostics) is { Length: > 0 } generatorErrors)
            throw new InvalidOperationException(generatorErrors);
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
            throw new InvalidOperationException(Errors(result.Diagnostics));
        return (string)System.Reflection.Assembly.Load(stream.ToArray()).GetType("Guides")!.GetField("SchemaJson")!.GetRawConstantValue()!;
    }
}
