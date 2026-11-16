using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Pages;

namespace LithoSharp.Tests;

public sealed class ApiAlternateMetadataTests
{
    [Test]
    public async Task AlternatesUseOnlyExistingSymbolsInTheSameCollectionAndVersion()
    {
        using var workspace = new TemporaryWorkspace();
        var layout = new CaptureLayout();
        var api = new ApiReferenceSite { Layout = layout, LayoutFingerprint = "alternate-test-v1" };
        foreach (var (collection, version, locale, ids) in new[]
        {
            ("library", "v1", "en", new[] { "T:Example.Shared", "T:Example.EnglishOnly" }),
            ("library", "v1", "ja", new[] { "T:Example.Shared" }),
            ("library", "v2", "fr", new[] { "T:Example.EnglishOnly" }),
            ("other", "v1", "de", new[] { "T:Example.Shared" })
        })
        {
            var file = Path.Combine(workspace.Root, collection + version + locale + ".xml");
            await File.WriteAllTextAsync(file, "<doc><members>" + string.Concat(ids.Select(id => "<member name=\"" + id + "\"><summary>Example.</summary></member>")) + "</members></doc>");
            api.AddXml(new(collection, new(version, locale, workspace.Root, $"api/{collection}/{version}/{locale}"), file));
        }
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.test/sub/" }, [], Path.Combine(workspace.Root, "out"), false,
            new() { Template = new DocsSiteTemplate() }, new() { Extensions = [api] }, default);
        var shared = layout.Pages.Single(page => page.Metadata.Document == new DocumentKey("library", "v1", "en", "T:Example.Shared"));
        await Assert.That(string.Join(',', shared.Metadata.Alternates.Keys.Order())).IsEqualTo("en,ja");
        var only = layout.Pages.Single(page => page.Metadata.Document == new DocumentKey("library", "v1", "en", "T:Example.EnglishOnly"));
        await Assert.That(only.Metadata.Alternates.Count).IsEqualTo(0);
        var index = layout.Pages.Single(page => page.Route.PublicPath == "/sub/api/library/v1/en/");
        await Assert.That(string.Join(',', index.Metadata.Alternates.Keys.Order())).IsEqualTo("en,ja");
        foreach (var page in layout.Pages)
            foreach (var alternate in page.Metadata.Alternates)
                await Assert.That(layout.Pages.Any(target => target.Metadata.Language == alternate.Key && target.Route.PublicPath == alternate.Value.Value)).IsTrue();
    }

    private sealed class CaptureLayout : IPageLayout<PageLayoutContent>
    {
        public List<SitePage<PageLayoutContent>> Pages { get; } = [];
        public IHtmlContent Render(SitePage<PageLayoutContent> page, PageRenderingContext context)
        {
            Pages.Add(page);
            return new DocsPageLayout().Render(page, context);
        }
    }
}
