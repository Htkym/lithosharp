using LithoSharp.AgentReady;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class AgentReadyUrlResolverTests
{
    private const string BaseUrl = "https://example.test/product/";
    private static readonly SiteRoute Page = SiteRoute.ForFile("ja/v3/guide.html", BaseUrl);
    private static readonly SiteRoute Other = SiteRoute.ForDirectoryIndex("en/v4/renamed", BaseUrl);
    private static readonly SiteRoute Image = SiteRoute.ForFile("assets/図%20(1).abc.png", BaseUrl);

    private static PublicMarkdownUrlResolver Create() => new(BaseUrl,
        [new("content/ja/start.md", Page), new("content/en/target.md", Other)],
        [new("includes/images/図 (1).png", Image)],
        ["includes/nested/part.md"]);

    [Test]
    [Arguments("../en/target.md?x=%26&y=日本語#見出し", "https://example.test/product/en/v4/renamed/?x=%26&y=日本語#見出し")]
    [Arguments("#見出し", "https://example.test/product/ja/v3/guide.html#見出し")]
    [Arguments("?x=%2520&y=2", "https://example.test/product/ja/v3/guide.html?x=%2520&y=2")]
    [Arguments("", "https://example.test/product/ja/v3/guide.html")]
    [Arguments("/product/en/v4/renamed/index.html#part", "https://example.test/product/en/v4/renamed/#part")]
    [Arguments("../../en/v4/renamed/", "https://example.test/product/en/v4/renamed/")]
    public async Task ResolvesActualPublishedRoutes(string target, string expected)
    {
        await Assert.That(Create().Resolve(target, "content/ja/start.md", Page)).IsEqualTo(expected);
    }

    [Test]
    public async Task IncludesUseTheirOwnSourceForAssetsAndDocuments()
    {
        var resolver = Create();
        await Assert.That(resolver.Resolve("../images/%E5%9B%B3%20(1).png?x=1&y=%26#part", "includes/nested/part.md", Page))
            .IsEqualTo("https://example.test/product/assets/%E5%9B%B3%20%281%29.abc.png?x=1&y=%26#part");
        await Assert.That(resolver.Resolve("../../content/en/target.md", "includes/nested/part.md", Page))
            .IsEqualTo("https://example.test/product/en/v4/renamed/");
        await Assert.That(resolver.Resolve("#local", "includes/nested/part.md", Page))
            .IsEqualTo("https://example.test/product/ja/v3/guide.html#local");
    }

    [Test]
    [Arguments("https://elsewhere.test/a%2Fb?q=%2520&x=1#日本語")]
    [Arguments("mailto:reader@example.test?subject=Hello%20World")]
    [Arguments("tel:+123456789")]
    public async Task SafeAbsoluteTargetsRemainUnchanged(string target)
    {
        await Assert.That(Create().Resolve(target, "unknown", Page)).IsEqualTo(target);
    }

    [Test]
    [Arguments("javascript:alert(1)")]
    [Arguments("data:text/html,body")]
    [Arguments("file:///C:/private/key.md")]
    [Arguments("C:/private/key.md")]
    [Arguments("https://user:password@example.test/")]
    [Arguments("//user:password@example.test/")]
    [Arguments("https://example.test/\npath")]
    [Arguments("../missing.md")]
    [Arguments("../missing.mdx")]
    [Arguments("../missing.png")]
    [Arguments("/private/source.md")]
    [Arguments("../images%2Fsecret.png")]
    [Arguments("../images%5Csecret.png")]
    [Arguments("../images\\secret.png")]
    public async Task UnsafeOrUnregisteredTargetsFailClosed(string target)
    {
        await Assert.That(Create().Resolve(target, "content/ja/start.md", Page)).IsNull();
    }

    [Test]
    public async Task MissingSourceAndUnpublishedCurrentPageFailClosed()
    {
        var resolver = Create();
        await Assert.That(resolver.Resolve("../en/target.md", "unknown.md", Page)).IsNull();
        await Assert.That(resolver.Resolve("#part", "unknown.md", Page)).IsNull();
        await Assert.That(resolver.Resolve("#part", "content/ja/start.md", SiteRoute.ForFile("hidden.html", BaseUrl))).IsNull();
    }

    [Test]
    public async Task ConflictingSourceRegistrationsAreNotResolvedByEnumerationOrder()
    {
        var resolver = new PublicMarkdownUrlResolver(BaseUrl,
            [new("page.md", Page), new("target.md", Other), new("target.md", Page)], []);
        await Assert.That(resolver.Resolve("target.md", "page.md", Page)).IsNull();
        await Assert.That(resolver.Resolve("#part", "target.md", Page)).IsNull();
    }

    [Test]
    public async Task CatalogIsCopiedAndSourceNamesCannotLeak()
    {
        var source = Path.Combine(Path.GetTempPath(), "agent-ready", "source.md");
        var target = Path.Combine(Path.GetTempPath(), "agent-ready", "secret-name.md");
        var pages = new List<KeyValuePair<string, SiteRoute>> { new(source, Page), new(target, Other) };
        var resolver = new PublicMarkdownUrlResolver(BaseUrl, pages, []);
        pages.Clear();
        await Assert.That(resolver.Resolve("secret-name.md", source, Page))
            .IsEqualTo("https://example.test/product/en/v4/renamed/");
    }

    [Test]
    public async Task LiteralPercentFilenameIsDecodedExactlyOnce()
    {
        var route = SiteRoute.ForFile("assets/100%25.txt", BaseUrl);
        var resolver = new PublicMarkdownUrlResolver(BaseUrl, [new("source.md", Page)], [new("100%.txt", route)]);
        await Assert.That(resolver.Resolve("100%25.txt?q=%2525&x=1", "source.md", Page))
            .IsEqualTo("https://example.test/product/assets/100%25.txt?q=%2525&x=1");
    }

    [Test]
    public async Task NetworkPathUsesSiteSchemeWithoutRetainingRelativeDestination()
    {
        await Assert.That(Create().Resolve("//cdn.test/a.png?x=1&y=2", "content/ja/start.md", Page))
            .IsEqualTo("https://cdn.test/a.png?x=1&y=2");
    }

    [Test]
    [Arguments("https://example.test/日本語/")]
    [Arguments("https://example.test/%E6%97%A5%E6%9C%AC%E8%AA%9E/")]
    public async Task UnicodeBasePathsPreserveActualRoutesAndIndexAliases(string baseUrl)
    {
        var page = SiteRoute.ForDirectoryIndex("概要", baseUrl);
        var resolver = new PublicMarkdownUrlResolver(baseUrl, [new("source.md", page)], []);
        const string canonical = "https://example.test/%E6%97%A5%E6%9C%AC%E8%AA%9E/%E6%A6%82%E8%A6%81/";
        await Assert.That(resolver.Resolve("index.html?q=%26&lang=日本語#見出し", "source.md", page))
            .IsEqualTo(canonical + "?q=%26&lang=日本語#見出し");
        await Assert.That(resolver.Resolve("/日本語/概要/index.html", "source.md", page)).IsEqualTo(canonical);
    }

    [Test]
    [Arguments("100%.txt", "100%25.txt")]
    [Arguments("literal%2F.txt", "literal%252F.txt")]
    [Arguments("part#name.txt", "part%23name.txt")]
    public async Task DecodedCatalogOutputNamesAreEncodedExactlyOnce(string sourceName, string escapedName)
    {
        var route = SiteRoute.ForFile("assets/" + escapedName, BaseUrl);
        var resolver = new PublicMarkdownUrlResolver(BaseUrl, [new("source.md", Page)], [new(sourceName, route)]);
        var expected = "https://example.test/product/assets/" + escapedName + "?q=%252F&x=1#part";
        await Assert.That(resolver.Resolve(escapedName + "?q=%252F&x=1#part", "source.md", Page)).IsEqualTo(expected);
        await Assert.That(resolver.Resolve("/product/assets/" + escapedName + "?q=%252F&x=1#part", "source.md", Page)).IsEqualTo(expected);
    }

    [Test]
    public async Task QuestionMarkFilenamesRemainUnavailableUnderExistingRoutePolicy()
    {
        // SiteRoute rejects '?' even when encoded because it is not a portable output filename.
        await Assert.That(() => SiteRoute.ForFile("assets/name%3Fpart.txt", BaseUrl)).Throws<ArgumentException>();
        await Assert.That(Create().Resolve("name%3Fpart.txt", "content/ja/start.md", Page)).IsNull();
    }

    [Test]
    [Arguments("https://[")]
    [Arguments("https://example.test:invalid/path")]
    [Arguments("https://example.test/a b")]
    [Arguments("https://example.test/%")]
    [Arguments("//[")]
    [Arguments("//example.test:invalid/path")]
    public async Task MalformedExternalTargetsReturnFailure(string target)
    {
        await Assert.That(Create().Resolve(target, "content/ja/start.md", Page)).IsNull();
    }
}
