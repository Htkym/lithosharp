using LithoSharp.Configuration;
using LithoSharp.Quality;

namespace LithoSharp.Tests;

public sealed class DeliveryTests
{
    [Test]
    [Arguments(false, "/")]
    [Arguments(true, "/")]
    [Arguments(false, "/product/")]
    [Arguments(true, "/product/")]
    public async Task BuiltInErrorDocumentUsesPublishedPathsAndExternalTheme(bool docs, string basePath)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var result = await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { Title = "Delivery", BaseUrl = "https://example.test" + basePath, Language = "en", TimeZone = "UTC" },
            [], output, true,
            new SiteCustomization
            {
                Template = docs ? new DocsSiteTemplate() : new BlogSiteTemplate(),
                Theme = new SiteThemeOptions { UseExternalThemeRestoration = true },
                Text = SiteText.English with { NotFoundTitle = "Missing <page>", NotFoundHomeLinkLabel = "Home & start" },
                FaviconSourceDirectory = Path.Combine(workspace.Root, "no-favicons")
            },
            new SiteGenerationOptions { Quality = new SiteQualityOptions() }, CancellationToken.None);

        var html = await File.ReadAllTextAsync(Path.Combine(output, "404.html"));
        await Assert.That(html).Contains("<h1>Missing &lt;page&gt;</h1>");
        await Assert.That(html).Contains($"href=\"{basePath}index.html\">Home &amp; start</a>");
        await Assert.That(html).Contains($"href=\"{basePath}assets/site.css\"");
        await Assert.That(html).Contains($"<script src=\"{basePath}assets/theme.js\"></script>");
        await Assert.That(html).Contains("<meta name=\"robots\" content=\"noindex\">");
        await Assert.That(html).DoesNotContain("<script>");
        await Assert.That(html.IndexOf("assets/theme.js", StringComparison.Ordinal))
            .IsLessThan(html.IndexOf("assets/site.css", StringComparison.Ordinal));
        await Assert.That(result.BuildPlan.Nodes.Single(node => node.Id.Value == "page:not-found").Artifacts.Single().RelativeOutputPath)
            .IsEqualTo("404.html");
        await Assert.That(result.QualityReport.Diagnostics.Any(item => item.Id == "LSQ007" && item.Location?.FilePath.EndsWith("404.html", StringComparison.Ordinal) == true)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "assets", "theme.js"))).Contains("localStorage.getItem('lithosharp-theme')");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "assets", "site.js"))).Contains("data-site-menu-toggle");
    }

    [Test]
    public async Task ThemeRestorationModeChangesInvalidateHtmlAndRemoveUnusedAsset()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var site = new SiteSettings { Title = "Delivery", BaseUrl = "https://example.test/sub/", Language = "en", TimeZone = "UTC" };
        var customization = new SiteCustomization { FaviconSourceDirectory = Path.Combine(workspace.Root, "no-favicons") };
        var generator = new SiteGenerator();
        var options = new SiteGenerationOptions { BuildTimestamp = DateTimeOffset.UnixEpoch };
        foreach (var external in new[] { false, true, false })
        {
            var generated = await generator.GenerateWithOptionsAsync(site, [], output, false,
                customization with { Theme = new SiteThemeOptions { UseExternalThemeRestoration = external } }, options, CancellationToken.None);
            var html = await File.ReadAllTextAsync(Path.Combine(output, "404.html"));
            await Assert.That(html.Contains("<script>", StringComparison.Ordinal)).IsEqualTo(!external);
            await Assert.That(File.Exists(Path.Combine(output, "assets", "theme.js"))).IsEqualTo(external);
            await Assert.That(generated.BuildPlan.Nodes.Any(node => node.Id.Value == "asset:theme-restoration-script")).IsEqualTo(external);
        }
    }
}
