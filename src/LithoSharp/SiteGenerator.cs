using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Xml;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Search;
using LithoSharp.Validation;
using Markdig;

namespace LithoSharp;

/// <summary>
/// A general-purpose generator that builds a static site from Markdown posts and
/// <see cref="SiteSettings"/>. Text, theme, content validation, and extra pages are
/// swapped in through <see cref="SiteCustomization"/>.
/// </summary>
public sealed class SiteGenerator
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();
    private const string FaviconOutputDirectory = "assets/favicon";
    private const string SocialImageOutputDirectory = "assets/social";
    private const string DefaultSocialImageFileName = "og-default.png";
    private const string SiteIconFileName = "android-chrome-192x192.png";
    private static readonly (string FileName, string Sizes)[] PngFaviconAssets =
    [
        ("favicon-16x16.png", "16x16"),
        ("favicon-32x32.png", "32x32"),
        ("favicon-48x48.png", "48x48"),
        ("favicon-64x64.png", "64x64"),
        ("favicon-96x96.png", "96x96"),
        ("favicon-128x128.png", "128x128"),
        ("favicon-180x180.png", "180x180"),
        ("favicon-192x192.png", "192x192"),
        ("favicon-256x256.png", "256x256"),
        ("favicon-512x512.png", "512x512")
    ];
    private static readonly (string FileName, string Sizes)[] ManifestIconAssets =
    [
        ("android-chrome-192x192.png", "192x192"),
        ("android-chrome-512x512.png", "512x512")
    ];
    private static readonly string[] BundledFaviconAssets =
    [
        "favicon.ico",
        "apple-touch-icon.png",
        "android-chrome-192x192.png",
        "android-chrome-512x512.png",
        .. PngFaviconAssets.Select(static asset => asset.FileName)
    ];
    private static readonly JsonSerializerOptions SearchSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private static readonly JsonSerializerOptions WebManifestSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private static readonly JsonSerializerOptions SearchMessageSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly SearchJsonSerializerContext SearchSerializerContext = new(SearchSerializerOptions);
    private static readonly Regex BodyHeadingOneOpenRegex = new("<h1(?<attributes>\\s[^>]*)?>", RegexOptions.Singleline);
    private static readonly Regex BodyHeadingOneCloseRegex = new("</h1>", RegexOptions.Singleline);
    private static readonly Regex HeadingRegex = new("<h(?<level>[2-3]) id=\"(?<id>[^\"]+)\">(?<text>.*?)</h\\k<level>>", RegexOptions.Singleline);
    private static readonly Regex AnchorTagRegex = new("<a(?<before>[^>]*?)\\shref=\"(?<href>[^\"]+)\"(?<after>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private const int SearchIndexMaxLength = 12000;
    private static readonly Regex StripTagsRegex = new("<.*?>", RegexOptions.Singleline);

    internal sealed record RenderContext(
        SiteSettings Site,
        SiteText Text,
        SiteThemeOptions Theme,
        IReadOnlyList<SiteExtraPage> ExtraPages,
        string FaviconSourceDirectory,
        bool HasFaviconAssets,
        bool HasSocialImage);


    /// <summary>Generates a static site from the site settings and posts.</summary>
    /// <param name="site">Site settings.</param>
    /// <param name="posts">Posts to render.</param>
    /// <param name="outputDirectory">Output directory.</param>
    /// <param name="clean">Whether to delete the output directory before generating.</param>
    /// <param name="customization">Text, theme, extra pages, and other overrides. Defaults to English when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generation result.</returns>
    public async Task<SiteGenerationResult> GenerateAsync(
        SiteSettings site,
        IReadOnlyList<MarkdownPost> posts,
        string outputDirectory,
        bool clean,
        SiteCustomization? customization = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(posts);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        customization ??= new SiteCustomization();
        var faviconSource = customization.FaviconSourceDirectory ?? Path.Combine(AppContext.BaseDirectory, "favicon");
        var hasFaviconAssets = BundledFaviconAssets.All(asset => File.Exists(Path.Combine(faviconSource, asset)));
        var hasSocialImage = File.Exists(Path.Combine(faviconSource, SiteIconFileName));
        var configuration = new RenderContext(site, customization.Text, customization.Theme, customization.ExtraPages, faviconSource, hasFaviconAssets, hasSocialImage);

        var outputRoot = Path.GetFullPath(outputDirectory);
        if (clean && Directory.Exists(outputRoot))
        {
            Directory.Delete(outputRoot, recursive: true);
        }

        Directory.CreateDirectory(outputRoot);
        var generated = new List<string>();
        var template = customization.Template
            ?? throw new InvalidOperationException("Site customization must specify a template.");
        var docsNavigation = BuildDocsNavigation(posts);
        var templatePages = BuildTemplatePages(configuration, docsNavigation);
        var templateNavigation = BuildTemplateNavigation(configuration, docsNavigation, templatePages);
        var templateContext = new SiteTemplateContext(
            this,
            site,
            posts,
            customization.Text,
            customization.Theme,
            customization.ExtraPages,
            templatePages,
            templateNavigation,
            configuration);
        var templateResult = await template.RenderAsync(templateContext, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Template '{template.GetType().FullName}' returned no result.");
        ValidateTemplateFiles(
            outputRoot,
            templateResult.Files,
            GetCommonArtifactPaths(configuration, posts, customization.GenerateLlmsTxt));

        foreach (var file in templateResult.Files)
        {
            await WriteTextAsync(outputRoot, file.RelativePath, file.Content, generated, cancellationToken).ConfigureAwait(false);
        }

        if (configuration.HasFaviconAssets)
        {
            await WriteBundledFaviconAssetsAsync(outputRoot, configuration, generated, cancellationToken).ConfigureAwait(false);
            await WriteTextAsync(outputRoot, "site.webmanifest", BuildWebManifest(configuration), generated, cancellationToken).ConfigureAwait(false);
        }

        if (configuration.HasSocialImage)
        {
            await WriteBinaryAssetAsync(
                    outputRoot,
                    SocialImageAssetPath(DefaultSocialImageFileName),
                    await BuildDefaultSocialImageAsync(configuration, cancellationToken).ConfigureAwait(false),
                    generated,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (configuration.HasSocialImage)
        {
            foreach (var post in posts)
            {
                await WriteBinaryAssetAsync(
                        outputRoot,
                        PostSocialImagePath(post),
                        await BuildPostSocialImageAsync(configuration, post, cancellationToken).ConfigureAwait(false),
                        generated,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (customization.GenerateLlmsTxt)
        {
            await WriteTextAsync(outputRoot, "llms.txt", BuildLlmsTxt(configuration, posts), generated, cancellationToken).ConfigureAwait(false);
        }

        return new SiteGenerationResult(outputRoot, posts.Count, generated);
    }

    /// <summary>Validates posts by applying the validators in <paramref name="customization"/> in order.</summary>
    /// <param name="site">Site settings.</param>
    /// <param name="contentDirectory">Content directory.</param>
    /// <param name="posts">Posts to validate.</param>
    /// <param name="customization">Overrides that include the validators. When omitted or empty, only the required-summary check runs.</param>
    public static void Validate(
        SiteSettings site,
        string contentDirectory,
        IReadOnlyList<MarkdownPost> posts,
        SiteCustomization? customization = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(posts);
        customization ??= new SiteCustomization();
        IReadOnlyList<IContentValidator> validators = customization.Validators.Count > 0
            ? customization.Validators
            : [new RequiredSummaryValidator()];
        var context = new ContentValidationContext(site, contentDirectory);
        foreach (var post in posts)
        {
            foreach (var validator in validators)
            {
                validator.Validate(post, context);
            }
        }
    }

    internal string RenderMarkdown(string markdown) => Markdown.ToHtml(markdown, _pipeline);

    internal string GetSitePath(RenderContext configuration, string relativePath) => SitePath(configuration, relativePath);

    internal string RenderTemplateDocument(RenderContext configuration, SiteTemplateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Layout(
            configuration,
            document.Title,
            document.BodyHtml,
            document.RelativePath,
            document.Description,
            document.OpenGraphType,
            document.PublishedAt,
            document.SocialImageRelativePath);
    }

    internal static string RenderTemplateTableOfContents(
        RenderContext configuration,
        IReadOnlyList<SiteTemplateHeading> headings)
    {
        ArgumentNullException.ThrowIfNull(headings);
        var body = new StringBuilder();
        body.AppendLine("<aside class=\"post-toc\" aria-labelledby=\"post-toc-title\">");
        body.AppendLine($"<h2 id=\"post-toc-title\">{Html.Encode(configuration.Text.TableOfContentsHeading)}</h2>");
        if (headings.Count == 0)
        {
            body.AppendLine($"<p>{Html.Encode(configuration.Text.TableOfContentsEmpty)}</p>");
        }
        else
        {
            body.AppendLine($"<nav class=\"toc-nav\" aria-label=\"{Html.Encode(configuration.Text.TableOfContentsHeading)}\">");
            body.AppendLine("<div class=\"toc-track\" aria-hidden=\"true\"></div>");
            body.AppendLine("<div class=\"toc-indicator\" aria-hidden=\"true\"></div>");
            body.AppendLine("<ol class=\"toc-list\">");
            foreach (var heading in headings)
            {
                var depth = Math.Clamp(heading.Level - 1, 1, 3);
                body.AppendLine($"<li class=\"toc-depth-{depth}\" data-toc-item><a data-toc-link href=\"#{Html.Encode(heading.Id)}\">{Html.Encode(heading.Text)}</a></li>");
            }

            body.AppendLine("</ol>");
            body.AppendLine("</nav>");
        }

        body.AppendLine("</aside>");
        return body.ToString();
    }

    internal SiteTemplateResult RenderBlogTemplate(SiteTemplateContext templateContext)
    {
        var configuration = templateContext.Configuration;
        var posts = templateContext.Posts;
        var files = new List<SiteTemplateFile>
        {
            new() { RelativePath = "assets/site.css", Content = BuildCss(configuration) },
            new() { RelativePath = "assets/site.js", Content = BuildSiteScript() },
            new() { RelativePath = "assets/search.js", Content = BuildSearchScript(configuration.Text) },
            new() { RelativePath = "search-index.json", Content = BuildSearchIndex(configuration, posts) },
            new() { RelativePath = "index.html", Content = RenderIndex(configuration, posts) },
            new() { RelativePath = "archives.html", Content = RenderArchives(configuration, posts) },
            new() { RelativePath = "tags.html", Content = RenderTags(configuration, posts) }
        };

        foreach (var extraPage in configuration.ExtraPages)
        {
            files.Add(new SiteTemplateFile
            {
                RelativePath = extraPage.RelativePath,
                Content = RenderExtraPage(configuration, extraPage)
            });
        }

        files.Add(new SiteTemplateFile
        {
            RelativePath = "search.html",
            Content = RenderSearch(configuration, DateTimeOffset.UtcNow)
        });

        foreach (var post in posts)
        {
            files.Add(new SiteTemplateFile
            {
                RelativePath = post.RelativeOutputPath,
                Content = RenderPost(configuration, post)
            });
        }

        files.Add(new SiteTemplateFile
        {
            RelativePath = "feed.xml",
            Content = RenderFeed(configuration, posts)
        });
        files.Add(new SiteTemplateFile
        {
            RelativePath = "sitemap.xml",
            Content = RenderSitemap(configuration, posts)
        });

        return new SiteTemplateResult(files);
    }

    internal SiteTemplateResult RenderDocsTemplate(SiteTemplateContext templateContext)
    {
        var configuration = templateContext.Configuration;
        var root = BuildDocsNavigation(templateContext.Posts);
        var orderedPosts = FlattenDocsNavigation(root).ToArray();
        var files = new List<SiteTemplateFile>
        {
            new() { RelativePath = "assets/site.css", Content = BuildDocsCss(configuration) },
            new() { RelativePath = "assets/site.js", Content = BuildDocsScript() },
            new()
            {
                RelativePath = "index.html",
                Content = RenderDocsIndex(configuration, root, orderedPosts)
            }
        };

        foreach (var post in orderedPosts)
        {
            files.Add(new SiteTemplateFile
            {
                RelativePath = post.RelativeOutputPath,
                Content = RenderDocsPost(configuration, root, orderedPosts, post)
            });
        }

        foreach (var extraPage in configuration.ExtraPages)
        {
            files.Add(new SiteTemplateFile
            {
                RelativePath = extraPage.RelativePath,
                Content = RenderDocsExtraPage(configuration, root, extraPage)
            });
        }

        return new SiteTemplateResult(files);
    }

    private static IEnumerable<string> GetCommonArtifactPaths(
        RenderContext configuration,
        IReadOnlyList<MarkdownPost> posts,
        bool generateLlmsTxt)
    {
        var paths = new List<string>();
        if (configuration.HasFaviconAssets)
        {
            paths.AddRange(BundledFaviconAssets.Select(FaviconAssetPath));
            paths.Add("site.webmanifest");
        }

        if (configuration.HasSocialImage)
        {
            paths.Add(SocialImageAssetPath(DefaultSocialImageFileName));
            paths.AddRange(posts.Select(PostSocialImagePath));
        }

        if (generateLlmsTxt)
        {
            paths.Add("llms.txt");
        }

        return paths;
    }

    private static void ValidateTemplateFiles(
        string outputRoot,
        IReadOnlyList<SiteTemplateFile> files,
        IEnumerable<string> commonArtifactPaths)
    {
        ArgumentNullException.ThrowIfNull(files);
        var commonPaths = new HashSet<string>(
            commonArtifactPaths.Select(path => NormalizeOutputPath(outputRoot, path)),
            StringComparer.OrdinalIgnoreCase);
        var templatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (file is null)
            {
                throw new InvalidOperationException("A template returned a null file.");
            }

            var path = NormalizeOutputPath(outputRoot, file.RelativePath);
            if (!templatePaths.Add(path))
            {
                throw new InvalidOperationException($"Template output path '{file.RelativePath}' is duplicated.");
            }

            if (commonPaths.Contains(path))
            {
                throw new InvalidOperationException(
                    $"Template output path '{file.RelativePath}' conflicts with a common artifact.");
            }
        }
    }

    private static string NormalizeOutputPath(string outputRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("A template output path must not be empty.");
        }

        var fullPath = SafeCombine(outputRoot, relativePath);
        var normalized = Path.GetRelativePath(outputRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        if (normalized is "." or "")
        {
            throw new InvalidOperationException($"Output path '{relativePath}' must name a file.");
        }

        return normalized;
    }

    private sealed class DocsNavigationNode(string segment, string path)
    {
        public string Segment { get; } = segment;

        public string Path { get; } = path;

        public MarkdownPost? Post { get; set; }

        public Dictionary<string, DocsNavigationNode> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private static DocsNavigationNode BuildDocsNavigation(IReadOnlyList<MarkdownPost> posts)
    {
        var root = new DocsNavigationNode(string.Empty, string.Empty);
        foreach (var post in posts)
        {
            var relativePath = post.RelativeOutputPath.Replace('\\', '/');
            var contentPath = relativePath.StartsWith("posts/", StringComparison.OrdinalIgnoreCase)
                ? relativePath["posts/".Length..]
                : relativePath;
            var withoutExtension = Path.ChangeExtension(contentPath, null)?.Replace('\\', '/') ?? contentPath;
            var segments = withoutExtension.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var node = root;
            var path = new StringBuilder();
            foreach (var segment in segments)
            {
                if (path.Length > 0)
                {
                    path.Append('/');
                }

                path.Append(segment);
                if (!node.Children.TryGetValue(segment, out var child))
                {
                    child = new DocsNavigationNode(segment, path.ToString());
                    node.Children.Add(segment, child);
                }

                node = child;
            }

            if (node.Post is not null)
            {
                throw new InvalidOperationException(
                    $"Posts '{node.Post.FilePath}' and '{post.FilePath}' have the same documentation path.");
            }

            node.Post = post;
        }

        return root;
    }

    private IReadOnlyList<SiteTemplatePage> BuildTemplatePages(
        RenderContext configuration,
        DocsNavigationNode root)
    {
        var orderedPosts = FlattenDocsNavigation(root).ToArray();
        var pages = orderedPosts
            .Select(post =>
            {
                var contentHtml = AddNewTabAttributesToExternalPostLinks(
                    configuration,
                    NormalizePostBodyHeadings(RenderMarkdown(post.MarkdownBody)));
                return new SiteTemplatePage
                {
                    Post = post,
                    Url = SitePath(configuration, post.RelativeOutputPath),
                    ContentHtml = contentHtml,
                    Headings = ExtractTemplateHeadings(contentHtml)
                };
            })
            .ToArray();

        return pages
            .Select((page, index) => page with
            {
                Previous = index > 0
                    ? new SiteTemplatePageLink(GetDocsLabel(pages[index - 1].Post), pages[index - 1].Url)
                    : null,
                Next = index + 1 < pages.Length
                    ? new SiteTemplatePageLink(GetDocsLabel(pages[index + 1].Post), pages[index + 1].Url)
                    : null
            })
            .ToArray();
    }

    private static SiteTemplateNavigationNode BuildTemplateNavigation(
        RenderContext configuration,
        DocsNavigationNode root,
        IReadOnlyList<SiteTemplatePage> pages)
    {
        var pagesByPost = pages.ToDictionary(page => page.Post);
        return BuildTemplateNavigationNode(root, pagesByPost, configuration.Site.Title);
    }

    private static SiteTemplateNavigationNode BuildTemplateNavigationNode(
        DocsNavigationNode node,
        IReadOnlyDictionary<MarkdownPost, SiteTemplatePage> pagesByPost,
        string rootLabel)
    {
        return new SiteTemplateNavigationNode
        {
            Label = string.IsNullOrEmpty(node.Segment)
                ? rootLabel
                : GetDocsNavigationLabel(node),
            Page = node.Post is not null ? pagesByPost[node.Post] : null,
            Children = OrderDocsNavigationChildren(node)
                .Select(child => BuildTemplateNavigationNode(child, pagesByPost, rootLabel))
                .ToArray()
        };
    }

    private static IEnumerable<MarkdownPost> FlattenDocsNavigation(DocsNavigationNode node)
    {
        foreach (var child in OrderDocsNavigationChildren(node))
        {
            if (child.Post is not null)
            {
                yield return child.Post;
            }

            foreach (var post in FlattenDocsNavigation(child))
            {
                yield return post;
            }
        }
    }

    private static IEnumerable<DocsNavigationNode> OrderDocsNavigationChildren(DocsNavigationNode node) =>
        node.Children.Values
            .OrderBy(GetDocsNavigationPosition)
            .ThenBy(GetDocsNavigationLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(child => child.Path, StringComparer.Ordinal);

    private static int GetDocsNavigationPosition(DocsNavigationNode node)
    {
        var ownPosition = node.Post?.FrontMatter.SidebarPosition;
        var childPosition = node.Children.Values
            .Select(GetDocsNavigationPosition)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        return ownPosition ?? childPosition;
    }

    private static string GetDocsNavigationLabel(DocsNavigationNode node)
    {
        if (node.Post is not null)
        {
            return GetDocsLabel(node.Post);
        }

        return FormatDocsFolderLabel(node.Segment);
    }

    private static string GetDocsLabel(MarkdownPost post) =>
        string.IsNullOrWhiteSpace(post.FrontMatter.SidebarLabel)
            ? post.FrontMatter.Title
            : post.FrontMatter.SidebarLabel.Trim();

    private static string FormatDocsFolderLabel(string value) =>
        value.Replace('-', ' ').Replace('_', ' ');

    private static string RenderDocsIndex(
        RenderContext configuration,
        DocsNavigationNode root,
        IReadOnlyList<MarkdownPost> orderedPosts)
    {
        var body = new StringBuilder();
        body.AppendLine("<section class=\"docs-hero\">");
        body.AppendLine($"<p class=\"docs-kicker\">Documentation</p>");
        body.AppendLine($"<h1>{Html.Encode(configuration.Site.Title)}</h1>");
        body.AppendLine($"<p>{Html.Encode(configuration.Site.Description)}</p>");
        if (orderedPosts.Count > 0)
        {
            var first = orderedPosts[0];
            body.AppendLine($"<p><a class=\"docs-primary-link\" href=\"{Html.Encode(SitePath(configuration, first.RelativeOutputPath))}\">Start reading</a></p>");
        }

        body.AppendLine("</section>");
        return DocsLayout(configuration, root, configuration.Site.Title, body.ToString(), "index.html", null, null);
    }

    private string RenderDocsPost(
        RenderContext configuration,
        DocsNavigationNode root,
        IReadOnlyList<MarkdownPost> orderedPosts,
        MarkdownPost post)
    {
        var postBody = AddNewTabAttributesToExternalPostLinks(
            configuration,
            NormalizePostBodyHeadings(RenderMarkdown(post.MarkdownBody)));
        var currentIndex = Array.IndexOf(orderedPosts.ToArray(), post);
        var body = new StringBuilder();
        body.AppendLine($"<h1>{Html.Encode(post.FrontMatter.Title)}</h1>");
        if (!string.IsNullOrWhiteSpace(post.FrontMatter.Summary))
        {
            body.AppendLine($"<p class=\"docs-description\">{Html.Encode(post.FrontMatter.Summary)}</p>");
        }

        body.AppendLine(postBody);
        body.AppendLine(RenderDocsPagination(configuration, orderedPosts, currentIndex));
        return DocsLayout(
            configuration,
            root,
            post.FrontMatter.Title,
            body.ToString(),
            post.RelativeOutputPath,
            post.RelativeOutputPath,
            RenderDocsTableOfContents(configuration, postBody),
            post.FrontMatter.Summary,
            "article",
            post.FrontMatter.Date,
            PostSocialImagePath(post));
    }

    private static string RenderDocsExtraPage(
        RenderContext configuration,
        DocsNavigationNode root,
        SiteExtraPage page) =>
        DocsLayout(configuration, root, page.Title, page.BodyHtml, page.RelativePath, null, null);

    private static string RenderDocsPagination(
        RenderContext configuration,
        IReadOnlyList<MarkdownPost> orderedPosts,
        int currentIndex)
    {
        if (currentIndex < 0)
        {
            return string.Empty;
        }

        var previous = currentIndex > 0 ? orderedPosts[currentIndex - 1] : null;
        var next = currentIndex + 1 < orderedPosts.Count ? orderedPosts[currentIndex + 1] : null;
        if (previous is null && next is null)
        {
            return string.Empty;
        }

        var body = new StringBuilder();
        body.AppendLine("<nav class=\"docs-pagination\" aria-label=\"Document navigation\">");
        if (previous is not null)
        {
            body.AppendLine($"<a class=\"docs-pagination-previous\" rel=\"prev\" href=\"{Html.Encode(SitePath(configuration, previous.RelativeOutputPath))}\"><small>Previous</small><span>{Html.Encode(GetDocsLabel(previous))}</span></a>");
        }
        else
        {
            body.AppendLine("<span></span>");
        }

        if (next is not null)
        {
            body.AppendLine($"<a class=\"docs-pagination-next\" rel=\"next\" href=\"{Html.Encode(SitePath(configuration, next.RelativeOutputPath))}\"><small>Next</small><span>{Html.Encode(GetDocsLabel(next))}</span></a>");
        }

        body.AppendLine("</nav>");
        return body.ToString();
    }

    private static string RenderDocsTableOfContents(RenderContext configuration, string postBody) =>
        RenderTableOfContents(configuration, postBody)
            .Replace("class=\"post-toc\"", "class=\"post-toc docs-toc\"", StringComparison.Ordinal);

    private static string DocsLayout(
        RenderContext configuration,
        DocsNavigationNode root,
        string title,
        string body,
        string relativePath,
        string? currentDocumentPath,
        string? tableOfContents,
        string? description = null,
        string openGraphType = "website",
        DateTimeOffset? publishedAt = null,
        string? socialImageRelativePath = null)
    {
        var fullTitle = title == configuration.Site.Title ? title : $"{title} - {configuration.Site.Title}";
        var pageDescription = string.IsNullOrWhiteSpace(description) ? configuration.Site.Description : description;
        var canonicalUrl = CombineUrl(configuration.Site.BaseUrl, relativePath);
        var socialImageUrl = CombineUrl(
            configuration.Site.BaseUrl,
            socialImageRelativePath ?? SocialImageAssetPath(DefaultSocialImageFileName));
        var homePath = Html.Encode(SitePath(configuration, "index.html"));
        return $"""
            <!doctype html>
            <html lang="{Html.Encode(configuration.Site.Language)}">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="color-scheme" content="dark">
              <meta name="theme-color" content="{Html.Encode(configuration.Theme.ThemeColor)}">
              <title>{Html.Encode(fullTitle)}</title>
              <meta name="description" content="{Html.Encode(pageDescription)}">
              <link rel="canonical" href="{Html.Encode(canonicalUrl)}">
              <meta property="og:site_name" content="{Html.Encode(configuration.Site.Title)}">
              <meta property="og:type" content="{Html.Encode(openGraphType)}">
              <meta property="og:title" content="{Html.Encode(fullTitle)}">
              <meta property="og:description" content="{Html.Encode(pageDescription)}">
              <meta property="og:url" content="{Html.Encode(canonicalUrl)}">
              <meta property="og:image" content="{Html.Encode(socialImageUrl)}">
              <meta property="og:image:alt" content="{Html.Encode(configuration.Site.Title)} social preview">
              <meta name="twitter:card" content="summary_large_image">
              <meta name="twitter:title" content="{Html.Encode(fullTitle)}">
              <meta name="twitter:description" content="{Html.Encode(pageDescription)}">
              <meta name="twitter:image" content="{Html.Encode(socialImageUrl)}">
              <meta name="twitter:image:alt" content="{Html.Encode(configuration.Site.Title)} social preview">
              {BuildPublishedTimeMetadata(publishedAt)}
              <link rel="stylesheet" href="{Html.Encode(SitePath(configuration, "assets/site.css"))}">
              {BuildFaviconLinks(configuration)}
              {BuildGoogleAnalyticsSnippet(configuration)}
              <script src="{Html.Encode(SitePath(configuration, "assets/site.js"))}" defer></script>
            </head>
            <body class="docs-body">
              <header class="docs-header">
                <a class="docs-brand" href="{homePath}">{Html.Encode(configuration.Site.Title)}</a>
                <button class="docs-menu-toggle" type="button" aria-expanded="false" aria-controls="docs-sidebar" data-docs-menu-toggle>Menu</button>
              </header>
              <div class="docs-shell">
                {RenderDocsSidebar(configuration, root, currentDocumentPath)}
                <main class="docs-main">
                  <article class="docs-content">
            {body}
                  </article>
                </main>
                {tableOfContents ?? string.Empty}
              </div>
              <footer class="docs-footer"><p>Generated by {Html.Encode(configuration.Site.Title)}.</p></footer>
            </body>
            </html>
            """;
    }

    private static string RenderDocsSidebar(
        RenderContext configuration,
        DocsNavigationNode root,
        string? currentDocumentPath)
    {
        var body = new StringBuilder();
        body.AppendLine("<aside id=\"docs-sidebar\" class=\"docs-sidebar\" data-docs-sidebar>");
        body.AppendLine("<nav aria-label=\"Documentation navigation\">");
        body.AppendLine("<ul class=\"docs-nav-list\">");
        RenderDocsNavigationNodes(body, configuration, root, currentDocumentPath);
        body.AppendLine("</ul>");
        body.AppendLine("</nav>");
        body.AppendLine("</aside>");
        return body.ToString();
    }

    private static void RenderDocsNavigationNodes(
        StringBuilder body,
        RenderContext configuration,
        DocsNavigationNode parent,
        string? currentDocumentPath)
    {
        foreach (var node in OrderDocsNavigationChildren(parent))
        {
            if (node.Post is not null)
            {
                var isCurrent = string.Equals(
                    node.Post.RelativeOutputPath,
                    currentDocumentPath,
                    StringComparison.OrdinalIgnoreCase);
                var cssClass = isCurrent ? "docs-nav-link is-current" : "docs-nav-link";
                var current = isCurrent ? " aria-current=\"page\"" : string.Empty;
                body.AppendLine($"<li><a class=\"{cssClass}\" href=\"{Html.Encode(SitePath(configuration, node.Post.RelativeOutputPath))}\"{current}>{Html.Encode(GetDocsLabel(node.Post))}</a></li>");
                continue;
            }

            var isAncestor = ContainsDocsPath(node, currentDocumentPath);
            var folderCssClass = isAncestor ? "docs-nav-folder is-ancestor" : "docs-nav-folder";
            body.AppendLine($"<li class=\"{folderCssClass}\"><span>{Html.Encode(FormatDocsFolderLabel(node.Segment))}</span>");
            body.AppendLine("<ul>");
            RenderDocsNavigationNodes(body, configuration, node, currentDocumentPath);
            body.AppendLine("</ul></li>");
        }
    }

    private static bool ContainsDocsPath(DocsNavigationNode node, string? currentDocumentPath)
    {
        if (string.IsNullOrWhiteSpace(currentDocumentPath))
        {
            return false;
        }

        if (node.Post is not null
            && string.Equals(node.Post.RelativeOutputPath, currentDocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return node.Children.Values.Any(child => ContainsDocsPath(child, currentDocumentPath));
    }

    private static string RenderExtraPage(RenderContext configuration, SiteExtraPage page) =>
        Layout(configuration, page.Title, page.BodyHtml, page.RelativePath);

    private static string BuildLlmsTxt(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var site = configuration.Site;
        var builder = new StringBuilder();
        builder.Append("# ").Append(site.Title).Append('\n');
        if (!string.IsNullOrWhiteSpace(site.Description))
        {
            builder.Append("\n> ").Append(site.Description).Append('\n');
        }

        builder.Append("\n## ").Append(configuration.Text.LlmsPostsHeading).Append('\n');
        if (posts.Count == 0)
        {
            return builder.ToString();
        }

        builder.Append('\n');
        foreach (var post in posts)
        {
            builder.Append("- [").Append(post.FrontMatter.Title).Append("](")
                .Append(CombineUrl(site.BaseUrl, post.RelativeOutputPath)).Append(')');
            if (!string.IsNullOrWhiteSpace(post.FrontMatter.Summary))
            {
                builder.Append(": ").Append(post.FrontMatter.Summary);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private string RenderIndex(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var body = new StringBuilder();
        body.AppendLine("<section class=\"hero\">");
        body.AppendLine($"<p class=\"eyebrow\">{Html.Encode(configuration.Site.Description)}</p>");
        body.AppendLine($"<h1>{Html.Encode(configuration.Site.Title)}</h1>");
        body.AppendLine($"<p>{configuration.Text.IndexLede}</p>");
        body.AppendLine("</section>");
        var latestPosts = posts.Take(1).ToArray();
        var olderPosts = posts.Skip(latestPosts.Length).ToArray();
        body.AppendLine("<section>");
        body.AppendLine("<h2>Latest post</h2>");
        if (posts.Count == 0)
        {
            body.AppendLine($"<p>{configuration.Text.IndexEmpty}</p>");
        }
        else
        {
            body.AppendLine("<div class=\"featured-post-list\">");
            foreach (var post in latestPosts)
            {
                body.AppendLine(RenderPostCard(configuration, post, "featured-card"));
            }

            body.AppendLine("</div>");
        }

        body.AppendLine("</section>");
        body.AppendLine("<section>");
        body.AppendLine($"<h2>{configuration.Text.OlderPostsHeading}</h2>");
        if (olderPosts.Length == 0)
        {
            body.AppendLine($"<p>{configuration.Text.OlderPostsEmpty}</p>");
        }
        else
        {
            body.AppendLine(RenderMonthlyPostPanels(configuration, olderPosts));
        }

        body.AppendLine("</section>");
        return Layout(configuration, configuration.Site.Title, body.ToString(), "index.html");
    }

    private string RenderArchives(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var body = new StringBuilder();
        body.AppendLine("<h1>Archives</h1>");
        foreach (var group in posts.GroupBy(post => SiteFormatting.FormatMonth(configuration.Site, post.FrontMatter.Date)))
        {
            body.AppendLine($"<h2>{Html.Encode(group.Key)}</h2>");
            body.AppendLine("<div class=\"archive-post-list\">");
            foreach (var post in group)
            {
                body.AppendLine(RenderArchivePostRow(configuration, post));
            }

            body.AppendLine("</div>");
        }

        if (posts.Count == 0)
        {
            body.AppendLine($"<p>{configuration.Text.ArchivesEmpty}</p>");
        }

        return Layout(configuration, "Archives", body.ToString(), "archives.html");
    }

    private string RenderTags(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var tags = posts
            .SelectMany(post => post.FrontMatter.Tags.Select(tag => new { Tag = tag, Post = post }))
            .GroupBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var body = new StringBuilder();
        body.AppendLine("<section class=\"hero\">");
        body.AppendLine("<p class=\"eyebrow\">Tags</p>");
        body.AppendLine($"<h1>{configuration.Text.TagsHeading}</h1>");
        body.AppendLine($"<p>{configuration.Text.TagsIntro}</p>");
        body.AppendLine("</section>");
        body.AppendLine("<section>");
        body.AppendLine($"<h2>{configuration.Text.ExistingTagsHeading}</h2>");
        if (tags.Length == 0)
        {
            body.AppendLine($"<p>{configuration.Text.TagsEmpty}</p>");
        }
        else
        {
            body.AppendLine("<div class=\"tag-cloud\">");
            foreach (var group in tags)
            {
                var tag = group.Key;
                var count = group.Select(item => item.Post).Distinct().Count();
                var href = $"{SitePath(configuration, "search.html")}?tag={Uri.EscapeDataString(tag)}";
                body.AppendLine($"""
                    <a class="tag-link" href="{Html.Encode(href)}">
                      <span>{Html.Encode(tag)}</span>
                      <small>{count.ToString(CultureInfo.InvariantCulture)} post(s)</small>
                    </a>
                    """);
            }

            body.AppendLine("</div>");
        }
        body.AppendLine("</section>");

        return Layout(configuration, "Tags", body.ToString(), "tags.html");
    }


    private string RenderPost(RenderContext configuration, MarkdownPost post)
    {
        var postBody = AddNewTabAttributesToExternalPostLinks(
            configuration,
            NormalizePostBodyHeadings(Markdown.ToHtml(post.MarkdownBody, _pipeline)));
        var toc = RenderTableOfContents(configuration, postBody);
        var body = new StringBuilder();
        body.AppendLine("<div class=\"post-layout\">");
        body.AppendLine("<article class=\"post\">");
        body.AppendLine($"<p class=\"eyebrow\">{Html.Encode(SiteFormatting.FormatDateTime(configuration.Site, post.FrontMatter.Date))}</p>");
        body.AppendLine($"<h1 class=\"post-title\">{Html.Encode(post.FrontMatter.Title)}</h1>");
        if (!string.IsNullOrWhiteSpace(post.FrontMatter.Summary))
        {
            body.AppendLine($"<p class=\"summary\">{Html.Encode(post.FrontMatter.Summary)}</p>");
        }

        if (post.FrontMatter.Tags.Count > 0)
        {
            body.AppendLine("<div class=\"tags\">");
            foreach (var tag in post.FrontMatter.Tags)
            {
                body.AppendLine($"<span>{Html.Encode(tag)}</span>");
            }

            body.AppendLine("</div>");
        }

        body.AppendLine(postBody);
        body.AppendLine("</article>");
        body.AppendLine(toc);
        body.AppendLine("</div>");
        return Layout(
            configuration,
            post.FrontMatter.Title,
            body.ToString(),
            post.RelativeOutputPath,
            post.FrontMatter.Summary,
            "article",
            post.FrontMatter.Date,
            PostSocialImagePath(post));
    }

    private static string RenderTableOfContents(RenderContext configuration, string postBody)
    {
        return RenderTemplateTableOfContents(
            configuration,
            ExtractTemplateHeadings(postBody));
    }

    private static IReadOnlyList<SiteTemplateHeading> ExtractTemplateHeadings(string postBody) =>
        HeadingRegex.Matches(postBody)
            .Select(match => new
            {
                Level = int.Parse(match.Groups["level"].Value, CultureInfo.InvariantCulture),
                Id = match.Groups["id"].Value,
                Text = StripTagsRegex.Replace(match.Groups["text"].Value, string.Empty)
            })
            .Where(heading => !string.IsNullOrWhiteSpace(heading.Id) && !string.IsNullOrWhiteSpace(heading.Text))
            .Select(heading => new SiteTemplateHeading(heading.Level, heading.Id, heading.Text))
            .ToArray();

    private static string NormalizePostBodyHeadings(string postBody)
    {
        var withoutNestedTitle = BodyHeadingOneOpenRegex.Replace(postBody, "<h2${attributes}>");
        return BodyHeadingOneCloseRegex.Replace(withoutNestedTitle, "</h2>");
    }

    private static string AddNewTabAttributesToExternalPostLinks(RenderContext configuration, string postBody)
    {
        if (!Uri.TryCreate(NormalizeBaseUrl(configuration.Site.BaseUrl), UriKind.Absolute, out var siteUri))
        {
            return postBody;
        }

        return AnchorTagRegex.Replace(postBody, match =>
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (!ShouldOpenInNewTab(siteUri, href))
            {
                return match.Value;
            }

            var targetAttribute = HasAnchorAttribute(match.Value, "target") ? string.Empty : " target=\"_blank\"";
            var relAttribute = HasAnchorAttribute(match.Value, "rel") ? string.Empty : " rel=\"noopener noreferrer\"";
            return $"<a{match.Groups["before"].Value} href=\"{match.Groups["href"].Value}\"{match.Groups["after"].Value}{targetAttribute}{relAttribute}>";
        });
    }

    private static bool ShouldOpenInNewTab(Uri siteUri, string href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#'))
        {
            return false;
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var linkUri))
        {
            return false;
        }

        if (!linkUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !linkUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !Uri.Compare(siteUri, linkUri, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase).Equals(0);
    }

    private static bool HasAnchorAttribute(string anchorTag, string attributeName) =>
        anchorTag.Contains($"{attributeName}=", StringComparison.OrdinalIgnoreCase);

    private static string RenderPostCard(RenderContext configuration, MarkdownPost post, string? extraClass = null)
    {
        var href = SitePath(configuration, post.RelativeOutputPath);
        var displayDate = SiteFormatting.FormatDateTime(configuration.Site, post.FrontMatter.Date);
        var cardClass = string.IsNullOrWhiteSpace(extraClass) ? "card" : $"card {extraClass}";
        return $"""
            <article class="{cardClass}">
              <time datetime="{post.FrontMatter.Date:O}">{Html.Encode(displayDate)}</time>
              <h3><a href="{Html.Encode(href)}">{Html.Encode(post.FrontMatter.Title)}</a></h3>
              <p>{Html.Encode(post.FrontMatter.Summary)}</p>
            </article>
            """;
    }

    private static string RenderArchivePostRow(RenderContext configuration, MarkdownPost post)
    {
        var href = SitePath(configuration, post.RelativeOutputPath);
        var displayDate = SiteFormatting.FormatDateTime(configuration.Site, post.FrontMatter.Date);
        return $"""
            <article class="archive-row">
              <time datetime="{post.FrontMatter.Date:O}">{Html.Encode(displayDate)}</time>
              <h3><a href="{Html.Encode(href)}">{Html.Encode(post.FrontMatter.Title)}</a></h3>
              <p>{Html.Encode(post.FrontMatter.Summary)}</p>
            </article>
            """;
    }

    private static string RenderMonthlyPostPanels(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var body = new StringBuilder();
        body.AppendLine("<div class=\"archive-panels\">");
        foreach (var group in posts.GroupBy(post => SiteFormatting.FormatMonth(configuration.Site, post.FrontMatter.Date)))
        {
            var countText = group.Count().ToString(CultureInfo.InvariantCulture);
            body.AppendLine("<details class=\"archive-panel\">");
            body.AppendLine($"<summary><span>{Html.Encode(group.Key)}</span><small>{countText} post(s)</small></summary>");
            body.AppendLine("<div class=\"archive-post-list\">");
            foreach (var post in group)
            {
                body.AppendLine(RenderArchivePostRow(configuration, post));
            }

            body.AppendLine("</div>");
            body.AppendLine("</details>");
        }

        body.AppendLine("</div>");
        return body.ToString();
    }

    private string RenderSearch(RenderContext configuration, DateTimeOffset generatedAt)
    {
        var indexPath = $"{SitePath(configuration, "search-index.json")}?v={generatedAt:yyyyMMddHHmmss}";
        var scriptPath = SitePath(configuration, "assets/search.js");
        var body = new StringBuilder();
        body.AppendLine("<section class=\"hero search-hero\">");
        body.AppendLine("<p class=\"eyebrow\">Search</p>");
        body.AppendLine($"<h1>{configuration.Text.SearchHeading}</h1>");
        body.AppendLine($"<p>{configuration.Text.SearchIntro}</p>");
        body.AppendLine($"<form class=\"search-box\" role=\"search\" action=\"{Html.Encode(SitePath(configuration, "search.html"))}\" method=\"get\">");
        body.AppendLine($"<input id=\"search-input\" name=\"q\" type=\"search\" autocomplete=\"off\" autocapitalize=\"off\" spellcheck=\"false\" enterkeyhint=\"search\" placeholder=\"{configuration.Text.SearchInputPlaceholder}\" aria-label=\"{configuration.Text.SearchInputLabel}\" autofocus>");
        body.AppendLine("</form>");
        body.AppendLine("<p id=\"search-scope\" class=\"search-scope\" role=\"status\" aria-live=\"polite\" hidden></p>");
        body.AppendLine($"<p id=\"search-status\" class=\"search-status\" role=\"status\" aria-live=\"polite\">{configuration.Text.SearchLoading}</p>");
        body.AppendLine("</section>");
        body.AppendLine($"<section id=\"search-app\" data-index=\"{Html.Encode(indexPath)}\">");
        body.AppendLine("<div id=\"search-results\" class=\"archive-post-list\"></div>");
        body.AppendLine("<noscript><p>" + configuration.Text.SearchNoscriptPrefix + "<a href=\"" + Html.Encode(SitePath(configuration, "archives.html")) + "\">" + configuration.Text.SearchNoscriptArchivesLinkText + "</a>" + configuration.Text.SearchNoscriptSuffix + "</p></noscript>");
        body.AppendLine("</section>");
        body.AppendLine($"<script src=\"{Html.Encode(scriptPath)}\" defer></script>");
        return Layout(configuration, "Search", body.ToString(), "search.html");
    }


    private string BuildSearchIndex(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var documents = new List<SearchDocument>(posts.Count);
        foreach (var post in posts)
        {
            var plain = Markdown.ToPlainText(post.MarkdownBody, _pipeline);
            documents.Add(new SearchDocument(
                post.FrontMatter.Title,
                post.FrontMatter.Summary,
                post.FrontMatter.Tags,
                SitePath(configuration, post.RelativeOutputPath),
                SiteFormatting.FormatDateTime(configuration.Site, post.FrontMatter.Date),
                NormalizeForIndex(plain)));
        }

        var index = new SearchIndex(
            configuration.Site.Title,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            documents);
        return JsonSerializer.Serialize(index, SearchSerializerContext.SearchIndex);
    }


    private static string NormalizeForIndex(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var previousWhitespace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousWhitespace = true;
                continue;
            }

            builder.Append(ch);
            previousWhitespace = false;
        }

        var normalized = builder.ToString().TrimEnd();
        return normalized.Length > SearchIndexMaxLength ? normalized[..SearchIndexMaxLength] : normalized;
    }

    private string RenderFeed(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };
        using var stringWriter = new Utf8StringWriter();
        using var writer = XmlWriter.Create(stringWriter, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("rss");
        writer.WriteAttributeString("version", "2.0");
        writer.WriteStartElement("channel");
        writer.WriteElementString("title", configuration.Site.Title);
        writer.WriteElementString("description", configuration.Site.Description);
        writer.WriteElementString("link", NormalizeBaseUrl(configuration.Site.BaseUrl));
        foreach (var post in posts.Take(20))
        {
            var url = CombineUrl(configuration.Site.BaseUrl, post.RelativeOutputPath);
            writer.WriteStartElement("item");
            writer.WriteElementString("title", post.FrontMatter.Title);
            writer.WriteElementString("description", post.FrontMatter.Summary);
            writer.WriteElementString("link", url);
            writer.WriteElementString("guid", url);
            writer.WriteElementString("pubDate", post.FrontMatter.Date.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
        return stringWriter.ToString();
    }

    private string RenderSitemap(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };
        using var stringWriter = new Utf8StringWriter();
        using var writer = XmlWriter.Create(stringWriter, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");
        WriteSitemapUrl(writer, CombineUrl(configuration.Site.BaseUrl, "index.html"));
        WriteSitemapUrl(writer, CombineUrl(configuration.Site.BaseUrl, "archives.html"));
        WriteSitemapUrl(writer, CombineUrl(configuration.Site.BaseUrl, "tags.html"));
        foreach (var extraPage in configuration.ExtraPages)
        {
            if (extraPage.IncludeInSitemap)
            {
                WriteSitemapUrl(writer, CombineUrl(configuration.Site.BaseUrl, extraPage.RelativePath));
            }
        }

        WriteSitemapUrl(writer, CombineUrl(configuration.Site.BaseUrl, "search.html"));
        foreach (var post in posts)
        {
            WriteSitemapUrl(writer, CombineUrl(configuration.Site.BaseUrl, post.RelativeOutputPath), post.FrontMatter.Date);
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
        return stringWriter.ToString();
    }

    private static void WriteSitemapUrl(XmlWriter writer, string loc, DateTimeOffset? lastModified = null)
    {
        writer.WriteStartElement("url");
        writer.WriteElementString("loc", loc);
        if (lastModified is not null)
        {
            writer.WriteElementString("lastmod", lastModified.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        writer.WriteEndElement();
    }

    private static string Layout(
        RenderContext configuration,
        string title,
        string body,
        string relativePath,
        string? description = null,
        string openGraphType = "website",
        DateTimeOffset? publishedAt = null,
        string? socialImageRelativePath = null)
    {
        var fullTitle = title == configuration.Site.Title ? title : $"{title} - {configuration.Site.Title}";
        var pageDescription = string.IsNullOrWhiteSpace(description) ? configuration.Site.Description : description;
        var canonicalUrl = CombineUrl(configuration.Site.BaseUrl, relativePath);
        var socialImageUrl = CombineUrl(configuration.Site.BaseUrl, socialImageRelativePath ?? SocialImageAssetPath(DefaultSocialImageFileName));
        var socialImageAlt = $"{configuration.Site.Title} social preview";
        return $"""
            <!doctype html>
            <html lang="{Html.Encode(configuration.Site.Language)}">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="color-scheme" content="dark">
              <meta name="theme-color" content="{configuration.Theme.ThemeColor}">
              <title>{Html.Encode(fullTitle)}</title>
              <meta name="description" content="{Html.Encode(pageDescription)}">
              <link rel="canonical" href="{Html.Encode(canonicalUrl)}">
              <meta property="og:site_name" content="{Html.Encode(configuration.Site.Title)}">
              <meta property="og:type" content="{Html.Encode(openGraphType)}">
              <meta property="og:title" content="{Html.Encode(fullTitle)}">
              <meta property="og:description" content="{Html.Encode(pageDescription)}">
              <meta property="og:url" content="{Html.Encode(canonicalUrl)}">
              <meta property="og:image" content="{Html.Encode(socialImageUrl)}">
              <meta property="og:image:alt" content="{Html.Encode(socialImageAlt)}">
              <meta name="twitter:card" content="summary_large_image">
              <meta name="twitter:title" content="{Html.Encode(fullTitle)}">
              <meta name="twitter:description" content="{Html.Encode(pageDescription)}">
              <meta name="twitter:image" content="{Html.Encode(socialImageUrl)}">
              <meta name="twitter:image:alt" content="{Html.Encode(socialImageAlt)}">
              {BuildPublishedTimeMetadata(publishedAt)}
              <link rel="stylesheet" href="{Html.Encode(SitePath(configuration, "assets/site.css"))}">
              {BuildFaviconLinks(configuration)}
              {BuildGoogleAnalyticsSnippet(configuration)}
              <link rel="alternate" type="application/rss+xml" title="{Html.Encode(configuration.Site.Title)}" href="{Html.Encode(SitePath(configuration, "feed.xml"))}">
              <script src="{Html.Encode(SitePath(configuration, "assets/site.js"))}" defer></script>
            </head>
            <body>
              {BuildSiteHeader(configuration)}
              <main>
            {body}
              </main>
              <footer class="site-footer">
                <p>Generated by {Html.Encode(configuration.Site.Title)}.</p>
              </footer>
            </body>
            </html>
            """;
    }

    private static string BuildPublishedTimeMetadata(DateTimeOffset? publishedAt)
    {
        if (publishedAt is null)
        {
            return string.Empty;
        }

        return $"<meta property=\"article:published_time\" content=\"{publishedAt.Value:O}\">";
    }

    private static async Task WriteTextAsync(string outputRoot, string relativePath, string contents, List<string> generated, CancellationToken cancellationToken)
    {
        var fullPath = SafeCombine(outputRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, contents, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        generated.Add(fullPath);
    }

    private static async Task WriteBinaryAssetAsync(string outputRoot, string relativePath, byte[] contents, List<string> generated, CancellationToken cancellationToken)
    {
        var fullPath = SafeCombine(outputRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, contents, cancellationToken).ConfigureAwait(false);
        generated.Add(fullPath);
    }

    private static async Task WriteBundledFaviconAssetsAsync(string outputRoot, RenderContext configuration, List<string> generated, CancellationToken cancellationToken)
    {
        foreach (var assetFile in BundledFaviconAssets)
        {
            await WriteBinaryAssetAsync(
                    outputRoot,
                    FaviconAssetPath(assetFile),
                    LoadBundledBytes(configuration.FaviconSourceDirectory, assetFile, "favicon asset"),
                    generated,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static byte[] LoadBundledBytes(string sourceDirectory, string fileName, string assetDescription)
    {
        var sourcePath = Path.Combine(sourceDirectory, fileName);
        if (!File.Exists(sourcePath))
        {
            throw new InvalidOperationException($"Bundled {assetDescription} '{sourcePath}' does not exist.");
        }

        return File.ReadAllBytes(sourcePath);
    }

    private static async Task<byte[]> BuildDefaultSocialImageAsync(RenderContext configuration, CancellationToken cancellationToken)
    {
        var generator = new SocialImageGenerator(LoadBundledBytes(configuration.FaviconSourceDirectory, SiteIconFileName, "favicon asset"));
        return await generator.BuildSiteImageAsync(configuration.Site.Title, configuration.Theme.DefaultSocialSubtitle, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> BuildPostSocialImageAsync(RenderContext configuration, MarkdownPost post, CancellationToken cancellationToken)
    {
        var generator = new SocialImageGenerator(LoadBundledBytes(configuration.FaviconSourceDirectory, SiteIconFileName, "favicon asset"));
        return await generator.BuildPostImageAsync(configuration.Site.Title, post.FrontMatter.Title, cancellationToken).ConfigureAwait(false);
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Output path '{relativePath}' escapes output directory.");
        }

        return fullPath;
    }

    private static string NormalizeBaseUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/";

    private static string CombineUrl(string baseUrl, string relativePath) => $"{NormalizeBaseUrl(baseUrl)}{relativePath.TrimStart('/').Replace('\\', '/')}";


    private static string SitePath(RenderContext configuration, string relativePath)
    {
        var basePath = "/";
        if (Uri.TryCreate(NormalizeBaseUrl(configuration.Site.BaseUrl), UriKind.Absolute, out var uri))
        {
            basePath = uri.AbsolutePath;
        }

        return $"{basePath.TrimEnd('/')}/{relativePath.TrimStart('/').Replace('\\', '/')}";
    }

    private static string FaviconAssetPath(string fileName) => $"{FaviconOutputDirectory}/{fileName}";

    private static string SocialImageAssetPath(string fileName) => $"{SocialImageOutputDirectory}/{fileName}";

    private static string PostSocialImagePath(MarkdownPost post) => $"{SocialImageOutputDirectory}/posts/{post.Slug}.png";

    private static string BuildGoogleAnalyticsSnippet(RenderContext configuration)
    {
        var measurementId = ResolveGoogleAnalyticsMeasurementId(configuration);
        if (string.IsNullOrWhiteSpace(measurementId))
        {
            return string.Empty;
        }

        var normalizedMeasurementId = measurementId.Trim();
        if (!Regex.IsMatch(normalizedMeasurementId, "^G-[A-Za-z0-9]+$", RegexOptions.CultureInvariant))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"<script async src=\"https://www.googletagmanager.com/gtag/js?id={normalizedMeasurementId}\"></script>");
        builder.AppendLine("<script>");
        builder.AppendLine("  window.dataLayer = window.dataLayer || [];");
        builder.AppendLine("  function gtag(){dataLayer.push(arguments);}");
        builder.AppendLine("  gtag('js', new Date());");
        builder.AppendLine($"  gtag('config', {JsonSerializer.Serialize(normalizedMeasurementId)});");
        builder.Append("</script>");
        return builder.ToString();
    }

    private static string ResolveGoogleAnalyticsMeasurementId(RenderContext configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.Site.GoogleAnalyticsMeasurementId))
        {
            return configuration.Site.GoogleAnalyticsMeasurementId.Trim();
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("GA_MEASUREMENT_ID");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        fromEnvironment = Environment.GetEnvironmentVariable("GOOGLE_ANALYTICS_MEASUREMENT_ID");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? string.Empty : fromEnvironment.Trim();
    }

    private static string BuildFaviconLinks(RenderContext configuration)
    {
        if (!configuration.HasFaviconAssets)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"              <link rel=\"icon\" type=\"image/x-icon\" sizes=\"any\" href=\"{Html.Encode(SitePath(configuration, FaviconAssetPath("favicon.ico")))}\">");
        foreach (var (fileName, sizes) in PngFaviconAssets)
        {
            builder.AppendLine($"              <link rel=\"icon\" type=\"image/png\" sizes=\"{sizes}\" href=\"{Html.Encode(SitePath(configuration, FaviconAssetPath(fileName)))}\">");
        }

        builder.AppendLine($"              <link rel=\"apple-touch-icon\" sizes=\"180x180\" href=\"{Html.Encode(SitePath(configuration, FaviconAssetPath("apple-touch-icon.png")))}\">");
        builder.Append($"              <link rel=\"manifest\" href=\"{Html.Encode(SitePath(configuration, "site.webmanifest"))}\">");
        return builder.ToString();
    }

    private static string BuildSiteHeader(RenderContext configuration)
    {
        var homePath = Html.Encode(SitePath(configuration, "index.html"));
        var archivesPath = Html.Encode(SitePath(configuration, "archives.html"));
        var tagsPath = Html.Encode(SitePath(configuration, "tags.html"));
        var searchPath = Html.Encode(SitePath(configuration, "search.html"));
        var feedPath = Html.Encode(SitePath(configuration, "feed.xml"));
        var navLinks = new List<string>
        {
            $"<a class=\"site-nav-home\" href=\"{homePath}\">Home</a>",
            $"<a href=\"{archivesPath}\">Archives</a>",
            $"<a href=\"{tagsPath}\">Tags</a>",
        };
        foreach (var extraPage in configuration.ExtraPages)
        {
            if (string.IsNullOrEmpty(extraPage.NavLabel))
            {
                continue;
            }

            var extraPath = Html.Encode(SitePath(configuration, extraPage.RelativePath));
            var cssClass = string.IsNullOrEmpty(extraPage.NavCssClass)
                ? string.Empty
                : $" class=\"{Html.Encode(extraPage.NavCssClass)}\"";
            navLinks.Add($"<a{cssClass} href=\"{extraPath}\">{Html.Encode(extraPage.NavLabel)}</a>");
        }

        navLinks.Add($"<a href=\"{searchPath}\">Search</a>");
        navLinks.Add($"<a class=\"rss-nav-link\" href=\"{feedPath}\">RSS</a>");
        var navHtml = string.Join("\n        ", navLinks);
        var menuLabel = Html.Encode(configuration.Text.MenuLabel);
        var navigationLabel = Html.Encode(configuration.Text.SiteNavigationLabel);
        return $"""
              <header class="site-header">
                <a class="brand" href="{homePath}">{Html.Encode(configuration.Site.Title)}</a>
                <div class="site-nav-shell">
                  <a class="site-home-link site-icon-button" href="{homePath}" aria-label="Home">
                    {BuildHomeIconSvg()}
                    <span class="visually-hidden">Home</span>
                  </a>
                  {BuildHeaderSearchForm(configuration)}
                  <button class="site-menu-toggle site-icon-button" type="button" aria-expanded="false" aria-controls="site-menu" aria-label="{menuLabel}" data-site-menu-toggle>
                    {BuildMenuIconSvg()}
                    <span class="visually-hidden">{menuLabel}</span>
                  </button>
                  <nav id="site-menu" class="site-nav" data-site-nav aria-label="{navigationLabel}">
                    {navHtml}
                  </nav>
                </div>
              </header>
            """;
    }

    private static string BuildHeaderSearchForm(RenderContext configuration)
    {
        var searchPath = Html.Encode(SitePath(configuration, "search.html"));
        var inputLabel = Html.Encode(configuration.Text.SearchInputLabel);
        var placeholder = Html.Encode(configuration.Text.HeaderSearchPlaceholder);
        var buttonLabel = Html.Encode(configuration.Text.SearchButtonLabel);
        return $"""
                  <form class="site-header-search browser-search" role="search" action="{searchPath}" method="get">
                    <label class="visually-hidden" for="header-search-input">{inputLabel}</label>
                    <input id="header-search-input" name="q" type="search" autocomplete="off" autocapitalize="off" spellcheck="false" enterkeyhint="search" placeholder="{placeholder}" aria-label="{inputLabel}">
                    <button type="submit" aria-label="{buttonLabel}">
                      {BuildSearchIconSvg()}
                      <span class="visually-hidden">{buttonLabel}</span>
                    </button>
                  </form>
                """;
    }

    private static string BuildHomeIconSvg() => """
        <svg aria-hidden="true" viewBox="0 0 24 24">
          <path d="M4.5 10.5L12 4.5l7.5 6v8.25a.75.75 0 0 1-.75.75h-4.5a.75.75 0 0 1-.75-.75V15a1.5 1.5 0 0 0-3 0v3.75a.75.75 0 0 1-.75.75h-4.5a.75.75 0 0 1-.75-.75z" />
        </svg>
        """;

    private static string BuildSearchIconSvg() => """
        <svg aria-hidden="true" viewBox="0 0 24 24">
          <circle cx="11" cy="11" r="5.5" />
          <path d="M15.25 15.25L19 19" />
        </svg>
        """;

    private static string BuildMenuIconSvg() => """
        <svg aria-hidden="true" viewBox="0 0 24 24">
          <path d="M4.5 7.5h15M4.5 12h15M4.5 16.5h15" />
        </svg>
        """;

    private static string BuildWebManifest(RenderContext configuration)
    {
        var manifest = new
        {
            name = configuration.Site.Title,
            short_name = configuration.Site.Title,
            description = configuration.Site.Description,
            start_url = SitePath(configuration, "index.html"),
            scope = SitePath(configuration, string.Empty),
            display = "standalone",
            background_color = configuration.Theme.ThemeColor,
            theme_color = configuration.Theme.ThemeColor,
            icons = ManifestIconAssets.Select(asset => new
            {
                src = SitePath(configuration, FaviconAssetPath(asset.FileName)),
                sizes = asset.Sizes,
                type = "image/png"
            })
        };

        return JsonSerializer.Serialize(manifest, WebManifestSerializerOptions);
    }

    private static string BuildDocsCss(RenderContext configuration) =>
        BuildCss(configuration) + """

        @layer layout {
          .docs-header,
          .docs-shell,
          .docs-footer {
            inline-size: min(100%, 90rem);
            margin-inline: auto;
          }
          .docs-header {
            min-block-size: 4.25rem;
            padding-inline: 1.25rem;
            display: flex;
            justify-content: space-between;
            align-items: center;
            position: sticky;
            inset-block-start: 0;
            z-index: 20;
            border-block-end: 1px solid var(--border);
            background: rgba(13, 17, 23, 0.94);
            backdrop-filter: blur(0.8rem);
          }
          .docs-shell {
            display: grid;
            grid-template-columns: minmax(13rem, 16rem) minmax(0, 1fr) minmax(12rem, 15rem);
            gap: clamp(1.25rem, 3vw, 3rem);
            align-items: start;
            padding: 2rem 1.25rem 4rem;
          }
          .docs-main {
            min-inline-size: 0;
          }
          .docs-sidebar {
            position: sticky;
            inset-block-start: 5.75rem;
            max-block-size: calc(100svh - 7rem);
            overflow-y: auto;
            padding-inline-end: 0.75rem;
          }
          .docs-footer {
            padding: 1.5rem 1.25rem 3rem;
            color: var(--muted);
            border-block-start: 1px solid var(--border);
          }
        }

        @layer components {
          .docs-brand {
            color: #f0f6fc;
            font-size: 1.15rem;
            font-weight: 750;
            text-decoration: none;
          }
          .docs-brand::before {
            content: "◆ ";
            color: var(--accent);
          }
          .docs-menu-toggle {
            display: none;
            padding: 0.45rem 0.8rem;
            color: #f0f6fc;
            background: var(--panel);
            border: 1px solid var(--border);
            border-radius: 0.45rem;
            cursor: pointer;
          }
          .docs-nav-list,
          .docs-nav-list ul {
            display: grid;
            gap: 0.12rem;
          }
          .docs-nav-list ul {
            margin: 0.25rem 0 0.5rem 0.7rem;
            padding-inline-start: 0.7rem;
            border-inline-start: 1px solid var(--border);
          }
          .docs-nav-folder > span {
            display: block;
            margin: 0.7rem 0 0.25rem;
            color: var(--text);
            font-size: 0.78rem;
            font-weight: 700;
            letter-spacing: 0.04em;
            text-transform: uppercase;
          }
          .docs-nav-folder.is-ancestor > span {
            color: var(--accent);
          }
          .docs-nav-link {
            display: block;
            padding: 0.34rem 0.55rem;
            color: var(--muted);
            border-radius: 0.35rem;
            text-decoration: none;
          }
          .docs-nav-link:hover,
          .docs-nav-link:focus-visible {
            color: #f0f6fc;
            background: rgba(88, 166, 255, 0.13);
          }
          .docs-nav-link.is-current {
            color: #f0f6fc;
            background: rgba(88, 166, 255, 0.2);
            box-shadow: inset 0.2rem 0 var(--accent);
          }
          .docs-content {
            inline-size: min(100%, 48rem);
            margin-inline: auto;
          }
          .docs-content h1 {
            margin-block: 0 1rem;
            font-size: clamp(2rem, 5vw, 3.3rem);
          }
          .docs-content h2 {
            margin-block: 2.5rem 0.8rem;
            padding-block-end: 0.45rem;
            border-block-end: 1px solid var(--border);
          }
          .docs-content h3 {
            margin-block: 1.8rem 0.6rem;
          }
          .docs-content :where(p, ul, ol, blockquote, pre) {
            margin-block: 1rem;
          }
          .docs-content :where(ul, ol) {
            padding-inline-start: 1.5rem;
          }
          .docs-content ul {
            list-style: disc;
          }
          .docs-content ol {
            list-style: decimal;
          }
          .docs-content li + li {
            margin-block-start: 0.35rem;
          }
          .docs-content blockquote {
            padding-inline-start: 1rem;
            color: var(--muted);
            border-inline-start: 0.25rem solid var(--accent);
          }
          .docs-description {
            color: var(--muted);
            font-size: 1.1rem;
          }
          .docs-hero {
            padding: clamp(2rem, 7vw, 5rem);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            background:
              linear-gradient(135deg, rgba(88, 166, 255, 0.22), transparent 52%),
              var(--panel);
          }
          .docs-hero h1 {
            margin-block: 0.5rem 1rem;
          }
          .docs-kicker {
            color: var(--accent);
            font-weight: 700;
            letter-spacing: 0.08em;
            text-transform: uppercase;
          }
          .docs-primary-link {
            display: inline-block;
            margin-block-start: 1rem;
            padding: 0.65rem 1rem;
            color: var(--accent-contrast);
            background: var(--accent);
            border-radius: 0.45rem;
            font-weight: 700;
            text-decoration: none;
          }
          .docs-toc {
            margin: 0;
            padding: 0;
            border: 0;
            border-radius: 0;
            background: transparent;
            box-shadow: none;
          }
          .docs-toc.post-toc {
            inset-block-start: 5.75rem;
          }
          .docs-pagination {
            display: grid;
            grid-template-columns: repeat(2, minmax(0, 1fr));
            gap: 1rem;
            margin-block-start: 3rem;
            padding-block-start: 1.5rem;
            border-block-start: 1px solid var(--border);
          }
          .docs-pagination a {
            display: grid;
            gap: 0.25rem;
            padding: 0.85rem;
            color: #f0f6fc;
            border: 1px solid var(--border);
            border-radius: 0.5rem;
            text-decoration: none;
          }
          .docs-pagination a:hover,
          .docs-pagination a:focus-visible {
            border-color: var(--accent);
            background: rgba(88, 166, 255, 0.1);
          }
          .docs-pagination-next {
            text-align: end;
          }
          .docs-pagination small {
            color: var(--muted);
          }
        }

        @media (max-width: 70rem) {
          .docs-shell {
            grid-template-columns: minmax(0, 1fr) minmax(12rem, 15rem);
          }
          .docs-sidebar {
            position: fixed;
            inset: 4.25rem auto 0 0;
            z-index: 15;
            inline-size: min(19rem, 85vw);
            max-block-size: none;
            padding: 1rem;
            border-inline-end: 1px solid var(--border);
            background: var(--surface);
            box-shadow: var(--shadow);
            transform: translateX(-105%);
            transition: transform 0.2s ease;
          }
          .docs-sidebar.docs-sidebar-open {
            transform: translateX(0);
          }
          .docs-menu-toggle {
            display: inline-flex;
          }
        }

        @media (max-width: 52rem) {
          .docs-shell {
            grid-template-columns: 1fr;
            padding-block-start: 1.25rem;
          }
          .docs-toc.post-toc {
            position: static;
            grid-row: 2;
            max-block-size: none;
            padding-block-start: 1.25rem;
            border-block-start: 1px solid var(--border);
          }
        }
        """;

    private static string BuildCss(RenderContext configuration)
    {
        var css = BuildBaseCss().Replace("__LITHOSHARP_BRAND_PREFIX__", configuration.Theme.BrandPrefix, StringComparison.Ordinal);
        var additional = configuration.Theme.AdditionalCss;
        return string.IsNullOrEmpty(additional) ? css : css + "\n\n" + additional;
    }

    private static string BuildDocsScript() =>
        BuildSiteScript() + """

        (() => {
          "use strict";

          const toggle = document.querySelector("[data-docs-menu-toggle]");
          const sidebar = document.querySelector("[data-docs-sidebar]");
          if (!toggle || !sidebar) {
            return;
          }

          const setOpen = (isOpen) => {
            toggle.setAttribute("aria-expanded", String(isOpen));
            sidebar.classList.toggle("docs-sidebar-open", isOpen);
          };

          toggle.addEventListener("click", () => {
            setOpen(toggle.getAttribute("aria-expanded") !== "true");
          });

          for (const link of sidebar.querySelectorAll("a")) {
            link.addEventListener("click", () => setOpen(false));
          }

          document.addEventListener("keydown", (event) => {
            if (event.key === "Escape") {
              setOpen(false);
              toggle.focus();
            }
          });

          window.addEventListener("resize", () => setOpen(false));
          setOpen(false);
        })();
        """;

    private static string BuildBaseCss() => """
        @layer reset, base, layout, components, utilities;

        @view-transition { navigation: auto; }

        :root {
          color-scheme: dark;
          --surface: #0d1117;
          --panel: #161b22;
          --panel-strong: #21262d;
          --text: #c9d1d9;
          --muted: #8b949e;
          --accent: #58a6ff;
          --accent-contrast: #0d1117;
          --success: #3fb950;
          --attention: #d29922;
          --danger: #f85149;
          --done: #bc8cff;
          --border: #30363d;
          --radius: 1.1rem;
          --shadow: 0 1rem 3rem rgba(1, 4, 9, 0.65);
          --measure: min(72rem, 100% - 2.5rem);
          --space: clamp(1rem, 0.6rem + 1.6vw, 1.75rem);
          scrollbar-color: var(--accent) transparent;
          accent-color: var(--accent);
        }

        @layer reset {
          *, *::before, *::after { box-sizing: border-box; }
          body, h1, h2, h3, h4, p, figure, ul { margin: 0; }
          ul { padding: 0; list-style: none; }
          :focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; border-radius: 0.35rem; }
          img { max-inline-size: 100%; block-size: auto; }
          button, input { font: inherit; }
        }

        @layer base {
          body {
            font-family: Consolas, "Cascadia Code", "Yu Gothic UI", "Meiryo", monospace;
            background:
              radial-gradient(80rem 45rem at 100% -10%, rgba(88, 166, 255, 0.2), transparent 58%),
              radial-gradient(65rem 38rem at 0% 0%, rgba(188, 140, 255, 0.16), transparent 56%),
              radial-gradient(55rem 32rem at 50% 120%, rgba(63, 185, 80, 0.1), transparent 62%),
              var(--surface);
            background-attachment: fixed;
            color: var(--text);
            line-height: 1.7;
            text-wrap: pretty;
            -webkit-text-size-adjust: 100%;
            min-block-size: 100svb;
            display: flex;
            flex-direction: column;
          }
          a { color: var(--accent); text-underline-offset: 0.2em; }
          a:hover:not(:where(.brand, .card a)) { text-decoration-thickness: 2px; }
          h1, h2, h3 { text-wrap: balance; line-height: 1.15; }
          :where(h1, h2, h3) { letter-spacing: -0.01em; }
          h1 { color: #f0f6fc; }
          h2 { color: var(--done); }
          h3 { color: var(--success); }
          h4 { color: var(--attention); }
          pre, code { font-family: Consolas, "Cascadia Code", monospace; }
          pre, code { background: var(--panel-strong); border-radius: 0.5rem; color: #f0f6fc; }
          code { padding-inline: 0.3em; padding-block: 0.1em; }
          pre { overflow-x: auto; padding: 1rem; border: 1px solid var(--border); }
          pre code { background: none; padding: 0; }
        }

        @layer layout {
          .site-header, main, .site-footer { inline-size: var(--measure); margin-inline: auto; }
          main { flex: 1; }
          .site-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            gap: 1rem;
            flex-wrap: wrap;
            padding-block: 1.25rem;
            position: sticky;
            inset-block-start: 0;
            z-index: 1;
            backdrop-filter: blur(0.5rem);
          }
          .site-nav-shell {
            display: grid;
            grid-template-columns: auto minmax(0, 1fr) auto;
            align-items: center;
            gap: 0.75rem;
            flex: 1 1 auto;
            min-inline-size: 0;
            margin-inline-start: auto;
            position: relative;
          }
          .site-nav {
            position: absolute;
            inset-block-start: calc(100% + 0.65rem);
            inset-inline-end: 0;
            display: none;
            flex-direction: column;
            gap: 0.35rem;
            min-inline-size: min(18rem, calc(100vw - 2rem));
            padding: 0.5rem;
            border: 1px solid var(--border);
            border-radius: 1rem;
            background: rgba(22, 27, 34, 0.96);
            box-shadow: var(--shadow);
          }
          .site-nav.site-nav-open { display: flex; }
          .site-footer { padding-block: 3rem 4rem; color: var(--muted); }
          section { margin-block: clamp(2rem, 5vw, 3.5rem); }
        }

        @layer components {
          .brand { font-weight: 700; font-size: 1.15rem; color: var(--text); text-decoration: none; }
          .brand::before { content: "__LITHOSHARP_BRAND_PREFIX__"; color: var(--accent); }
          .site-home-link,
          .site-menu-toggle { display: inline-flex; }
          .site-nav a {
            color: var(--muted);
            text-decoration: none;
            padding: 0.5rem 0.85rem;
            border-radius: 999px;
            min-block-size: 2.75rem;
            display: inline-flex;
            align-items: center;
            inline-size: 100%;
            justify-content: flex-start;
            transition: background-color 0.2s, color 0.2s;
          }
          .site-nav a:hover,
          .site-nav a:focus-visible { color: #f0f6fc; background: rgba(88, 166, 255, 0.14); }
          .site-nav-home { display: none; }
          .site-icon-button {
            inline-size: 2.75rem;
            block-size: 2.75rem;
            justify-content: center;
            align-items: center;
            padding: 0;
            color: #f0f6fc;
            border: 1px solid rgba(88, 166, 255, 0.34);
            background: rgba(88, 166, 255, 0.1);
            border-radius: 999px;
            text-decoration: none;
            display: inline-flex;
            appearance: none;
            cursor: pointer;
            transition: background-color 0.2s, color 0.2s, border-color 0.2s;
          }
          .site-icon-button:hover,
          .site-icon-button:focus-visible {
            color: #f0f6fc;
            background: rgba(88, 166, 255, 0.2);
            border-color: rgba(88, 166, 255, 0.5);
          }
          .site-icon-button svg {
            inline-size: 1.25rem;
            block-size: 1.25rem;
            fill: none;
            stroke: currentColor;
            stroke-width: 1.8;
            stroke-linecap: round;
            stroke-linejoin: round;
          }

          .visually-hidden {
            position: absolute;
            inline-size: 1px;
            block-size: 1px;
            overflow: hidden;
            clip: rect(0 0 0 0);
            white-space: nowrap;
            clip-path: inset(50%);
          }

          .surface, .hero, .card, .source, .post, .post-toc, .archive-panel {
            background: rgba(22, 27, 34, 0.88);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            padding: var(--space);
            box-shadow: var(--shadow);
          }

          .hero {
            padding: clamp(1.75rem, 4vw, 3rem);
            background:
              linear-gradient(135deg, rgba(88, 166, 255, 0.18), transparent 42%),
              linear-gradient(225deg, rgba(188, 140, 255, 0.16), transparent 52%),
              rgba(22, 27, 34, 0.92);
          }
          .hero h1 { font-size: clamp(2.25rem, 6vw, 4.25rem); line-height: 1.05; margin-block: 0.4rem 0.75rem; }
          .eyebrow { color: var(--success); font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase; font-size: 0.8rem; }

          .browser-search {
            display: grid;
            grid-template-columns: minmax(0, 1fr) auto;
            align-items: center;
            gap: 0.5rem;
            min-block-size: 3rem;
            padding: 0.35rem 0.4rem 0.35rem 0.8rem;
            border: 1px solid rgba(88, 166, 255, 0.38);
            border-radius: 999px;
            background:
              linear-gradient(180deg, rgba(33, 38, 45, 0.96), rgba(13, 17, 23, 0.92)),
              rgba(13, 17, 23, 0.92);
            box-shadow: inset 0 1px 0 rgba(240, 246, 252, 0.08), 0 1rem 2.5rem rgba(1, 4, 9, 0.32);
          }
          .browser-search input {
            inline-size: 100%;
            min-inline-size: 0;
            border: 0;
            color: #f0f6fc;
            background: transparent;
            outline: 0;
          }
          .browser-search input::placeholder { color: var(--muted); }
          .browser-search button {
            border: 0;
            padding: 0.2rem;
            color: var(--accent);
            background: transparent;
            text-decoration: none;
            line-height: 1;
            cursor: pointer;
            display: inline-flex;
            justify-content: center;
            align-items: center;
            transition: color 0.2s;
          }
          .browser-search:focus-within {
            border-color: var(--accent);
            box-shadow: 0 0 0 3px rgba(88, 166, 255, 0.25), inset 0 1px 0 rgba(240, 246, 252, 0.08);
          }
          .browser-search button:hover,
          .browser-search button:focus-visible {
            color: #f0f6fc;
          }
          .browser-search button svg {
            inline-size: 1.1rem;
            block-size: 1.1rem;
            fill: none;
            stroke: currentColor;
            stroke-width: 1.8;
            stroke-linecap: round;
            stroke-linejoin: round;
          }
          .site-header-search {
            display: grid;
            inline-size: 100%;
            min-inline-size: 0;
            margin: 0;
          }

          .post-list, .source-grid {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(min(100%, 17rem), 1fr));
            gap: 1rem;
            container-type: inline-size;
          }
          .featured-post-list {
            display: grid;
            grid-template-columns: minmax(0, 1fr);
          }
          .tag-cloud {
            display: flex;
            flex-wrap: wrap;
            gap: 0.75rem;
          }
          .tag-link {
            display: inline-flex;
            align-items: center;
            gap: 0.6rem;
            padding: 0.7rem 0.95rem;
            border: 1px solid var(--border);
            border-radius: 999px;
            background: rgba(22, 27, 34, 0.82);
            color: #f0f6fc;
            text-decoration: none;
            transition: border-color 0.2s ease, transform 0.2s ease, background-color 0.2s ease;
          }
          .tag-link span {
            color: var(--done);
            font-weight: 700;
          }
          .tag-link small { color: var(--muted); }
          .tag-link:hover,
          .tag-link:focus-visible {
            border-color: var(--accent);
            background: rgba(22, 27, 34, 0.96);
            transform: translateY(-1px);
          }
          .archive-post-list {
            display: grid;
            gap: 0.65rem;
          }

          .card {
            display: flex;
            flex-direction: column;
            gap: 0.4rem;
            transition: transform 0.2s ease, box-shadow 0.2s ease, border-color 0.2s ease;
          }
          .card:has(a:hover), .card:has(a:focus-visible) { transform: translateY(-3px); border-color: var(--accent); }
          .card h3 { margin: 0; font-size: 1.2rem; }
          .card a { color: #f0f6fc; text-decoration: none; }
          .card a::after { content: ""; position: absolute; inset: 0; }
          .card { position: relative; }
          .card time, .card p, .source small { color: var(--muted); }
          .card time { font-variant-numeric: tabular-nums; font-size: 0.85rem; }
          .featured-card {
            display: grid;
            grid-template-columns: minmax(16rem, 22rem) minmax(0, 1fr);
            grid-template-areas:
              "time summary"
              "title summary";
            gap: 0.75rem 1.75rem;
            align-items: start;
            padding: clamp(1.35rem, 3vw, 2rem);
            background:
              linear-gradient(135deg, rgba(88, 166, 255, 0.14), transparent 40%),
              linear-gradient(225deg, rgba(188, 140, 255, 0.12), transparent 58%),
              rgba(22, 27, 34, 0.9);
          }
          .featured-card time { grid-area: time; }
          .featured-card h3 { grid-area: title; font-size: clamp(1.5rem, 3.5vw, 2.3rem); }
          .featured-card p {
            grid-area: summary;
            margin: 0;
            font-size: 1rem;
            line-height: 1.75;
          }
          .archive-row {
            display: grid;
            grid-template-columns: minmax(12rem, 15rem) minmax(7rem, 10rem) minmax(0, 1fr);
            align-items: center;
            gap: 1rem;
            padding: 0.9rem 1rem;
            border: 1px solid var(--border);
            border-radius: 1rem;
            background: rgba(22, 27, 34, 0.72);
            transition: border-color 0.2s ease, background-color 0.2s ease, transform 0.2s ease;
          }
          .archive-row:hover,
          .archive-row:focus-within {
            border-color: rgba(88, 166, 255, 0.42);
            background: rgba(22, 27, 34, 0.88);
            transform: translateY(-1px);
          }
          .archive-row time,
          .archive-row p {
            color: var(--muted);
            margin: 0;
            font-size: 0.92rem;
          }
          .archive-row time {
            font-variant-numeric: tabular-nums;
            white-space: nowrap;
          }
          .archive-row h3 {
            margin: 0;
            min-inline-size: 0;
            font-size: 1rem;
          }
          .archive-row a {
            color: #f0f6fc;
            text-decoration: none;
          }
          .archive-row a:hover,
          .archive-row a:focus-visible {
            color: var(--accent);
          }
          .archive-row h3,
          .archive-row p {
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
          }

          .source { display: grid; gap: 0.25rem; color: var(--text); text-decoration: none; }
          .source span { color: var(--success); font-weight: 600; }

          .archive-panels {
            display: grid;
            gap: 1rem;
          }
          .archive-panel {
            overflow: clip;
          }
          .archive-panel summary {
            display: flex;
            justify-content: space-between;
            align-items: center;
            gap: 1rem;
            cursor: pointer;
            color: var(--done);
            font-weight: 700;
            list-style: none;
          }
          .archive-panel summary::-webkit-details-marker { display: none; }
          .archive-panel summary::before {
            content: "▶";
            color: var(--accent);
            transition: transform 0.2s ease;
          }
          .archive-panel[open] summary::before { transform: rotate(90deg); }
          .archive-panel summary span { flex: 1; }
          .archive-panel summary small { color: var(--muted); font-weight: 400; }
          .archive-panel .archive-post-list {
            margin-block-start: 1rem;
          }

          .post-layout {
            display: grid;
            grid-template-columns: minmax(0, 7fr) minmax(16rem, 3fr);
            gap: clamp(1rem, 3vw, 2rem);
            align-items: start;
            margin-block: 2rem;
          }
          .post { margin-block: 0; }
          .post-title { font-size: clamp(1.9rem, 4.5vw, 3.25rem); }
          .post .summary { color: var(--muted); font-size: 1.1rem; }
          .post :where(h2, h3) { scroll-margin-top: 6.5rem; }
          .post :where(h2, h3) { margin-block-start: 2rem; }
          .post h4 {
            margin-block: 1.35rem 0.35rem;
            font-size: 1rem;
            line-height: 1.45;
          }
          .post h4 a {
            color: var(--accent);
            text-decoration: none;
            border-block-end: 1px solid rgba(88, 166, 255, 0.45);
          }
          .post h4 a:hover,
          .post h4 a:focus-visible {
            color: #79c0ff;
            border-block-end-color: currentColor;
          }
          .post :where(p, ul, ol) { margin-block: 1rem; }
          .post a { word-break: break-word; }
          .post-toc {
            position: sticky;
            inset-block-start: 5.5rem;
            max-block-size: calc(100svh - 7rem);
            overflow: auto;
          }
          .post-toc h2 {
            margin: 0 0 1rem;
            color: #f0f6fc;
            font-size: 1.2rem;
          }
          .toc-nav {
            position: relative;
          }
          .toc-track,
          .toc-indicator {
            position: absolute;
            inset-inline-start: calc(0.75rem - 1px);
            inline-size: 2px;
            border-radius: 999px;
            pointer-events: none;
          }
          .toc-track {
            inset-block: 0.85rem 0.85rem;
            background: rgba(88, 166, 255, 0.2);
          }
          .toc-indicator {
            inset-block-start: 0.85rem;
            block-size: 2.2rem;
            background: linear-gradient(180deg, #58a6ff, #1f6feb);
            box-shadow: 0 0 0 1px rgba(88, 166, 255, 0.16), 0 0 16px rgba(31, 111, 235, 0.3);
            transition: transform 0.2s ease, block-size 0.2s ease, opacity 0.2s ease;
            opacity: 0;
          }
          .toc-list {
            display: grid;
            gap: 0.55rem;
            margin: 0;
            padding: 0 0 0 1.75rem;
            list-style: none;
          }
          .toc-list li {
            position: relative;
          }
          .toc-list li::before {
            content: "";
            position: absolute;
            inset-inline-start: -1.25rem;
            inset-block-start: 0.6rem;
            inline-size: 0.5rem;
            block-size: 0.5rem;
            border-radius: 50%;
            background: rgba(88, 166, 255, 0.8);
            box-shadow: 0 0 0 0.1rem rgba(31, 111, 235, 0.16);
            transition: transform 0.2s ease, background-color 0.2s ease, box-shadow 0.2s ease;
          }
          .post-toc a {
            display: block;
            color: var(--muted);
            text-decoration: none;
            padding-inline-start: 0.35rem;
            transition: color 0.2s ease, transform 0.2s ease;
          }
          .toc-depth-1 a {
            color: var(--accent);
            font-weight: 700;
          }
          .toc-depth-2 a {
            color: var(--text);
          }
          .toc-link-active {
            color: #f0f6fc !important;
            transform: translateX(0.15rem);
          }
          .toc-list li.toc-item-active::before {
            background: #58a6ff;
            box-shadow: 0 0 0 0.16rem rgba(31, 111, 235, 0.3), 0 0 16px rgba(88, 166, 255, 0.3);
            transform: scale(1.15);
          }
          .post-toc a:hover,
          .post-toc a:focus-visible {
            color: #f0f6fc;
            transform: translateX(0.15rem);
          }
          .toc-depth-2 { padding-inline-start: 0.9rem; }
          .toc-depth-3 { padding-inline-start: 1.8rem; }

          .tags { display: flex; gap: 0.5rem; flex-wrap: wrap; margin-block: 1rem; }
          .tags span {
            border: 1px solid var(--border);
            border-radius: 999px;
            padding: 0.25rem 0.7rem;
            color: var(--done);
            background: rgba(188, 140, 255, 0.1);
            font-size: 0.85rem;
          }

          .search-box { margin-block-start: 1.25rem; }
          #search-input {
            inline-size: 100%;
            font: inherit;
            font-size: 1.1rem;
            padding: 0.85rem 1.1rem;
            min-block-size: 3rem;
            color: var(--text);
            background: rgba(22, 27, 34, 0.9);
            border: 1px solid var(--border);
            border-radius: 999px;
            transition: border-color 0.2s, box-shadow 0.2s;
          }
          #search-input:focus-visible {
            outline: none;
            border-color: var(--accent);
            box-shadow: 0 0 0 3px rgba(88, 166, 255, 0.3);
          }
          .search-scope {
            color: var(--done);
            font-size: 0.92rem;
            margin-block: 1rem 0 0.35rem;
          }
          .search-status { color: var(--muted); font-size: 0.9rem; margin-block: 1rem; }
          #search-app { margin-block-start: 1.5rem; }
          .release-note-list {
            display: grid;
            gap: 1.25rem;
          }
          .release-note {
            display: grid;
            gap: 1rem;
          }
          .release-note-header {
            display: grid;
            gap: 0.45rem;
          }
          .release-note-meta {
            color: var(--muted);
            font-size: 0.9rem;
          }
          .card mark {
            background: rgba(210, 153, 34, 0.35);
            color: #f0f6fc;
            border-radius: 0.2rem;
            padding-inline: 0.1em;
          }
          .search-empty { color: var(--muted); }
        }

        @media (min-width: 34rem) {
          .hero { padding-inline: clamp(2rem, 5vw, 3.5rem); }
        }

        @media (max-width: 40rem) {
          .site-header {
            padding-block: 0.9rem;
          }
          .site-header .brand {
            display: none;
          }
          .site-nav-shell {
            inline-size: 100%;
            gap: 0.65rem;
          }
          .site-home-link,
          .site-menu-toggle,
          .site-header-search {
            display: inline-flex;
          }
          .site-header-search {
            display: grid;
            grid-template-columns: minmax(0, 1fr) auto;
            min-block-size: 2.75rem;
            padding: 0.25rem 0.35rem 0.25rem 0.8rem;
            gap: 0.35rem;
          }
          .site-header-search input {
            font-size: 0.95rem;
          }
          .site-header-search button {
            padding: 0;
          }
          .site-menu-toggle {
            justify-self: end;
          }
          .site-nav {
            inline-size: 100%;
            grid-column: 1 / -1;
            min-inline-size: 0;
            margin-block-start: 0.65rem;
            position: static;
            inset-block-start: auto;
            inset-inline-end: auto;
          }
          .site-nav.site-nav-open {
            display: flex;
          }
          .site-nav a { font-size: 0.95rem; }
        }

        @media (max-width: 54rem) {
          .archive-row {
            grid-template-columns: 1fr;
            gap: 0.35rem;
            align-items: start;
          }
          .archive-row h3,
          .archive-row p {
            white-space: normal;
          }
          .featured-card {
            grid-template-columns: 1fr;
            grid-template-areas:
              "time"
              "title"
              "summary";
          }
          .post-layout {
            grid-template-columns: 1fr;
          }
          .post-toc {
            position: static;
            max-block-size: none;
            order: -1;
          }
        }

        @media (prefers-reduced-motion: reduce) {
          @view-transition { navigation: none; }
          *, *::before, *::after { animation-duration: 0.01ms !important; transition-duration: 0.01ms !important; }
        }
        """;

    private static string BuildSearchScript(SiteText text)
    {
        var messages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["browseAll"] = text.SearchStatusBrowseAll,
            ["hits"] = text.SearchStatusHits,
            ["hitsInTag"] = text.SearchStatusHitsInTag,
            ["showingTopSuffix"] = text.SearchStatusShowingTopSuffix,
            ["taggedPosts"] = text.SearchStatusTaggedPosts,
            ["noMatch"] = text.SearchStatusNoMatch,
            ["noMatchInTag"] = text.SearchStatusNoMatchInTag,
            ["noPostsInTag"] = text.SearchStatusNoPostsInTag,
            ["noPosts"] = text.SearchStatusNoPosts,
            ["scopeTag"] = text.SearchScopeTag,
            ["loadError"] = text.SearchStatusLoadError,
        };
        return SearchScriptTemplate.Replace("__LITHOSHARP_SEARCH_MESSAGES__", SerializeSearchMessages(messages), StringComparison.Ordinal);
    }

    private static string SerializeSearchMessages(Dictionary<string, string> messages)
    {
        var builder = new StringBuilder();
        builder.Append('{');
        var first = true;
        foreach (var (key, value) in messages)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append('"').Append(key).Append("\":").Append(JsonSerializer.Serialize(value, SearchMessageSerializerOptions));
        }

        builder.Append('}');
        return builder.ToString();
    }

    private const string SearchScriptTemplate = """
        (() => {
          "use strict";
          const app = document.getElementById("search-app");
          const input = document.getElementById("search-input");
          const results = document.getElementById("search-results");
          const status = document.getElementById("search-status");
          const scope = document.getElementById("search-scope");
          if (!app || !input || !results || !status) {
            return;
          }

          const indexUrl = app.dataset.index;
          let docs = null;
          const RESULT_LIMIT = 30;
          const RECENT_LIMIT = 20;
          const pageParams = new URLSearchParams(window.location.search);
          const selectedTag = (pageParams.get("tag") || "").trim();
          const MESSAGES = __LITHOSHARP_SEARCH_MESSAGES__;
          const fmt = (template, values) =>
            Object.keys(values).reduce((acc, key) => acc.split("{" + key + "}").join(String(values[key])), template);

          const escapeHtml = (value) =>
            value.replace(/[&<>"']/g, (ch) => ({
              "&": "&amp;",
              "<": "&lt;",
              ">": "&gt;",
              '"': "&quot;",
              "'": "&#39;",
            })[ch]);

          const normalizeText = (value) =>
            String(value || "")
              .toLowerCase()
              .normalize("NFKC")
              .replace(/[!"#$%&'()*+,./:;<=>?@[\\\]^_`{|}~-]+/g, " ")
              .replace(/\s+/g, " ")
              .trim();

          const normalizedSelectedTag = normalizeText(selectedTag);

          // Prefer substring matches; fall back to fuzzy subsequence scoring.
          const scoreField = (needle, hay) => {
            const idx = hay.indexOf(needle);
            if (idx !== -1) {
              let score = 1000 - Math.min(idx, 500);
              if (idx === 0 || /\s/.test(hay[idx - 1])) {
                score += 250;
              }
              return score;
            }
            let cursor = 0;
            let streak = 0;
            let score = 0;
            for (let i = 0; i < needle.length; i += 1) {
              const ch = needle[i];
              let found = -1;
              for (; cursor < hay.length; cursor += 1) {
                if (hay[cursor] === ch) {
                  found = cursor;
                  break;
                }
              }
              if (found === -1) {
                return -1;
              }
              streak = found > 0 && /\s/.test(hay[found - 1]) ? streak + 3 : streak + 1;
              score += streak;
              cursor = found + 1;
            }
            return score;
          };

          const scoreDoc = (tokens, doc) => {
            let total = 0;
            for (const token of tokens) {
              const hayScore = scoreField(token, doc.hay);
              if (hayScore < 0) {
                return -1;
              }
              const titleScore = scoreField(token, doc.titleLc);
              total += hayScore + (titleScore > 0 ? titleScore * 2 : 0);
            }
            return total;
          };

          const highlight = (text, tokens) => {
            if (!tokens.length) {
              return escapeHtml(text);
            }
            const lower = text.toLowerCase();
            const ranges = [];
            for (const token of tokens) {
              let from = 0;
              let at = lower.indexOf(token, from);
              while (at !== -1) {
                ranges.push([at, at + token.length]);
                from = at + token.length;
                at = lower.indexOf(token, from);
              }
            }
            if (!ranges.length) {
              return escapeHtml(text);
            }
            ranges.sort((a, b) => a[0] - b[0]);
            const merged = [];
            for (const range of ranges) {
              const last = merged[merged.length - 1];
              if (last && range[0] <= last[1]) {
                last[1] = Math.max(last[1], range[1]);
              } else {
                merged.push(range.slice());
              }
            }
            let html = "";
            let pos = 0;
            for (const [start, end] of merged) {
              html += escapeHtml(text.slice(pos, start));
              html += "<mark>" + escapeHtml(text.slice(start, end)) + "</mark>";
              pos = end;
            }
            html += escapeHtml(text.slice(pos));
            return html;
          };

          const safeUrl = (url) => {
            const value = String(url || "");
            if (/^(https?:\/\/|\/|\.\/|\.\.\/|#)/.test(value)) {
              return value;
            }
            return "#";
          };

          const matchTag = (doc) => !normalizedSelectedTag
            || (doc.tagsNormalized || []).includes(normalizedSelectedTag);

          const render = (entries, tokens, query, total) => {
            if (!entries.length) {
              results.innerHTML = "";
              if (query && selectedTag) {
                status.textContent = fmt(MESSAGES.noMatchInTag, { tag: selectedTag, query: query });
              } else if (query) {
                status.textContent = fmt(MESSAGES.noMatch, { query: query });
              } else if (selectedTag) {
                status.textContent = fmt(MESSAGES.noPostsInTag, { tag: selectedTag });
              } else {
                status.textContent = MESSAGES.noPosts;
              }
              return;
            }

            const matched = total ?? entries.length;
            const topSuffix = matched > entries.length ? fmt(MESSAGES.showingTopSuffix, { shown: entries.length }) : "";
            if (query && selectedTag) {
              status.textContent = fmt(MESSAGES.hitsInTag, { count: matched, tag: selectedTag }) + topSuffix;
            } else if (query) {
              status.textContent = fmt(MESSAGES.hits, { count: matched }) + topSuffix;
            } else if (selectedTag) {
              status.textContent = fmt(MESSAGES.taggedPosts, { count: matched, tag: selectedTag });
            } else {
              status.textContent = fmt(MESSAGES.browseAll, { count: docs.length });
            }

            const rows = entries.map(({ doc }) => (
              '<article class="archive-row">' +
              "<time>" + escapeHtml(doc.date) + "</time>" +
              '<h3><a href="' + escapeHtml(safeUrl(doc.url)) + '">' + highlight(doc.title, tokens) + "</a></h3>" +
              "<p>" + highlight(doc.summary, tokens) + "</p>" +
              "</article>"
            ));
            results.innerHTML = rows.join("");
          };

          const search = (raw) => {
            if (!docs) {
              return;
            }
            const query = raw.trim();
            const normalized = normalizeText(query);
            const scopedDocs = docs.filter((doc) => matchTag(doc));
            if (!normalized) {
              render(scopedDocs.slice(0, RECENT_LIMIT).map((doc) => ({ doc, score: 0 })), [], "", scopedDocs.length);
              return;
            }
            const tokens = normalized.split(/\s+/).filter(Boolean);
            const matches = [];
            for (const doc of scopedDocs) {
              const score = scoreDoc(tokens, doc);
              if (score >= 0) {
                matches.push({ doc, score });
              }
            }
            matches.sort((a, b) => b.score - a.score || a.doc.title.localeCompare(b.doc.title));
            render(matches.slice(0, RESULT_LIMIT), tokens, query, matches.length);
          };

          const prepare = (data) => {
            docs = (data.documents || []).map((doc) => ({
              title: doc.title || "",
              summary: doc.summary || "",
              tags: doc.tags || [],
              tagsNormalized: (doc.tags || []).map((tag) => normalizeText(tag)),
              url: doc.url || "#",
              date: doc.date || "",
              titleLc: normalizeText(doc.title),
              hay: normalizeText([doc.title, doc.summary, (doc.tags || []).join(" "), doc.body]
                .join(" \n ")),
            }));
            if (scope && selectedTag) {
              scope.hidden = false;
              scope.textContent = fmt(MESSAGES.scopeTag, { tag: selectedTag });
            }
            search(input.value);
          };

          let timer = 0;
          input.addEventListener("input", () => {
            window.clearTimeout(timer);
            timer = window.setTimeout(() => {
              search(input.value);
              const params = new URLSearchParams(window.location.search);
              const value = input.value.trim();
              if (value) {
                params.set("q", value);
              } else {
                params.delete("q");
              }
              const next = params.toString();
              window.history.replaceState(null, "", next ? "?" + next : window.location.pathname);
            }, 90);
          });

          input.form?.addEventListener("submit", (event) => {
            event.preventDefault();
            search(input.value);
          });

          const initial = new URLSearchParams(window.location.search).get("q");
          if (initial) {
            input.value = initial;
          }

          const load = () => {
            fetch(indexUrl, { cache: "no-cache" })
              .then((response) => {
                if (!response.ok) {
                  throw new Error("failed to load search index");
                }
                return response.json();
              })
              .then(prepare)
              .catch(() => {
                status.textContent = MESSAGES.loadError;
              });
          };

          if ("requestIdleCallback" in window) {
            window.requestIdleCallback(load, { timeout: 1000 });
          } else {
            window.setTimeout(load, 0);
          }
        })();
        """;

    private static string BuildSiteScript() => """
        (() => {
          "use strict";

          const menuToggle = document.querySelector("[data-site-menu-toggle]");
          const siteNav = document.querySelector("[data-site-nav]");

          if (menuToggle && siteNav) {
            const setMenuOpen = (isOpen) => {
              menuToggle.setAttribute("aria-expanded", String(isOpen));
              siteNav.classList.toggle("site-nav-open", isOpen);
            };

            const closeMenu = () => {
              setMenuOpen(false);
            };

            menuToggle.addEventListener("click", () => {
              const isOpen = menuToggle.getAttribute("aria-expanded") === "true";
              setMenuOpen(!isOpen);
            });

            for (const link of siteNav.querySelectorAll("a")) {
              link.addEventListener("click", () => {
                closeMenu();
              });
            }

            document.addEventListener("click", (event) => {
              const target = event.target;
              if (!(target instanceof Node)) {
                return;
              }
              if (!siteNav.contains(target) && !menuToggle.contains(target)) {
                closeMenu();
              }
            });

            document.addEventListener("keydown", (event) => {
              if (event.key === "Escape" && menuToggle.getAttribute("aria-expanded") === "true") {
                closeMenu();
                menuToggle.focus();
              }
            });

            window.addEventListener("resize", closeMenu);

            closeMenu();
          }

          const toc = document.querySelector(".post-toc");
          if (!toc) {
            return;
          }

          const nav = toc.querySelector(".toc-nav");
          const links = Array.from(toc.querySelectorAll("[data-toc-link]"));
          const indicator = toc.querySelector(".toc-indicator");
          if (!nav || !indicator || links.length === 0) {
            return;
          }

          const entries = links
            .map((link) => {
              const href = link.getAttribute("href") || "";
              const id = href.startsWith("#") ? href.slice(1) : "";
              const heading = id ? document.getElementById(id) : null;
              const item = link.closest("[data-toc-item]");
              return heading && item ? { link, heading, item } : null;
            })
            .filter(Boolean);

          if (entries.length === 0) {
            return;
          }

          let active = entries[0];

          const updateIndicator = (entry) => {
            active = entry;
            for (const candidate of entries) {
              const isActive = candidate === entry;
              candidate.link.classList.toggle("toc-link-active", isActive);
              candidate.item.classList.toggle("toc-item-active", isActive);
              if (isActive) {
                candidate.link.setAttribute("aria-current", "location");
              }
              else {
                candidate.link.removeAttribute("aria-current");
              }
            }

            const navRect = nav.getBoundingClientRect();
            const itemRect = entry.item.getBoundingClientRect();
            indicator.style.opacity = "1";
            indicator.style.transform = `translateY(${Math.max(0, itemRect.top - navRect.top + 8)}px)`;
            indicator.style.height = `${Math.max(24, itemRect.height - 4)}px`;
          };

          const pickActive = () => {
            const threshold = window.innerHeight * 0.24;
            let current = entries[0];
            for (const entry of entries) {
              const rect = entry.heading.getBoundingClientRect();
              if (rect.top <= threshold) {
                current = entry;
              }
              else {
                break;
              }
            }

            updateIndicator(current);
          };

          let rafId = 0;
          const requestUpdate = () => {
            if (rafId !== 0) {
              return;
            }

            rafId = window.requestAnimationFrame(() => {
              rafId = 0;
              pickActive();
            });
          };

          window.addEventListener("scroll", requestUpdate, { passive: true });
          window.addEventListener("resize", requestUpdate);
          requestUpdate();
        })();
        """;

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
