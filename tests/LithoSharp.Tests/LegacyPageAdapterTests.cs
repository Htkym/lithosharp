using LithoSharp.Compatibility;
using LithoSharp.Content;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class LegacyPageAdapterTests
{
    [Test]
    public async Task MarkdownPostAdapter_PreservesHtmlRouteAndMapsMetadata()
    {
        var post = Post("guides\\日本語\\intro.html", new PostFrontMatter
        {
            Title = "Intro",
            Summary = "説明",
            Date = DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
            Draft = true,
            PublishFrom = DateTimeOffset.Parse("2026-09-01T00:00:00+09:00"),
            Environments = ["Production"]
        });

        var page = LegacyPageAdapters.ToSitePage(post, "https://example.test/docs/");

        await Assert.That(page.Route.PublicPath).IsEqualTo("/docs/guides/%E6%97%A5%E6%9C%AC%E8%AA%9E/intro.html");
        await Assert.That(page.Route.RelativeOutputPath).IsEqualTo("guides/日本語/intro.html");
        await Assert.That(page.Id.Value).IsEqualTo("markdown:guides/日本語/intro.html");
        await Assert.That(page.Metadata.Title).IsEqualTo("Intro");
        await Assert.That(page.Metadata.Description).IsEqualTo("説明");
        await Assert.That(page.Metadata.Draft).IsTrue();
        await Assert.That(page.Metadata.PublishFrom).IsEqualTo(DateTimeOffset.Parse("2026-08-31T15:00:00Z"));
        await Assert.That(page.Metadata.Environments).IsEquivalentTo(["Production"]);
    }

    [Test]
    public async Task ExtraPageAdapter_RetainsLegacyContentAndUsesDistinctStableId()
    {
        var extra = new SiteExtraPage
        {
            RelativePath = "about\\team.html",
            Title = "Team",
            BodyHtml = "<h1>Team</h1>",
            NavLabel = "People",
            NavCssClass = "team-link",
            IncludeInSitemap = false
        };

        var page = LegacyPageAdapters.ToSitePage(extra, "https://example.test/docs/");

        await Assert.That(page.Content).IsSameReferenceAs(extra);
        await Assert.That(page.Content.BodyHtml).IsEqualTo("<h1>Team</h1>");
        await Assert.That(page.Route.PublicPath).IsEqualTo("/docs/about/team.html");
        await Assert.That(page.Id.Value).IsEqualTo("extra:about/team.html");
        await Assert.That(page.Metadata.Title).IsEqualTo("Team");
        await Assert.That(page.Id.Equals(LegacyPageAdapters.ToSitePage(
            extra with { RelativePath = "about/team.html" }).Id)).IsTrue();
        await Assert.That(page.Id.Equals(new LithoSharp.Pages.PageId("markdown:about/team.html"))).IsFalse();
    }

    [Test]
    public async Task FilterPublished_UsesExplicitTimestampAndEnvironment()
    {
        var pages = new[]
        {
            LegacyPageAdapters.ToSitePage(Post("a.html", new PostFrontMatter
            {
                Title = "A",
                Date = DateTimeOffset.UtcNow,
                PublishFrom = DateTimeOffset.Parse("2026-08-30T10:00:00Z")
            })),
            LegacyPageAdapters.ToSitePage(Post("b.html", new PostFrontMatter
            {
                Title = "B",
                Date = DateTimeOffset.UtcNow,
                Environments = ["Staging"]
            }))
        };

        var published = LegacyPageAdapters.FilterPublished(
            pages,
            DateTimeOffset.Parse("2026-08-30T09:00:00Z"),
            "staging");

        await Assert.That(published).Count().IsEqualTo(1);
        await Assert.That(published[0].Content.FrontMatter.Title).IsEqualTo("B");
    }

    [Test]
    public async Task FrontMatterReader_ParsesPublicationAliases()
    {
        var frontMatter = MarkdownFrontMatterYaml.Deserialize("""
            title: Published later
            date: 2026-08-30T00:00:00Z
            draft: true
            publish_from: 2026-09-01T00:00:00Z
            publish_until: 2026-09-30T00:00:00Z
            environments:
              - Production
            """)!;

        await Assert.That(frontMatter.Draft).IsTrue();
        await Assert.That(frontMatter.PublishFrom).IsEqualTo(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        await Assert.That(frontMatter.PublishUntil).IsEqualTo(DateTimeOffset.Parse("2026-09-30T00:00:00Z"));
        await Assert.That(frontMatter.Environments).IsEquivalentTo(["Production"]);
    }

    private static MarkdownPost Post(string path, PostFrontMatter frontMatter) =>
        new("content/" + path, "post", frontMatter, "body", path);
}
