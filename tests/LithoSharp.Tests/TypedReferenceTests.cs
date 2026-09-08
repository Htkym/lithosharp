using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class TypedReferenceTests
{
    [Test]
    public async Task ReferencesPreserveEscapedRoutesAndDirectoryShapeWhenRebased()
    {
        var route = SiteRoute.ForFile("guides/100%25.html", "https://example.test/old/");
        var page = new PageRef<string>(new PageId("page"), route);
        var entry = new ContentRef<ContentEntry<string, string>>(
            new ContentCollectionId("guides"), new ContentEntryId("entry"), route);
        await Assert.That(page.GetUrl().Value).IsEqualTo("/old/guides/100%25.html");
        await Assert.That(page.GetUrl("https://example.test/new/").Value).IsEqualTo("/new/guides/100%25.html");
        await Assert.That(entry.GetUrl("https://example.test/new/").Value).IsEqualTo("/new/guides/100%25.html");
        var directory = new PageRef<string>(new PageId("directory"), SiteRoute.ForDirectoryIndex("100%25"));
        await Assert.That(directory.GetUrl("https://example.test/sub/").Value).IsEqualTo("/sub/100%25/");
        await Assert.That(() => page.GetUrl("javascript:alert(1)")).Throws<UriFormatException>();
        await Assert.That(() => new ContentRef<string>(null!, new ContentEntryId("id"), route)).Throws<ArgumentNullException>();
    }
}
