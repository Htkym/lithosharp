using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Testing;

namespace LithoSharp.Tests;

public sealed class DocsHeaderContentTests
{
    [Test]
    public async Task HeaderSlotsPreserveControlsAndInvalidateChangedOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var site = new SiteSettings { Title = "Guide", BaseUrl = "https://example.test/sub/", Language = "ja", TimeZone = "UTC" };
        var post = new MarkdownPost("guide.md", "guide", new PostFrontMatter
        {
            Title = "Guide",
            Summary = "Summary",
            Date = timestamp.AddDays(-1)
        }, "## Guide\n\nBody", "posts/guide.html");
        var version = "v1";
        var observed = new HashSet<(string?, string?)>();
        var customization = new SiteCustomization
        {
            Template = new DocsSiteTemplate
            {
                EnableHeaderSearch = true,
                HeaderContentRenderer = (url, locale) =>
                {
                    observed.Add((url, locale));
                    return new DocsHeaderContent
                    {
                        Navigation = Html.UnsafeRaw("<nav class=\"extra-navigation\">" + Html.Encode(version) + "</nav>"),
                        Actions = Html.UnsafeRaw("<a class=\"extra-action\" href=\"/languages\">Languages</a>")
                    };
                }
            },
            FaviconSourceDirectory = Path.Combine(workspace.Root, "favicon")
        };
        var generator = new SiteGenerator();
        var options = new SiteGenerationOptions { BuildTimestamp = timestamp };
        var first = await generator.GenerateWithOptionsAsync(site, [post], output, true, customization, options, CancellationToken.None);
        foreach (var page in new[] { "index.html", "404.html", "search.html", "posts/guide.html" })
        {
            using var document = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, page)));
            document.AssertText("header .extra-navigation", "v1");
            document.AssertText("header .extra-action", "Languages");
            await Assert.That(document.Snapshot.QueryAll("header [data-docs-menu-toggle]").Count).IsEqualTo(1);
            await Assert.That(document.Snapshot.QueryAll("header [data-site-theme-toggle]").Count).IsEqualTo(1);
            await Assert.That(document.Snapshot.QueryAll("header input[name=q]").Count).IsEqualTo(page == "search.html" ? 0 : 1);
        }
        await Assert.That(observed.Contains((null, "ja"))).IsTrue();
        await Assert.That(observed.Contains(("/sub/posts/guide.html", "ja"))).IsTrue();
        version = "v2 & updated";
        var second = await generator.GenerateWithOptionsAsync(site, [post], output, false, customization,
            options with { PreviousBuildPlan = first.BuildPlan }, CancellationToken.None);
        using (var home = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "index.html"))))
            home.AssertText("header .extra-navigation", version);
        var third = await generator.GenerateWithOptionsAsync(site, [post], output, false, customization,
            options with { PreviousBuildPlan = second.BuildPlan }, CancellationToken.None);
        await Assert.That(third.BuildReport.Invalidations).IsEmpty();
        await Assert.That(third.BuildReport.CacheMissCount).IsEqualTo(0);
    }
}
