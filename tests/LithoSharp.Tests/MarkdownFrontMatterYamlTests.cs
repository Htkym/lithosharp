using LithoSharp.Content;

namespace LithoSharp.Tests;

public sealed class MarkdownFrontMatterYamlTests
{
    [Test]
    public async Task Serialize_RoundTripsPublicationWindowsAndDocumentedAliases()
    {
        var frontMatter = new PostFrontMatter
        {
            Title = "Scheduled post",
            Date = DateTimeOffset.Parse("2026-08-30T12:34:56.1234567+09:00"),
            Draft = true,
            PublishFrom = DateTimeOffset.Parse("2026-09-01T08:15:00.1234567+09:30"),
            PublishUntil = DateTimeOffset.Parse("2026-09-30T18:45:00.7654321-04:00"),
            SidebarPosition = 3,
            SidebarLabel = "Read this first",
            Environments = ["Production", "Staging"]
        };

        var yaml = MarkdownFrontMatterYaml.Serialize(frontMatter);
        var roundTripped = MarkdownFrontMatterYaml.Deserialize(yaml)!;

        await Assert.That(yaml).Contains("publish_from:");
        await Assert.That(yaml).Contains("publish_until:");
        await Assert.That(yaml).Contains("sidebar_position:");
        await Assert.That(yaml).Contains("sidebar_label:");
        await Assert.That(yaml).Contains("draft:");
        await Assert.That(yaml).Contains("environments:");
        await Assert.That(yaml).DoesNotContain("publishFrom");
        await Assert.That(yaml).DoesNotContain("publishUntil");
        await Assert.That(yaml).DoesNotContain("sidebarPosition");
        await Assert.That(yaml).DoesNotContain("sidebarLabel");
        await Assert.That(roundTripped.PublishFrom).IsEqualTo(frontMatter.PublishFrom);
        await Assert.That(roundTripped.PublishUntil).IsEqualTo(frontMatter.PublishUntil);
        await Assert.That(roundTripped.Draft).IsTrue();
        await Assert.That(roundTripped.SidebarPosition).IsEqualTo(3);
        await Assert.That(roundTripped.SidebarLabel).IsEqualTo("Read this first");
        await Assert.That(roundTripped.Environments).IsEquivalentTo(["Production", "Staging"]);
    }

    [Test]
    public async Task Deserialize_RejectsExcessiveRecursionAndRemainsReusable()
    {
        var deep = new string('[', 160) + "value" + new string(']', 160);
        await Assert.That(() => MarkdownFrontMatterYaml.Deserialize("title: " + deep))
            .Throws<FormatException>()
            .WithMessageContaining("limit");
        await Assert.That(MarkdownFrontMatterYaml.Deserialize("title: After rejection\ndate: 2026-10-04")!.Title)
            .IsEqualTo("After rejection");
    }

    [Test]
    public async Task Serialize_OmitsNullPublicationWindows()
    {
        var yaml = MarkdownFrontMatterYaml.Serialize(new PostFrontMatter
        {
            Title = "Unscheduled post",
            Date = DateTimeOffset.Parse("2026-08-30T00:00:00Z")
        });

        await Assert.That(yaml).DoesNotContain("publish_from:");
        await Assert.That(yaml).DoesNotContain("publish_until:");
        await Assert.That(MarkdownFrontMatterYaml.Deserialize(yaml)!.PublishFrom).IsNull();
        await Assert.That(MarkdownFrontMatterYaml.Deserialize(yaml)!.PublishUntil).IsNull();
    }

    [Test]
    public async Task Serialize_PreservesStringMeaningAndProducesDeterministicNewYaml()
    {
        var frontMatter = new PostFrontMatter
        {
            Title = "false",
            Summary = "null\n1.2\r\n2026-11-13\t\"quoted\" \\ 😀",
            Tags = ["null", "false", "1.2", "2026-11-13", "#tag: [value]"],
            Sources = [new() { Type = "article", Name = "null", Url = "https://example.test/a?q=1#part" }],
        };

        var yaml = MarkdownFrontMatterYaml.Serialize(frontMatter);
        var parsed = MarkdownFrontMatterYaml.Deserialize(yaml)!;

        await Assert.That(yaml).IsEqualTo(MarkdownFrontMatterYaml.Serialize(frontMatter));
        await Assert.That(yaml).DoesNotContain("\r");
        await Assert.That(parsed.Title).IsEqualTo(frontMatter.Title);
        await Assert.That(parsed.Summary).IsEqualTo(frontMatter.Summary);
        await Assert.That(parsed.Tags).IsEquivalentTo(frontMatter.Tags);
        await Assert.That(parsed.Sources.Single()).IsEqualTo(frontMatter.Sources.Single());
    }

    [Test]
    public async Task Deserialize_UsesStrictDtoMappingNamesAndNullSemantics()
    {
        var parsed = MarkdownFrontMatterYaml.Deserialize<NamedFrontMatter?>("custom_name: 'null'\noptional: null")!;

        await Assert.That(parsed.Title).IsEqualTo("null");
        await Assert.That(parsed.Optional).IsNull();
        await Assert.That(parsed.Ignored).IsEqualTo("kept");
        await Assert.That(() => MarkdownFrontMatterYaml.Deserialize<NamedFrontMatter>("custom_name: value\nignored: rejected"))
            .Throws<FormatException>().WithMessageContaining(ContentFrontMatterDiagnosticIds.UnknownField);
        await Assert.That(() => MarkdownFrontMatterYaml.Deserialize<NamedFrontMatter>("custom_name: null"))
            .Throws<FormatException>().WithMessageContaining(ContentFrontMatterDiagnosticIds.NullNotAllowed);
        await Assert.That(() => MarkdownFrontMatterYaml.Deserialize<NamedFrontMatter>("[value]"))
            .Throws<FormatException>().WithMessageContaining(MarkdownContentDiagnosticIds.InvalidFrontMatterRoot);
        await Assert.That(() => MarkdownFrontMatterYaml.Deserialize<NamedFrontMatter>("custom_name: [value]"))
            .Throws<FormatException>().WithMessageContaining(ContentFrontMatterDiagnosticIds.InvalidValue);
        await Assert.That(() => MarkdownFrontMatterYaml.Deserialize<object>("custom_name: value"))
            .Throws<NotSupportedException>();
        await Assert.That(() => MarkdownFrontMatterYaml.Deserialize<int?>("value: 1"))
            .Throws<NotSupportedException>();
    }

    private sealed class NamedFrontMatter
    {
        [ContentName("custom_name")]
        public required string Title { get; set; }

        public string? Optional { get; set; } = "default";

        [ContentIgnore]
        public string Ignored { get; set; } = "kept";
    }
}
