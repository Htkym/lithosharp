using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Mdx;

/// <summary>A named author shared by blog posts and generated author listings.</summary>
public sealed record BlogAuthor(string Name, string? Bio = null, SiteUrl? Url = null, SiteUrl? Image = null);

/// <summary>Strict metadata for an MDX blog entry. Summary is an explicit, plain-text excerpt.</summary>
public sealed class MdxBlogFrontMatter
{
    /// <summary>The article title.</summary>
    public string Title { get; set; } = "";
    /// <summary>The public path within the blog.</summary>
    public string? Slug { get; set; }
    /// <summary>The publication date.</summary>
    public DateTimeOffset Date { get; set; }
    /// <summary>A plain-text excerpt, without truncating a React tree.</summary>
    public string Summary { get; set; } = "";
    /// <summary>Author IDs defined in the blog configuration.</summary>
    public List<string> Authors { get; set; } = [];
    /// <summary>Article tags.</summary>
    public List<string> Tags { get; set; } = [];
    /// <summary>Excludes the entry before compilation.</summary>
    public bool Draft { get; set; }
    /// <summary>Publishes the entry without including it in listings and feeds.</summary>
    public bool Unlisted { get; set; }
    /// <summary>The exclusive publication end.</summary>
    public DateTimeOffset? PublishUntil { get; set; }
    /// <summary>An explicit feed text alternative; otherwise the excerpt is used.</summary>
    public string? FeedText { get; set; }
}

/// <summary>One blog's input, routes, authors and pagination.</summary>
public sealed record MdxBlogCollection(string Id, string InputDirectory, string RoutePrefix)
{
    /// <summary>Named author profiles.</summary>
    public IReadOnlyDictionary<string, BlogAuthor> Authors { get; init; } = new Dictionary<string, BlogAuthor>();
    /// <summary>The maximum number of articles per main listing page.</summary>
    public int PageSize { get; init; } = 10;
}

/// <summary>Multiple MDX blogs sharing one compiler graph and the existing publication pipeline.</summary>
public sealed class MdxBlogSite : ISiteBuildExtension, IAsyncDisposable
{
    private readonly MdxSite mdx;
    private readonly List<MdxBlogCollection> blogs = [];
    /// <summary>Creates the optional blog preset without starting Node.</summary>
    public MdxBlogSite(MdxOptions options) => mdx = new(options);
    internal MdxBlogSite(MdxSite shared) => mdx = shared;
    /// <summary>Registers a blog with strict publication and author validation.</summary>
    public void AddCollection(MdxBlogCollection blog)
    {
        ArgumentNullException.ThrowIfNull(blog);
        ArgumentOutOfRangeException.ThrowIfLessThan(blog.PageSize, 1);
        if (blogs.Any(value => value.Id == blog.Id)) throw new ArgumentException("Duplicate blog ID.");
        _ = SiteRoute.ForDirectoryIndex(blog.RoutePrefix);
        blogs.Add(blog);
        mdx.AddCollection(new BlogLoader(blog), renderer: (entry, context) =>
        {
            var metadata = entry.FrontMatter;
            var text = entry.Body.PlainText ?? metadata.Summary;
            var units = Regex.Matches(text, @"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]|[\p{L}\p{N}]+").Count;
            var body = "<h1>" + Html.Encode(metadata.Title) + "</h1><p><time datetime=\"" + metadata.Date.ToString("O") + "\">" + metadata.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "</time> · "
                + Math.Max(1, (int)Math.Ceiling(units / 250d)) + " min read</p>"
                + string.Concat(metadata.Authors.Select(id => Profile(blog, id, context.Site.BaseUrl))) + entry.Body.ToHtmlString();
            return context.RenderDocument(body);
        });
    }
    /// <inheritdoc />
    public async Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
    {
        var prepared = await mdx.PrepareAsync(context, cancellationToken).ConfigureAwait(false);
        return Contribute(prepared, context);
    }
    internal SiteBuildContribution Contribute(SiteBuildContribution prepared, SiteBuildContext context)
    {
        var collections = prepared.ContentCollections.ToList();
        var assets = prepared.Assets.ToList();
        foreach (var blog in blogs)
        {
            var collection = prepared.ContentCollections.OfType<SiteContentCollection<MdxBlogFrontMatter, MdxDocument>>().Single(value => value.Collection.Id.Value == "blog:" + blog.Id).Collection;
            var listed = collection.Entries.Where(entry => !entry.FrontMatter.Unlisted).OrderByDescending(entry => entry.FrontMatter.Date).ThenBy(entry => entry.Id.Value, StringComparer.Ordinal).ToArray();
            var positions = listed.Select((entry, index) => (entry.Id, Page: index / blog.PageSize + 1)).ToDictionary(pair => pair.Id, pair => pair.Page);
            collections.Add(collection.GeneratePages(new("blog-list:" + blog.Id), entry => entry.FrontMatter.Unlisted ? [] :
                new[] { "page/" + positions[entry.Id], "archive/" + entry.FrontMatter.Date.ToString("yyyy/MM", CultureInfo.InvariantCulture) }
                    .Concat(entry.FrontMatter.Tags.Select(tag => "tags/" + tag)).Concat(entry.FrontMatter.Authors.Select(author => "authors/" + author)),
                group => new SitePage<IReadOnlyList<ContentEntry<MdxBlogFrontMatter, MdxDocument>>>(new(group.Key),
                    SiteRoute.ForDirectoryIndex(blog.RoutePrefix.Trim('/') + (group.Key == "page/1" ? "" : "/" + string.Join('/', group.Key.Split('/').Select(Uri.EscapeDataString))), context.Site.BaseUrl),
                    group.Entries, new(group.Key.StartsWith("authors/", StringComparison.Ordinal) ? blog.Authors[group.Key[8..]].Name : blog.Id)),
                (page, rendering) => rendering.RenderDocument("<h1>" + Html.Encode(page.Metadata.Title!) + "</h1>" +
                    (page.Id.Value.StartsWith("authors/", StringComparison.Ordinal) ? Profile(blog, page.Id.Value[8..], context.Site.BaseUrl) : "") +
                    string.Concat(page.Content.OrderByDescending(entry => entry.FrontMatter.Date).Select(entry => "<article><h2><a href=\"" + SiteUrl.FromRoute(collection.RouteConvention(entry).WithBaseUrl(context.Site.BaseUrl)).ToAttributeValue()
                        + "\">" + Html.Encode(entry.FrontMatter.Title) + "</a></h2><p>" + Html.Encode(entry.FrontMatter.Summary) + "</p><a href=\""
                        + SiteUrl.FromRoute(collection.RouteConvention(entry).WithBaseUrl(context.Site.BaseUrl)).ToAttributeValue() + "\">Read more</a></article>")) +
                    "<nav aria-label=\"Blog pages\">" + string.Concat(Enumerable.Range(1, (listed.Length + blog.PageSize - 1) / blog.PageSize).Select(number =>
                        "<a href=\"" + SiteUrl.ForDirectory(blog.RoutePrefix.Trim('/') + (number == 1 ? "" : "/page/" + number), context.Site.BaseUrl).ToAttributeValue() + "\">" + number + "</a> ")) + "</nav>"),
                transformationId: new("mdx-blog-v1"), isCacheable: true));
            string Absolute(ContentEntry<MdxBlogFrontMatter, MdxDocument> entry) => new Uri(new Uri(context.Site.BaseUrl), collection.RouteConvention(entry).WithBaseUrl(context.Site.BaseUrl).PublicPath).AbsoluteUri;
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var updated = listed.Length == 0 ? context.BuildTimestamp : listed.Max(entry => entry.FrontMatter.Date);
            var atomFeed = new XElement(atom + "feed", new XElement(atom + "title", blog.Id), new XElement(atom + "id", new Uri(new Uri(context.Site.BaseUrl), SiteUrl.ForDirectory(blog.RoutePrefix, context.Site.BaseUrl).Value)),
                new XElement(atom + "updated", updated.ToString("O")), listed.Select(entry => new XElement(atom + "entry",
                    new XElement(atom + "id", Absolute(entry)), new XElement(atom + "title", entry.FrontMatter.Title), new XElement(atom + "updated", entry.FrontMatter.Date.ToString("O")),
                    new XElement(atom + "link", new XAttribute("href", Absolute(entry))), entry.FrontMatter.Authors.Select(id => new XElement(atom + "author", new XElement(atom + "name", blog.Authors[id].Name))),
                    new XElement(atom + "content", new XAttribute("type", "text"), entry.FrontMatter.FeedText ?? entry.FrontMatter.Summary))));
            var rss = new XElement("rss", new XAttribute("version", "2.0"), new XElement("channel", new XElement("title", blog.Id), new XElement("description", context.Site.Description),
                new XElement("link", context.Site.BaseUrl), listed.Select(entry => new XElement("item", new XElement("title", entry.FrontMatter.Title), new XElement("guid", Absolute(entry)),
                    new XElement("link", Absolute(entry)), new XElement("pubDate", entry.FrontMatter.Date.ToString("R", CultureInfo.InvariantCulture)), new XElement("description", entry.FrontMatter.FeedText ?? entry.FrontMatter.Summary)))));
            var json = JsonSerializer.Serialize(new { version = "https://jsonfeed.org/version/1.1", title = blog.Id, home_page_url = context.Site.BaseUrl,
                items = listed.Select(entry => new { id = Absolute(entry), url = Absolute(entry), title = entry.FrontMatter.Title, content_text = entry.FrontMatter.FeedText ?? entry.FrontMatter.Summary,
                    date_published = entry.FrontMatter.Date, tags = entry.FrontMatter.Tags, authors = entry.FrontMatter.Authors.Select(id => new { name = blog.Authors[id].Name, url = blog.Authors[id].Url?.Value }) }) });
            foreach (var (file, content) in new[] { ("atom.xml", atomFeed.ToString()), ("rss.xml", rss.ToString()), ("feed.json", json) })
                assets.Add(new("blog:" + blog.Id + ":" + file, blog.RoutePrefix.Trim('/') + "/" + file, Encoding.UTF8.GetBytes(content)));
        }
        return new() { ContentCollections = collections, Assets = assets };
    }
    private static string Profile(MdxBlogCollection blog, string id, string baseUrl)
    {
        var author = blog.Authors[id];
        return "<aside class=\"blog-author\">" + (author.Image is null ? "" : "<img alt=\"\" width=\"48\" height=\"48\" src=\"" + author.Image.ToAttributeValue() + "\">")
            + "<a href=\"" + SiteUrl.ForDirectory(blog.RoutePrefix.Trim('/') + "/authors/" + Uri.EscapeDataString(id), baseUrl).ToAttributeValue() + "\">" + Html.Encode(author.Name) + "</a><p>"
            + Html.Encode(author.Bio ?? "") + "</p>" + (author.Url is null ? "" : "<a href=\"" + author.Url.ToAttributeValue() + "\">Profile</a>") + "</aside>";
    }
    /// <inheritdoc />
    public JsonElement? GetInspection() => mdx.GetInspection();
    /// <inheritdoc />
    public ValueTask DisposeAsync() => mdx.DisposeAsync();
    private sealed class BlogLoader(MdxBlogCollection blog) : IContentCollectionLoader<MdxBlogFrontMatter, MdxDocument>
    {
        public async ValueTask<ContentLoadResult<MdxBlogFrontMatter, MdxDocument>> LoadAsync(CancellationToken cancellationToken = default)
        {
            var result = await new MdxContentCollectionLoader<MdxBlogFrontMatter>(new("blog:" + blog.Id), blog.InputDirectory,
                entry => SiteRoute.ForDirectoryIndex(blog.RoutePrefix.Trim('/') + "/" + (entry.FrontMatter.Slug ?? Path.ChangeExtension(entry.Id.Value, null)).Trim('/')),
                entry => new(entry.FrontMatter.Title, entry.FrontMatter.Summary, entry.FrontMatter.Draft, entry.FrontMatter.Date, entry.FrontMatter.PublishUntil))
                { IncludeMarkdown = true, TransformationFingerprint = "mdx-blog-v1" }.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess) return result;
            var source = result.Collection!;
            foreach (var entry in source.Entries)
                if (entry.FrontMatter.Authors.Any(id => !blog.Authors.ContainsKey(id)) || entry.FrontMatter.Date == default)
                    throw new ArgumentException("Blog entries require a date and known author IDs: " + entry.SourcePath);
            return ContentLoadResult<MdxBlogFrontMatter, MdxDocument>.Success(new(source.Id, source.InputRoot, source.Entries.Select(entry =>
                new ContentEntry<MdxBlogFrontMatter, MdxDocument>(entry.Id, entry.SourcePath, entry.SourceFingerprint, entry.FrontMatter, entry.Body, entry.SourceLocation)
                { DerivedSurfaces = entry.FrontMatter.Unlisted ? GeneratedPageDerivedSurfaces.None : GeneratedPageDerivedSurfaces.All }), source.RouteConvention, source.PublicationMapper,
                declaredDependencies: [ContentDependency.FromValue("blog.options", JsonSerializer.Serialize(blog))], transformationId: new("mdx-blog-v1"), isCacheable: true));
        }
    }
}
