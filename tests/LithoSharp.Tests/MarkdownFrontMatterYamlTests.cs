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
}
