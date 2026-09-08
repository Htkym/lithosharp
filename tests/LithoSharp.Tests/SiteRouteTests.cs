using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class SiteRouteTests
{
    [Test]
    [Arguments("index.html", null, "/index.html", "index.html")]
    [Arguments(".html", null, "/.html", ".html")]
    [Arguments("posts\\guide.html", null, "/posts/guide.html", "posts/guide.html")]
    [Arguments("posts//guide.html", null, "/posts/guide.html", "posts/guide.html")]
    [Arguments("日本語/概要.html", null, "/%E6%97%A5%E6%9C%AC%E8%AA%9E/%E6%A6%82%E8%A6%81.html", "日本語/概要.html")]
    [Arguments("%E6%97%A5%E6%9C%AC%E8%AA%9E/%e6%a6%82%e8%a6%81.html", null, "/%E6%97%A5%E6%9C%AC%E8%AA%9E/%E6%A6%82%E8%A6%81.html", "日本語/概要.html")]
    [Arguments("hello%20world.html", null, "/hello%20world.html", "hello world.html")]
    [Arguments("100%25.html", null, "/100%25.html", "100%.html")]
    [Arguments("name%20with%20space.html", null, "/name%20with%20space.html", "name with space.html")]
    [Arguments("symbols%7Bbrace%7D%5Ecaret%60tick.html", null, "/symbols%7Bbrace%7D%5Ecaret%60tick.html", "symbols{brace}^caret`tick.html")]
    [Arguments("index.html", "https://example.test/product", "/product/index.html", "index.html")]
    [Arguments("index.html", "https://example.test/base%20space-%7Bbrace%7D-%7Cpipe-%5Ecaret-%60tick/", "/base%20space-%7Bbrace%7D-%7Cpipe-%5Ecaret-%60tick/index.html", "index.html")]
    [Arguments("posts/a.html", "https://example.test/product//docs/", "/product//docs/posts/a.html", "posts/a.html")]
    [Arguments("index.html", "https://example.test/CON/", "/CON/index.html", "index.html")]
    [Arguments("index.html", "https://example.test/docs:v2/", "/docs:v2/index.html", "index.html")]
    [Arguments("index.html", "https://example.test/app;v=1/", "/app;v=1/index.html", "index.html")]
    [Arguments("posts/a.html", "https://example.test/日本語/", "/%E6%97%A5%E6%9C%AC%E8%AA%9E/posts/a.html", "posts/a.html")]
    [Arguments("posts/a.html", "https://example.test/%E6%97%A5%E6%9C%AC%E8%AA%9E/", "/%E6%97%A5%E6%9C%AC%E8%AA%9E/posts/a.html", "posts/a.html")]
    [Arguments("index.html", "https://example.test/docs%3Av2/", "/docs%3Av2/index.html", "index.html")]
    public async Task ForFile_NormalizesValidRoutes(
        string relativePath,
        string? baseUrl,
        string expectedPublicPath,
        string expectedOutputPath)
    {
        var route = SiteRoute.ForFile(relativePath, baseUrl);

        await Assert.That(route.PublicPath).IsEqualTo(expectedPublicPath);
        await Assert.That(route.RelativeOutputPath).IsEqualTo(expectedOutputPath);
    }

    [Test]
    [Arguments("", null, "/", "index.html")]
    [Arguments("/", null, "/", "index.html")]
    [Arguments("\\", null, "/", "index.html")]
    [Arguments("guides", null, "/guides/", "guides/index.html")]
    [Arguments("guides/", null, "/guides/", "guides/index.html")]
    [Arguments("guides//start", null, "/guides/start/", "guides/start/index.html")]
    [Arguments("guides\\start\\", null, "/guides/start/", "guides/start/index.html")]
    [Arguments("日本語/概要", null, "/%E6%97%A5%E6%9C%AC%E8%AA%9E/%E6%A6%82%E8%A6%81/", "日本語/概要/index.html")]
    [Arguments("", "https://example.test/product/", "/product/", "index.html")]
    [Arguments("guide", "https://example.test/product/", "/product/guide/", "guide/index.html")]
    public async Task ForDirectoryIndex_NormalizesValidRoutes(
        string relativePath,
        string? baseUrl,
        string expectedPublicPath,
        string expectedOutputPath)
    {
        var route = SiteRoute.ForDirectoryIndex(relativePath, baseUrl);

        await Assert.That(route.PublicPath).IsEqualTo(expectedPublicPath);
        await Assert.That(route.RelativeOutputPath).IsEqualTo(expectedOutputPath);
    }

    [Test]
    [Arguments("", "empty")]
    [Arguments("/", "relative file")]
    [Arguments("posts/", "end with a separator")]
    [Arguments("/posts/a.html", "relative path")]
    [Arguments("\\\\server\\share\\a.html", "relative path")]
    [Arguments("C:\\site\\a.html", "relative path")]
    [Arguments("https://example.test/a.html", "relative path")]
    [Arguments("a/./b.html", "'.' or '..'")]
    [Arguments("a/../b.html", "'.' or '..'")]
    [Arguments("a/%2e%2e/b.html", "'.' or '..'")]
    [Arguments("a.html?download=1", "query string or fragment")]
    [Arguments("a.html#section", "query string or fragment")]
    [Arguments("a/%2F/b.html", "platform-unsafe character")]
    [Arguments("a/%5c/b.html", "platform-unsafe character")]
    [Arguments("CON", "platform-reserved")]
    [Arguments("con.txt", "platform-reserved")]
    [Arguments("Lpt9.log", "platform-reserved")]
    [Arguments("COM0", "platform-reserved")]
    [Arguments("com0.txt", "platform-reserved")]
    [Arguments("LPT0", "platform-reserved")]
    [Arguments("lPt0.log", "platform-reserved")]
    [Arguments("COM¹.txt", "platform-reserved")]
    [Arguments("name.", "space or period")]
    [Arguments("name%20", "space or period")]
    [Arguments("a%3Ab.html", "platform-unsafe character")]
    [Arguments("pipe%7Cname.html", "platform-unsafe character")]
    [Arguments("a%00b.html", "platform-unsafe character")]
    public async Task ForFile_RejectsUnsafeRoutes(string relativePath, string expectedMessage)
    {
        await Assert.That(() => SiteRoute.ForFile(relativePath))
            .Throws<ArgumentException>()
            .WithMessageContaining(expectedMessage);
    }

    [Test]
    [Arguments("/guide")]
    [Arguments("\\guide")]
    [Arguments("C:\\guide")]
    [Arguments("guide/../secret")]
    [Arguments("guide/.")]
    [Arguments("guide?mode=1")]
    [Arguments("guide#part")]
    [Arguments("AUX")]
    [Arguments("guide/name.")]
    public async Task ForDirectoryIndex_RejectsUnsafeRoutes(string relativePath)
    {
        await Assert.That(() => SiteRoute.ForDirectoryIndex(relativePath))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("%")]
    [Arguments("%2")]
    [Arguments("%GG")]
    [Arguments("a/%C3%28.html")]
    public async Task Factories_RejectInvalidPercentEncoding(string relativePath)
    {
        await Assert.That(() => SiteRoute.ForFile(relativePath))
            .Throws<UriFormatException>();
    }

    [Test]
    [Arguments("")]
    [Arguments("/product/")]
    [Arguments("product")]
    [Arguments("ftp://example.test/product/")]
    [Arguments("https://example.test/product/?view=all")]
    [Arguments("https://example.test/product/#top")]
    [Arguments("https://example.test/%ZZ/")]
    [Arguments("https://example.test/a/../b/")]
    [Arguments("https://example.test/%2e%2e/b/")]
    [Arguments("https://example.test/a/%2E/b/")]
    [Arguments("https://example.test/a/%2f/b/")]
    [Arguments("https://example.test/a/%5C/b/")]
    [Arguments("https://example.test//docs/")]
    [Arguments("https://example.test\\product\\")]
    [Arguments("https://example.test/a b/")]
    [Arguments("https://example.test/a/%C3%28/")]
    [Arguments("https://example.test/product?")]
    [Arguments("https://example.test/product#")]
    public async Task Factories_RejectInvalidBaseUrls(string baseUrl)
    {
        await Assert.That(() => SiteRoute.ForFile("index.html", baseUrl))
            .Throws<UriFormatException>();
    }

    [Test]
    public async Task Factories_RejectNullRelativePath()
    {
        await Assert.That(() => SiteRoute.ForFile(null!))
            .Throws<ArgumentNullException>();
        await Assert.That(() => SiteRoute.ForDirectoryIndex(null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Equality_UsesNormalizedOrdinalRouteIdentity()
    {
        var composed = SiteRoute.ForFile("日本語/caf\u00E9.html", "https://example.test/product/");
        var decomposed = SiteRoute.ForFile("%E6%97%A5%E6%9C%AC%E8%AA%9E/cafe\u0301.html", "https://example.test/product");
        var differentCase = SiteRoute.ForFile("日本語/Caf\u00E9.html", "https://example.test/product/");

        await Assert.That(composed.Equals(decomposed)).IsTrue();
        await Assert.That(composed.Equals((object)decomposed)).IsTrue();
        await Assert.That(composed.GetHashCode()).IsEqualTo(decomposed.GetHashCode());
        await Assert.That(composed.Equals(differentCase)).IsFalse();
    }

    [Test]
    public async Task Equality_DistinguishesFileAndDirectoryPublicRoutes()
    {
        var file = SiteRoute.ForFile("index.html");
        var directory = SiteRoute.ForDirectoryIndex("");

        await Assert.That(file.RelativeOutputPath).IsEqualTo(directory.RelativeOutputPath);
        await Assert.That(file.Equals(directory)).IsFalse();
    }
}
