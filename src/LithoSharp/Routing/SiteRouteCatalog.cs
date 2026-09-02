using LithoSharp.Content;
using LithoSharp.Pages;
using System.Security.Cryptography;
using System.Text;

namespace LithoSharp.Routing;

/// <summary>1 回の生成で使うページと共通成果物のルートを一元管理します。</summary>
internal sealed class SiteRouteCatalog
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _baseUrl;
    private readonly string _origin;
    private readonly Dictionary<string, SiteRoute> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<MarkdownPost, SiteRoute> _posts =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SiteExtraPage, SiteRoute> _extraPages =
        new(ReferenceEqualityComparer.Instance);

    public SiteRouteCatalog(
        string baseUrl,
        IEnumerable<SitePage<MarkdownPost>> posts,
        IEnumerable<SitePage<SiteExtraPage>> extraPages)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(posts);
        ArgumentNullException.ThrowIfNull(extraPages);

        _baseUrl = baseUrl;
        Root = SiteRoute.ForDirectoryIndex("", baseUrl);
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        _origin = baseUri.GetLeftPart(UriPartial.Authority);

        foreach (var page in posts)
        {
            _posts.TryAdd(page.Content, Add(page.Route));
        }

        foreach (var page in extraPages)
        {
            _extraPages.TryAdd(page.Content, Add(page.Route));
        }
    }

    public SiteRoute Root { get; }

    public SiteRoute Home => File("index.html");

    public SiteRoute Archives => File("archives.html");

    public SiteRoute Tags => File("tags.html");

    public SiteRoute SearchPage => File("search.html");

    public SiteRoute SearchIndex => File("search-index.json");

    public SiteRoute Feed => File("feed.xml");

    public SiteRoute Sitemap => File("sitemap.xml");

    public SiteRoute SiteCss => File("assets/site.css");

    public SiteRoute SiteScript => File("assets/site.js");

    public SiteRoute SearchScript => File("assets/search.js");

    public SiteRoute WebManifest => File("site.webmanifest");

    public SiteRoute Llms => File("llms.txt");

    public SiteRoute DefaultSocialImage => File("assets/social/og-default.png");

    public SiteRoute File(string relativePath)
    {
        var route = SiteRoute.ForFile(relativePath, _baseUrl);
        return Add(route);
    }

    public bool TryGetFile(string relativeOutputPath, out SiteRoute route) =>
        _files.TryGetValue(relativeOutputPath, out route!);

    public SiteRoute Post(MarkdownPost post) =>
        _posts.TryGetValue(post, out var route)
            ? route
            : throw new InvalidOperationException($"Post '{post.FilePath}' is not part of this generation.");

    public SiteRoute ExtraPage(SiteExtraPage page) =>
        _extraPages.TryGetValue(page, out var route)
            ? route
            : throw new InvalidOperationException($"Extra page '{page.RelativePath}' is not part of this generation.");

    public SiteRoute Favicon(string fileName) => File($"assets/favicon/{fileName}");

    public SiteRoute PostSocialImage(MarkdownPost post) =>
        File($"assets/social/posts/{RouteHash(Post(post))}.png");

    public SiteRoute ContentSocialImage(SiteRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return File($"assets/social/content/{RouteHash(route)}.png");
    }

    public string PublicPath(SiteRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return DecodeNonAsciiUtf8(route.PublicPath);
    }

    public string AbsoluteUrl(SiteRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return $"{_origin}{PublicPath(route)}";
    }

    public string AbsoluteRootUrl => $"{_origin}{PublicPath(Root)}";

    private SiteRoute Add(SiteRoute route)
    {
        if (_files.TryGetValue(route.RelativeOutputPath, out var existing))
        {
            return existing;
        }

        _files.Add(route.RelativeOutputPath, route);
        return route;
    }

    private static string RouteHash(SiteRoute route) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(route.RelativeOutputPath)));

    private static string DecodeNonAsciiUtf8(string path)
    {
        var result = new StringBuilder(path.Length);
        for (var index = 0; index < path.Length;)
        {
            if (path[index] != '%' || index + 2 >= path.Length)
            {
                result.Append(path[index++]);
                continue;
            }

            var firstByte = ParseByte(path, index);
            var byteCount = firstByte switch
            {
                >= 0xC2 and <= 0xDF => 2,
                >= 0xE0 and <= 0xEF => 3,
                >= 0xF0 and <= 0xF4 => 4,
                _ => 0,
            };
            if (byteCount == 0 || !TryReadEncodedBytes(path, index, byteCount, out var bytes))
            {
                result.Append(path, index, 3);
                index += 3;
                continue;
            }

            try
            {
                result.Append(StrictUtf8.GetString(bytes));
                index += byteCount * 3;
            }
            catch (DecoderFallbackException)
            {
                result.Append(path, index, 3);
                index += 3;
            }
        }

        return result.ToString();
    }

    private static bool TryReadEncodedBytes(
        string value,
        int start,
        int byteCount,
        out byte[] bytes)
    {
        bytes = new byte[byteCount];
        for (var offset = 0; offset < byteCount; offset++)
        {
            var index = start + (offset * 3);
            if (index + 2 >= value.Length || value[index] != '%')
            {
                return false;
            }

            var parsed = ParseByte(value, index);
            if (parsed < 0)
            {
                return false;
            }

            bytes[offset] = (byte)parsed;
        }

        return true;
    }

    private static int ParseByte(string value, int percentIndex)
    {
        var high = HexValue(value[percentIndex + 1]);
        var low = HexValue(value[percentIndex + 2]);
        return high < 0 || low < 0 ? -1 : (high << 4) | low;
    }

    private static int HexValue(char value) =>
        value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'A' and <= 'F' => value - 'A' + 10,
            >= 'a' and <= 'f' => value - 'a' + 10,
            _ => -1,
        };
}
