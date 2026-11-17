using LithoSharp.Configuration;
using LithoSharp.Pages;
using LithoSharp.Routing;
using LithoSharp.Testing;

namespace LithoSharp.Tests;

public sealed class DocsUtilityLayoutTests
{
    [Test]
    public async Task OverriddenUtilityPagesUseDocsHeaderWithoutSidebars()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await new SiteGenerator().GenerateAsync(
            new SiteSettings { Title = "Example", BaseUrl = "https://example.test/sub/" }, [], output, true,
            new SiteCustomization { Template = new UtilityTemplate() });
        foreach (var path in new[] { "index.html", "404.html", "search.html" })
        {
            using var document = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, path)));
            await Assert.That(document.Snapshot.QueryAll(".docs-header .version").Count).IsEqualTo(1);
            await Assert.That(document.Snapshot.QueryAll(".docs-header .language").Count).IsEqualTo(1);
            await Assert.That(document.Snapshot.QueryAll("[data-docs-menu-toggle]").Count).IsEqualTo(1);
            await Assert.That(document.Snapshot.QueryAll("[data-site-theme-toggle]").Count).IsEqualTo(1);
            await Assert.That(document.Snapshot.QueryAll("#docs-sidebar").Count).IsEqualTo(0);
            await Assert.That(document.Snapshot.QueryAll("#docs-main").Count).IsEqualTo(1);
            await Assert.That(document.Snapshot.QueryAll("#blog-main").Count).IsEqualTo(0);
            document.AssertAttribute(".docs-brand", "href", "/sub/index.html");
            await Assert.That(document.Snapshot.QueryAll("header input[name=q]").Count).IsEqualTo(path == "search.html" ? 0 : 1);
            await Assert.That(document.Snapshot.QueryAll("script[src='/sub/assets/site.js']").Count).IsEqualTo(1);
        }
    }

    private sealed class UtilityTemplate : DocsSiteTemplate
    {
        public UtilityTemplate()
        {
            EnableHeaderSearch = true;
            HeaderContentRenderer = (_, _) => new DocsHeaderContent
            {
                Navigation = Html.UnsafeRaw("<span class=\"version\">v3</span>"),
                Actions = Html.UnsafeRaw("<span class=\"language\">English</span>")
            };
            NavigationRenderer = (_, _) => Html.UnsafeRaw("<aside id=\"docs-sidebar\" class=\"docs-sidebar\">Navigation</aside>");
        }

        public override async Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default)
        {
            var standard = await base.RenderAsync(context, cancellationToken);
            var paths = new[] { "index.html", "404.html", "search.html" };
            var utilities = paths.Select(path =>
            {
                var route = SiteRoute.ForFile(path, context.Site.BaseUrl);
                var content = new PageLayoutContent(Html.UnsafeRaw("<h1>Utility</h1>"));
                var page = new SitePage<PageLayoutContent>(new PageId("utility:" + path), route, content, new PageMetadata("Utility"));
                return new SiteTemplateFile { RelativePath = path, Content = context.RenderLayout(page, new DocsPageLayout()).ToHtmlString() };
            });
            return new SiteTemplateResult([.. standard.Files.Where(file => !paths.Contains(file.RelativePath)), .. utilities]);
        }
    }
}