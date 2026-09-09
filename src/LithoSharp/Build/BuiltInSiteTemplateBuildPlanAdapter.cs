using System.Globalization;
using System.Text.Json;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Routing;

namespace LithoSharp.Build;

internal static class BuiltInSiteTemplateBuildPlanAdapter
{
    private static readonly BuildNodeId DocsNavigationNodeId = new("navigation:docs");
    private static readonly BuildNodeId BlogNavigationNodeId = new("navigation:blog");

    internal static SiteBuildPlan Create(
        ISiteTemplate template,
        SiteSettings site,
        SiteText text,
        SiteThemeOptions theme,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<SiteExtraPage> extraPages,
        IReadOnlyList<IntegratedContentPage> contentPages,
        SiteRouteCatalog routes,
        IReadOnlyList<SiteTemplateFile> templateFiles,
        DateTimeOffset buildTimestamp,
        string faviconSourceDirectory,
        bool hasFaviconAssets,
        bool hasSocialImage,
        bool generateLlmsTxt)
    {
        var isDocs = template is DocsSiteTemplate;
        if (!isDocs && template is not BlogSiteTemplate)
        {
            throw new ArgumentException("The template must be a built-in template.", nameof(template));
        }

        var filesByPath = templateFiles.ToDictionary(
            file => file.RelativePath,
            StringComparer.Ordinal);
        var nodes = new List<BuildNode>();
        var templateIdentity = isDocs ? "docs" : "blog";
        var navigationNodeId = isDocs ? DocsNavigationNodeId : BlogNavigationNodeId;
        nodes.Add(new BuildNode(
            navigationNodeId,
            [BuildInput.FromCollection(
                isDocs ? "pages.docs.navigation" : "pages.blog.navigation",
                isDocs
                    ? DocsNavigationValue(posts, extraPages, contentPages, routes)
                    : BlogNavigationValue(extraPages, contentPages, routes, text))]));

        AddTextArtifactNode(
            nodes,
            filesByPath,
            SiteCssNodeId,
            routes.SiteCss,
            [
                Configuration("template.builtIn", templateIdentity),
                Configuration("theme.brandPrefix", theme.BrandPrefix),
                Configuration("theme.additionalCss", theme.AdditionalCss),
                Configuration("theme.enableThemeSwitching", Json(theme.EnableThemeSwitching)),
            ]);
        AddTextArtifactNode(
            nodes,
            filesByPath,
            SiteScriptNodeId,
            routes.SiteScript,
            [Configuration("template.builtIn", templateIdentity),
                Configuration("theme.enableThemeSwitching", Json(theme.EnableThemeSwitching))]);

        if (isDocs)
        {
            AddDocsNodes(
                nodes,
                filesByPath,
                site,
                text,
                theme,
                posts,
                extraPages,
                contentPages,
                routes,
                hasFaviconAssets,
                navigationNodeId);
        }
        else
        {
            AddBlogNodes(
                nodes,
                filesByPath,
                site,
                text,
                theme,
                posts,
                extraPages,
                contentPages,
                routes,
                buildTimestamp,
                hasFaviconAssets,
                navigationNodeId);
        }

        if (template is DocsSiteTemplate { EnableSearch: true })
        {
            foreach (var (id, route) in new[] { (SearchIndexNodeId, routes.SearchIndex), (SearchScriptNodeId, routes.SearchScript), (SearchPageNodeId, routes.SearchPage), (SitemapNodeId, routes.Sitemap) })
                AddTextArtifactNode(nodes, filesByPath, id, route,
                    [Configuration("site", Json(site)), Configuration("text", Json(text)), Configuration("timestamp", Format(buildTimestamp)),
                        BuildInput.FromCollection("docs.search.pages", ContentCollectionBuildPlanAdapter.SurfaceValue(contentPages, "search", true, GeneratedPageDerivedSurfaces.Search)),
                        BuildInput.FromCollection("docs.search.markdown", SearchCollectionValue(posts, routes, site))],
                    id == SearchPageNodeId ? [SearchIndexNodeId] : []);
        }
        if (filesByPath.Count != 0)
        {
            throw new InvalidOperationException(
                $"Built-in template returned undeclared artifacts: {string.Join(", ", filesByPath.Keys.Order(StringComparer.Ordinal))}.");
        }

        AddCommonNodes(
            nodes,
            site,
            text,
            theme,
            posts,
            contentPages,
            routes,
            faviconSourceDirectory,
            hasFaviconAssets,
            hasSocialImage,
            generateLlmsTxt);
        return SiteBuildPlan.Create(nodes);
    }

    internal static IReadOnlyDictionary<string, BuildNodeId> CreateTemplateOwnerMap(
        ISiteTemplate template,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<SiteExtraPage> extraPages,
        SiteRouteCatalog routes)
    {
        var owners = new Dictionary<string, BuildNodeId>(StringComparer.Ordinal)
        {
            [routes.SiteCss.RelativeOutputPath] = SiteCssNodeId,
            [routes.SiteScript.RelativeOutputPath] = SiteScriptNodeId,
            [routes.Home.RelativeOutputPath] = HomeNodeId,
        };

        if (template is BlogSiteTemplate)
        {
            owners[routes.SearchScript.RelativeOutputPath] = SearchScriptNodeId;
            owners[routes.SearchIndex.RelativeOutputPath] = SearchIndexNodeId;
            owners[routes.Archives.RelativeOutputPath] = ArchivesNodeId;
            owners[routes.Tags.RelativeOutputPath] = TagsNodeId;
            owners[routes.SearchPage.RelativeOutputPath] = SearchPageNodeId;
            owners[routes.Feed.RelativeOutputPath] = FeedNodeId;
            owners[routes.Sitemap.RelativeOutputPath] = SitemapNodeId;
        }
        else if (template is DocsSiteTemplate { EnableSearch: true })
        {
            owners[routes.SearchScript.RelativeOutputPath] = SearchScriptNodeId;
            owners[routes.SearchIndex.RelativeOutputPath] = SearchIndexNodeId;
            owners[routes.SearchPage.RelativeOutputPath] = SearchPageNodeId;
            owners[routes.Sitemap.RelativeOutputPath] = SitemapNodeId;
        }
        else if (template is not DocsSiteTemplate)
        {
            throw new ArgumentException("The template must be a built-in template.", nameof(template));
        }

        foreach (var post in posts)
        {
            var route = routes.Post(post);
            owners[route.RelativeOutputPath] = MarkdownPageNodeId(route);
        }

        foreach (var page in extraPages)
        {
            var route = routes.ExtraPage(page);
            owners[route.RelativeOutputPath] = ExtraPageNodeId(route);
        }

        return owners;
    }

    internal static IReadOnlyDictionary<string, BuildNodeId> CreateCommonOwnerMap(
        IReadOnlyList<MarkdownPost> posts,
        SiteRouteCatalog routes)
    {
        var owners = new Dictionary<string, BuildNodeId>(StringComparer.Ordinal)
        {
            [routes.WebManifest.RelativeOutputPath] = WebManifestNodeId,
            [routes.DefaultSocialImage.RelativeOutputPath] = DefaultSocialImageNodeId,
            [routes.Llms.RelativeOutputPath] = LlmsNodeId,
        };

        foreach (var fileName in SiteGenerator.BundledFaviconAssetNames)
        {
            owners[routes.Favicon(fileName).RelativeOutputPath] = FaviconNodeId(fileName);
        }

        foreach (var post in posts)
        {
            owners[routes.PostSocialImage(post).RelativeOutputPath] =
                PostSocialImageNodeId(routes.Post(post));
        }

        return owners;
    }

    private static BuildNodeId SiteCssNodeId { get; } = new("asset:site-css");
    private static BuildNodeId SiteScriptNodeId { get; } = new("asset:site-script");
    private static BuildNodeId SearchScriptNodeId { get; } = new("asset:search-script");
    private static BuildNodeId SearchIndexNodeId { get; } = new("index:search");
    private static BuildNodeId HomeNodeId { get; } = new("page:index");
    private static BuildNodeId ArchivesNodeId { get; } = new("page:archives");
    private static BuildNodeId TagsNodeId { get; } = new("page:tags");
    private static BuildNodeId SearchPageNodeId { get; } = new("page:search");
    private static BuildNodeId FeedNodeId { get; } = new("feed:rss");
    private static BuildNodeId SitemapNodeId { get; } = new("index:sitemap");
    private static BuildNodeId WebManifestNodeId { get; } = new("asset:web-manifest");
    private static BuildNodeId DefaultSocialImageNodeId { get; } = new("social:default");
    private static BuildNodeId LlmsNodeId { get; } = new("text:llms");

    private static void AddDocsNodes(
        ICollection<BuildNode> nodes,
        IDictionary<string, SiteTemplateFile> filesByPath,
        SiteSettings site,
        SiteText text,
        SiteThemeOptions theme,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<SiteExtraPage> extraPages,
        IReadOnlyList<IntegratedContentPage> contentPages,
        SiteRouteCatalog routes,
        bool hasFaviconAssets,
        BuildNodeId navigationNodeId)
    {
        AddTextArtifactNode(
            nodes,
            filesByPath,
            HomeNodeId,
            routes.Home,
            [
                .. HtmlLayoutInputs(site, theme, hasFaviconAssets),
                BuildInput.FromCollection("pages.docs.index", Json(posts.Select(post => new
                {
                    Route = routes.Post(post).RelativeOutputPath,
                }))),
            ],
            [navigationNodeId]);

        foreach (var post in posts)
        {
            AddTextArtifactNode(
                nodes,
                filesByPath,
                MarkdownPageNodeId(routes.Post(post)),
                routes.Post(post),
                [
                    .. HtmlLayoutInputs(site, theme, hasFaviconAssets),
                    Value("page.source", post.FilePath),
                    Value("page.route", routes.Post(post).RelativeOutputPath),
                    Value("page.title", post.FrontMatter.Title),
                    Value("page.summary", post.FrontMatter.Summary),
                    Value("page.date", Format(post.FrontMatter.Date)),
                    Value("page.body", post.MarkdownBody),
                    Configuration(
                        "text.tableOfContents",
                        Json(new
                        {
                            text.TableOfContentsHeading,
                            text.TableOfContentsEmpty,
                        })),
                ],
                [navigationNodeId]);
        }

        foreach (var page in extraPages)
        {
            AddTextArtifactNode(
                nodes,
                filesByPath,
                ExtraPageNodeId(routes.ExtraPage(page)),
                routes.ExtraPage(page),
                [
                    .. HtmlLayoutInputs(site, theme, hasFaviconAssets),
                    Value("page.route", routes.ExtraPage(page).RelativeOutputPath),
                    Value("page.title", page.Title),
                    Value("page.body", page.BodyHtml),
                ],
                [navigationNodeId]);
        }
    }

    private static void AddBlogNodes(
        ICollection<BuildNode> nodes,
        IDictionary<string, SiteTemplateFile> filesByPath,
        SiteSettings site,
        SiteText text,
        SiteThemeOptions theme,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<SiteExtraPage> extraPages,
        IReadOnlyList<IntegratedContentPage> contentPages,
        SiteRouteCatalog routes,
        DateTimeOffset buildTimestamp,
        bool hasFaviconAssets,
        BuildNodeId navigationNodeId)
    {
        AddTextArtifactNode(
            nodes,
            filesByPath,
            SearchScriptNodeId,
            routes.SearchScript,
            [
                Configuration(
                    "text.searchScript",
                    Json(new
                    {
                        text.SearchStatusBrowseAll,
                        text.SearchStatusHits,
                        text.SearchStatusHitsInTag,
                        text.SearchStatusShowingTopSuffix,
                        text.SearchStatusTaggedPosts,
                        text.SearchStatusNoMatch,
                        text.SearchStatusNoMatchInTag,
                        text.SearchStatusNoPostsInTag,
                        text.SearchStatusNoPosts,
                        text.SearchScopeTag,
                        text.SearchStatusLoadError,
                    })),
            ]);
        AddTextArtifactNode(
            nodes,
            filesByPath,
            SearchIndexNodeId,
            routes.SearchIndex,
            [
                Configuration("site.title", site.Title),
                Configuration("site.timeZone", site.TimeZone),
                Configuration("build.timestamp", Format(buildTimestamp)),
                BuildInput.FromCollection("pages.blog.search", SearchCollectionValue(posts, routes, site)),
                BuildInput.FromCollection(
                    "pages.content.search",
                    ContentCollectionBuildPlanAdapter.SurfaceValue(
                        contentPages,
                        "search",
                        includeContent: true,
                        GeneratedPageDerivedSurfaces.Search)),
            ]);
        AddTextArtifactNode(
            nodes,
            filesByPath,
            HomeNodeId,
            routes.Home,
            [
                .. HtmlLayoutInputs(site, theme, hasFaviconAssets),
                Configuration("site.timeZone", site.TimeZone),
                Configuration(
                    "text.index",
                    Json(new
                    {
                        text.IndexLede,
                        text.IndexEmpty,
                        text.OlderPostsHeading,
                        text.OlderPostsEmpty,
                    })),
                BuildInput.FromCollection("pages.blog.index", ListingCollectionValue(posts, routes, site)),
            ],
            [navigationNodeId]);
        AddTextArtifactNode(
            nodes,
            filesByPath,
            ArchivesNodeId,
            routes.Archives,
            [
                .. HtmlLayoutInputs(site, theme, hasFaviconAssets),
                Configuration("site.timeZone", site.TimeZone),
                Configuration("text.archives", text.ArchivesEmpty),
                BuildInput.FromCollection("pages.blog.archives", ListingCollectionValue(posts, routes, site)),
            ],
            [navigationNodeId]);
        AddTextArtifactNode(
            nodes,
            filesByPath,
            TagsNodeId,
            routes.Tags,
            [
                .. HtmlLayoutInputs(site, theme, hasFaviconAssets),
                Configuration(
                    "text.tags",
                    Json(new
                    {
                        text.TagsHeading,
                        text.TagsIntro,
                        text.ExistingTagsHeading,
                        text.TagsEmpty,
                    })),
                BuildInput.FromCollection("pages.blog.tags", Json(posts.Select(post => new
                {
                    Route = routes.Post(post).RelativeOutputPath,
                    post.FrontMatter.Tags,
                }))),
            ],
            [navigationNodeId]);
        AddTextArtifactNode(
            nodes,
            filesByPath,
            SearchPageNodeId,
            routes.SearchPage,
            [
                .. HtmlLayoutInputs(site, theme, hasFaviconAssets),
                Configuration(
                    "text.searchPage",
                    Json(new
                    {
                        text.SearchHeading,
                        text.SearchIntro,
                        text.SearchInputPlaceholder,
                        text.SearchInputLabel,
                        text.SearchLoading,
                        text.SearchNoscriptPrefix,
                        text.SearchNoscriptArchivesLinkText,
                        text.SearchNoscriptSuffix,
                    })),
            ],
            [navigationNodeId, SearchIndexNodeId]);

        foreach (var post in posts)
        {
            AddTextArtifactNode(
                nodes,
                filesByPath,
                MarkdownPageNodeId(routes.Post(post)),
                routes.Post(post),
                [
                    .. HtmlLayoutInputs(site, theme, hasFaviconAssets),
                    Configuration("site.timeZone", site.TimeZone),
                    Configuration(
                        "text.tableOfContents",
                        Json(new
                        {
                            text.TableOfContentsHeading,
                            text.TableOfContentsEmpty,
                        })),
                    Value("page.route", routes.Post(post).RelativeOutputPath),
                    Value("page.title", post.FrontMatter.Title),
                    Value("page.summary", post.FrontMatter.Summary),
                    Value("page.date", Format(post.FrontMatter.Date)),
                    Value("page.tags", Json(post.FrontMatter.Tags)),
                    Value("page.body", post.MarkdownBody),
                ],
                [navigationNodeId]);
        }

        foreach (var page in extraPages)
        {
            AddTextArtifactNode(
                nodes,
                filesByPath,
                ExtraPageNodeId(routes.ExtraPage(page)),
                routes.ExtraPage(page),
                [
                    .. HtmlLayoutInputs(site, theme, hasFaviconAssets),
                    Value("page.route", routes.ExtraPage(page).RelativeOutputPath),
                    Value("page.title", page.Title),
                    Value("page.body", page.BodyHtml),
                ],
                [navigationNodeId]);
        }

        var feedContentPages = contentPages
            .Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.Rss))
            .Take(Math.Max(0, 20 - posts.Count))
            .ToArray();
        var feedInputs = new List<BuildInput>
        {
            Configuration("site.feed", Json(new { site.Title, site.Description, site.BaseUrl })),
            BuildInput.FromCollection("pages.blog.feed", Json(posts.Take(20).Select(post => new
            {
                Route = routes.Post(post).RelativeOutputPath,
                post.FrontMatter.Title,
                post.FrontMatter.Summary,
                Date = post.FrontMatter.Date.UtcDateTime.ToString(
                    "R",
                    CultureInfo.InvariantCulture),
            }))),
            BuildInput.FromCollection(
                "pages.content.feed",
                ContentCollectionBuildPlanAdapter.SurfaceValue(
                    feedContentPages,
                    "feed",
                    includeContent: false)),
        };
        if (feedContentPages.Any(page => page.Metadata.PublishFrom is null))
        {
            feedInputs.Add(Configuration(
                "build.timestamp",
                Format(buildTimestamp.ToUniversalTime())));
        }

        AddTextArtifactNode(
            nodes,
            filesByPath,
            FeedNodeId,
            routes.Feed,
            feedInputs);
        AddTextArtifactNode(
            nodes,
            filesByPath,
            SitemapNodeId,
            routes.Sitemap,
            [
                Configuration("site.baseUrl", site.BaseUrl),
                BuildInput.FromCollection("pages.blog.sitemap", Json(new
                {
                    ExtraPages = extraPages.Select(page => new
                    {
                        Route = routes.ExtraPage(page).RelativeOutputPath,
                        page.IncludeInSitemap,
                    }),
                    Posts = posts.Select(post => new
                    {
                        Route = routes.Post(post).RelativeOutputPath,
                        Date = post.FrontMatter.Date.ToString(
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture),
                    }),
                })),
                BuildInput.FromCollection(
                    "pages.content.sitemap",
                    ContentCollectionBuildPlanAdapter.SurfaceValue(
                        contentPages,
                        "sitemap",
                        includeContent: false,
                        GeneratedPageDerivedSurfaces.Sitemap)),
            ]);
    }

    private static void AddCommonNodes(
        ICollection<BuildNode> nodes,
        SiteSettings site,
        SiteText text,
        SiteThemeOptions theme,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<IntegratedContentPage> contentPages,
        SiteRouteCatalog routes,
        string faviconSourceDirectory,
        bool hasFaviconAssets,
        bool hasSocialImage,
        bool generateLlmsTxt)
    {
        if (hasFaviconAssets)
        {
            foreach (var fileName in SiteGenerator.BundledFaviconAssetNames)
            {
                var sourcePath = Path.Combine(faviconSourceDirectory, fileName);
                var nodeId = FaviconNodeId(fileName);
                nodes.Add(new BuildNode(
                    nodeId,
                    [BuildInput.FromFile($"favicon/{fileName}", BuildInputFingerprint.FromFile(sourcePath))],
                    artifacts: [Artifact(nodeId, routes.Favicon(fileName))]));
            }

            nodes.Add(new BuildNode(
                WebManifestNodeId,
                [
                    Configuration(
                        "manifest",
                        Json(new
                        {
                            site.Title,
                            site.Description,
                            site.BaseUrl,
                            theme.ThemeColor,
                        })),
                ],
                artifacts: [Artifact(WebManifestNodeId, routes.WebManifest)]));
        }

        if (hasSocialImage)
        {
            var socialSourcePath = Path.Combine(
                faviconSourceDirectory,
                SiteGenerator.SocialImageSourceFileName);
            var socialSource = BuildInput.FromFile(
                $"favicon/{SiteGenerator.SocialImageSourceFileName}",
                BuildInputFingerprint.FromFile(socialSourcePath));
            nodes.Add(new BuildNode(
                DefaultSocialImageNodeId,
                [
                    socialSource,
                    Configuration("site.title", site.Title),
                    Configuration("theme.defaultSocialSubtitle", theme.DefaultSocialSubtitle),
                ],
                artifacts: [Artifact(DefaultSocialImageNodeId, routes.DefaultSocialImage)]));
            foreach (var post in posts)
            {
                var nodeId = PostSocialImageNodeId(routes.Post(post));
                nodes.Add(new BuildNode(
                    nodeId,
                    [
                        socialSource,
                        Configuration("site.title", site.Title),
                        Value("page.title", post.FrontMatter.Title),
                        Value("page.route", routes.Post(post).RelativeOutputPath),
                    ],
                    artifacts: [Artifact(nodeId, routes.PostSocialImage(post))]));
            }
        }

        if (generateLlmsTxt)
        {
            nodes.Add(new BuildNode(
                LlmsNodeId,
                [
                    Configuration(
                        "site.llms",
                        Json(new
                        {
                            site.Title,
                            site.Description,
                            site.BaseUrl,
                            text.LlmsPostsHeading,
                        })),
                    BuildInput.FromCollection("pages.markdown.llms", Json(posts.Select(post => new
                    {
                        Route = routes.Post(post).RelativeOutputPath,
                        post.FrontMatter.Title,
                        post.FrontMatter.Summary,
                    }))),
                    BuildInput.FromCollection(
                        "pages.content.llms",
                        ContentCollectionBuildPlanAdapter.SurfaceValue(
                            contentPages,
                            "llms",
                            includeContent: false,
                            GeneratedPageDerivedSurfaces.LlmsTxt)),
                ],
                artifacts: [Artifact(LlmsNodeId, routes.Llms)]));
        }
    }

    private static IEnumerable<BuildInput> HtmlLayoutInputs(
        SiteSettings site,
        SiteThemeOptions theme,
        bool hasFaviconAssets)
    {
        yield return Configuration(
            "site.layout",
            Json(new
            {
                site.Title,
                site.Description,
                site.BaseUrl,
                site.Language,
                AnalyticsMeasurementId = ResolveAnalyticsMeasurementId(site),
            }));
        yield return Configuration("theme.themeColor", theme.ThemeColor);
        yield return Configuration("theme.enableThemeSwitching", Json(theme.EnableThemeSwitching));
        yield return Configuration(
            "assets.hasFaviconSet",
            hasFaviconAssets.ToString(CultureInfo.InvariantCulture));
    }

    private static string DocsNavigationValue(
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<SiteExtraPage> extraPages,
        IReadOnlyList<IntegratedContentPage> contentPages,
        SiteRouteCatalog routes) =>
        Json(new
        {
            Posts = posts.Select(post => new
            {
                post.FilePath,
                Route = routes.Post(post).RelativeOutputPath,
                post.FrontMatter.Title,
                post.FrontMatter.SidebarLabel,
                post.FrontMatter.SidebarPosition,
            }),
            ExtraPages = extraPages.Select(page => new
            {
                Route = routes.ExtraPage(page).RelativeOutputPath,
                page.NavLabel,
            }),
            ContentPages = contentPages
                .Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.Navigation))
                .Select(page => new
            {
                Collection = page.CollectionId.Value,
                Entry = page.EntryId.Value,
                page.Route.RelativeOutputPath,
                page.Metadata.Title,
                Layout = page.LayoutId?.Value,
            }),
        });

    private static string BlogNavigationValue(
        IReadOnlyList<SiteExtraPage> extraPages,
        IReadOnlyList<IntegratedContentPage> contentPages,
        SiteRouteCatalog routes,
        SiteText text) =>
        Json(new
        {
            ExtraPages = extraPages.Select(page => new
            {
                Route = routes.ExtraPage(page).RelativeOutputPath,
                page.NavLabel,
                page.NavCssClass,
            }),
            ContentPages = contentPages
                .Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.Navigation))
                .Select(page => new
            {
                Collection = page.CollectionId.Value,
                Entry = page.EntryId.Value,
                page.Route.RelativeOutputPath,
                page.Metadata.Title,
                Layout = page.LayoutId?.Value,
            }),
            Text = new
            {
                text.MenuLabel,
                text.SiteNavigationLabel,
                text.SearchInputLabel,
                text.SearchButtonLabel,
                text.HeaderSearchPlaceholder,
            },
        });

    private static string ListingCollectionValue(
        IReadOnlyList<MarkdownPost> posts,
        SiteRouteCatalog routes,
        SiteSettings site) =>
        Json(posts.Select(post => new
        {
            Route = routes.Post(post).RelativeOutputPath,
            post.FrontMatter.Title,
            post.FrontMatter.Summary,
            Date = Format(post.FrontMatter.Date),
            DisplayDate = SiteFormatting.FormatDateTime(site, post.FrontMatter.Date),
            Month = SiteFormatting.FormatMonth(site, post.FrontMatter.Date),
        }));

    private static string SearchCollectionValue(
        IReadOnlyList<MarkdownPost> posts,
        SiteRouteCatalog routes,
        SiteSettings site) =>
        Json(posts.Select(post => new
        {
            Route = routes.Post(post).RelativeOutputPath,
            post.FrontMatter.Title,
            post.FrontMatter.Summary,
            post.FrontMatter.Tags,
            Date = SiteFormatting.FormatDateTime(site, post.FrontMatter.Date),
            post.MarkdownBody,
        }));

    private static string ResolveAnalyticsMeasurementId(SiteSettings site)
    {
        if (!string.IsNullOrWhiteSpace(site.GoogleAnalyticsMeasurementId))
        {
            return site.GoogleAnalyticsMeasurementId.Trim();
        }

        var value = Environment.GetEnvironmentVariable("GA_MEASUREMENT_ID");
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        value = Environment.GetEnvironmentVariable("GOOGLE_ANALYTICS_MEASUREMENT_ID");
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static void AddTextArtifactNode(
        ICollection<BuildNode> nodes,
        IDictionary<string, SiteTemplateFile> filesByPath,
        BuildNodeId nodeId,
        SiteRoute route,
        IEnumerable<BuildInput> inputs,
        IEnumerable<BuildNodeId>? dependencies = null)
    {
        if (!filesByPath.Remove(route.RelativeOutputPath))
        {
            throw new InvalidOperationException(
                $"Built-in template did not return expected artifact '{route.RelativeOutputPath}'.");
        }

        nodes.Add(new BuildNode(
            nodeId,
            inputs,
            dependencies,
            [Artifact(nodeId, route)]));
    }

    private static BuildArtifact Artifact(BuildNodeId owner, SiteRoute route) =>
        new(new BuildArtifactId($"artifact:{route.RelativeOutputPath}"), owner, route.RelativeOutputPath);

    private static BuildNodeId MarkdownPageNodeId(SiteRoute route) =>
        new($"page:markdown:{route.RelativeOutputPath}");

    private static BuildNodeId ExtraPageNodeId(SiteRoute route) =>
        new($"page:extra:{route.RelativeOutputPath}");

    private static BuildNodeId PostSocialImageNodeId(SiteRoute route) =>
        new($"social:post:{route.RelativeOutputPath}");

    private static BuildNodeId FaviconNodeId(string fileName) =>
        new($"asset:favicon:{fileName}");

    private static BuildInput Value(string key, string value) =>
        BuildInput.FromValue(key, value);

    private static BuildInput Configuration(string key, string value) =>
        BuildInput.FromConfiguration(key, value);

    private static string Format(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
}
