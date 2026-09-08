using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Routing;

namespace LithoSharp.Mdx;

/// <summary>How a collection handles documents missing from a translated variant.</summary>
public enum MissingDocumentPolicy
{
    /// <summary>Publish only translated documents and report missing IDs in inspection.</summary>
    Exclude,
    /// <summary>Fail the build before publication.</summary>
    Error
}

/// <summary>A document collection with independently configured variants and navigation.</summary>
public sealed record DocumentationCollection(string Id, IReadOnlyList<DocumentVariant> Variants)
{
    /// <summary>Uses official MDX for .mdx and .md. False uses the existing Markdown renderer without Node.</summary>
    public bool UseMdx { get; init; }
    /// <summary>Sidebars keyed by stable names. An empty map uses automatic navigation.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<SidebarItem>> Sidebars { get; init; } = new Dictionary<string, IReadOnlyList<SidebarItem>>();
    /// <summary>The default canonical sidebar. Null selects the first declared sidebar.</summary>
    public string? DefaultSidebar { get; init; }
    /// <summary>An absolute repository edit URL prefix; source paths are appended by segment.</summary>
    public string? EditUrl { get; init; }
    /// <summary>Reads the last committed date and author with Git. Explicit last_update fields take precedence.</summary>
    public bool GitMetadata { get; init; }
    /// <summary>The source locale used to check translation completeness; defaults to the first declared locale for each version.</summary>
    public string? SourceLocale { get; init; }
    /// <summary>The explicit behavior when translated document IDs are missing.</summary>
    public MissingDocumentPolicy MissingDocuments { get; init; } = MissingDocumentPolicy.Exclude;
}

/// <summary>Builds Markdown or MDX documentation through the existing typed collections and asset registry.</summary>
public sealed class DocumentationSite : ISiteBuildExtension, IAsyncDisposable
{
    private readonly MdxSite mdx;
    private readonly MdxBlogSite blogs;
    private readonly List<Registration> registrations = [];
    private DocumentCatalog catalog = new([]);
    private string browserHead = "";
    private readonly List<DocumentKey> missingTranslations = [];
    /// <summary>Optional progressive navigation and theme controls.</summary>
    public DocumentationBrowserOptions? Browser { get; init; }
    /// <summary>The public C# layout contract, including sidebar, body, table of contents and head regions.</summary>
    public IPageLayout<PageLayoutContent> Layout { get; init; } = new DocsPageLayout();
    /// <summary>Creates a documentation extension. Node is started only for an MDX collection on a cache miss.</summary>
    public DocumentationSite(MdxOptions options) { mdx = new(options); blogs = new(mdx); }
    /// <summary>Registers a blog in the same MDX graph, sharing React and all common chunks with documentation.</summary>
    public void AddBlog(MdxBlogCollection blog) => blogs.AddCollection(blog);
    /// <summary>Registers independent typed MDX pages in the same graph as documentation and blogs.</summary>
    public void AddPages<TFrontMatter>(IContentCollectionLoader<TFrontMatter, MdxDocument> loader,
        Func<ContentEntry<TFrontMatter, MdxDocument>, MdxPublicData>? publicData = null,
        ContentPageRenderer<TFrontMatter, MdxDocument>? renderer = null) where TFrontMatter : notnull => mdx.AddCollection(loader, publicData, renderer);
    /// <summary>The most recently prepared published document catalog.</summary>
    public DocumentCatalog Catalog => catalog;
    /// <summary>Measured MDX work, including valid no-op cache hits.</summary>
    public MdxBuildMetrics MdxMetrics => mdx.Metrics;
    /// <inheritdoc />
    public JsonElement? GetInspection() => JsonSerializer.SerializeToElement(new { kind = "documentation", mdx = mdx.GetInspection(),
        variants = registrations.Select(value => new { collection = value.Options.Id, value.Variant.Version, value.Variant.Locale, value.Variant.RoutePrefix }),
        pages = catalog.Pages.Select(page => new { page.Key, page.Route.PublicPath, page.FrontMatter.Unlisted }), missingTranslations });
    /// <summary>Optional shared UI messages for C# and React.</summary>
    public TranslationCatalog? Translations { get; init; }
    /// <summary>The explicit behavior for untranslated UI messages.</summary>
    public MissingTranslationPolicy TranslationPolicy { get; init; } = MissingTranslationPolicy.Source;
    /// <summary>Additional message keys used by site components, including dynamically selected keys.</summary>
    public IReadOnlyList<string> MessageKeys { get; init; } = [];

    /// <summary>Registers a collection and all its explicit version/language inputs.</summary>
    public void AddCollection(DocumentationCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        _ = new ContentCollectionId(collection.Id);
        if (collection.Variants.Count == 0) throw new ArgumentException("A documentation collection requires a variant.");
        if (registrations.Any(value => value.Options.Id == collection.Id)) throw new ArgumentException("Duplicate documentation collection.");
        foreach (var variant in collection.Variants)
        {
            _ = SiteRoute.ForDirectoryIndex(variant.RoutePrefix);
            _ = new ContentEntryId(variant.Version);
            _ = System.Globalization.CultureInfo.GetCultureInfo(variant.Locale);
            if (collection.Variants.Count(value => value.Version == variant.Version && value.Locale == variant.Locale) != 1)
                throw new ArgumentException("Duplicate document variant.");
            var registration = new Registration(collection, variant);
            registrations.Add(registration);
            if (collection.UseMdx) mdx.AddCollection(registration, entry => PublicData(registration, entry), (entry, context) => Render(registration, entry, context));
        }
    }

    /// <inheritdoc />
    public async Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
    {
        var documents = new List<DocumentPage>();
        var browserAssets = new List<SiteGeneratedAsset>();
        browserHead = "";
        missingTranslations.Clear();
        if (Browser is { } browser)
        {
            if (browser.Analytics is { } analytics && (analytics.Provider is not ("plausible" or "json") || !Uri.TryCreate(analytics.Endpoint.Value, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")))
                throw new ArgumentException("Analytics requires an explicit HTTP(S) endpoint and a supported provider.");
            if (browser.Algolia is { } algolia && (!System.Text.RegularExpressions.Regex.IsMatch(algolia.ApplicationId, "^[A-Za-z0-9]+$") || string.IsNullOrWhiteSpace(algolia.SearchOnlyApiKey) || string.IsNullOrWhiteSpace(algolia.IndexName)))
                throw new ArgumentException("Algolia requires an application ID, public search-only key and index name.");
            string Resource(string name) { using var stream = typeof(DocumentationSite).Assembly.GetManifestResourceStream("LithoSharp.Mdx.Browser." + name)!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
            string Asset(string id, string extension, string source)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(source);
                var path = "_docs/" + id + "-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + extension;
                browserAssets.Add(new("docs:" + id, path, bytes));
                return SiteUrl.ForFile(path, context.Site.BaseUrl).ToAttributeValue().ToString();
            }
            var config = JsonSerializer.Serialize(new { basePath = SiteUrl.ForDirectory("", context.Site.BaseUrl).Value,
                navigation = browser.Navigation, theme = browser.Theme, announcement = browser.Announcement,
                navbar = browser.Navbar.Select(link => new { label = link.Label, url = link.Url.Value }),
                footer = browser.Footer.Select(link => new { label = link.Label, url = link.Url.Value }), offline = browser.Offline,
                search = browser.Search, algolia = browser.Algolia is { } search ? new { applicationId = search.ApplicationId, searchOnlyApiKey = search.SearchOnlyApiKey, indexName = search.IndexName } : null,
                analytics = browser.Analytics is { } provider ? new { endpoint = provider.Endpoint.Value, domain = provider.Domain, provider = provider.Provider } : null });
            var script = Asset("client", ".js", Resource("client.mjs") + "\nstart(" + config + ");\n");
            var css = Asset("theme", ".css", Resource("theme.css"));
            browserHead = "<link rel=\"stylesheet\" href=\"" + css + "\"><script type=\"module\" src=\"" + script + "\"></script>";
        }
        foreach (var registration in registrations)
        {
            var options = registration.Options;
            var variant = registration.Variant;
            var collectionId = new ContentCollectionId($"docs:{options.Id}:{variant.Version}:{variant.Locale}");
            SiteRoute Route(ContentEntry<DocumentFrontMatter, MdxDocument> entry) => SiteRoute.ForDirectoryIndex(
                variant.RoutePrefix.Trim('/') + "/" + (entry.FrontMatter.Slug ?? entry.Id.Value).Trim('/'), context.Site.BaseUrl);
            PageMetadata Metadata(ContentEntry<DocumentFrontMatter, MdxDocument> entry) => new(entry.FrontMatter.Title,
                entry.FrontMatter.Description, entry.FrontMatter.Draft, entry.FrontMatter.PublishFrom, entry.FrontMatter.PublishUntil)
            {
                Language = variant.Locale, RightToLeft = variant.RightToLeft, NoIndex = variant.NoIndex || entry.FrontMatter.Unlisted,
                Document = new(options.Id, variant.Version, variant.Locale, entry.Id.Value),
                Alternates = catalog.Alternates(new(options.Id, variant.Version, variant.Locale, entry.Id.Value))
            };
            ContentCollection<DocumentFrontMatter, MdxDocument> raw;
            if (options.UseMdx)
            {
                var result = await new MdxContentCollectionLoader<DocumentFrontMatter>(collectionId, variant.InputDirectory, Route, Metadata)
                    { IncludeMarkdown = true, TransformationFingerprint = "docs-v1" }.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess) throw new SiteBuildExtensionException(result.Diagnostics);
                raw = result.Collection!;
            }
            else
            {
                var result = await new MarkdownContentCollectionLoader<DocumentFrontMatter>(collectionId, variant.InputDirectory,
                    _ => SiteRoute.ForDirectoryIndex("unused"), entry => new PageMetadata(entry.FrontMatter.Title))
                    .LoadAsync(cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess) throw new SiteBuildExtensionException(result.Diagnostics);
                raw = new(collectionId, variant.InputDirectory, result.Collection!.Entries.Select(entry => new ContentEntry<DocumentFrontMatter, MdxDocument>(
                    entry.Id, entry.SourcePath, entry.SourceFingerprint, entry.FrontMatter, new(entry.Body), entry.SourceLocation)), Route, Metadata);
            }
            var entries = raw.Entries.Where(entry => !MdxContentCollectionLoader<DocumentFrontMatter>.IsPartial(entry.SourcePath)).Select(entry => new ContentEntry<DocumentFrontMatter, MdxDocument>(
                new(entry.FrontMatter.Id ?? DocumentCatalog.DefaultId(entry.SourcePath)), entry.SourcePath, entry.SourceFingerprint,
                entry.FrontMatter, entry.Body, entry.SourceLocation)
            { DerivedSurfaces = entry.FrontMatter.Unlisted ? GeneratedPageDerivedSurfaces.None : (GeneratedPageDerivedSurfaces.Default | GeneratedPageDerivedSurfaces.Navigation) & (entry.FrontMatter.SearchExclude ? ~GeneratedPageDerivedSurfaces.Search : GeneratedPageDerivedSurfaces.All) }).ToArray();
            registration.Collection = new(collectionId, variant.InputDirectory, entries, Route, Metadata,
                transformationId: new("docs-v1"), isCacheable: true);
            if (options.GitMetadata)
                foreach (var entry in entries)
                    entry.FrontMatter.LastUpdate ??= await ReadGitUpdateAsync(variant.InputDirectory, entry.SourcePath, cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
                if (PagePublicationPolicy.ShouldPublish(Metadata(entry), context.BuildTimestamp, context.Options.EnvironmentName))
                    documents.Add(new(new(options.Id, variant.Version, variant.Locale, entry.Id.Value), entry.SourcePath, entry.FrontMatter, Route(entry)));
            registration.Categories = await ReadCategoriesAsync(variant.InputDirectory, cancellationToken).ConfigureAwait(false);
            foreach (var (directory, category) in registration.Categories.Where(pair => pair.Value.Link?.Type == "generated-index"))
            {
                var frontMatter = new DocumentFrontMatter { Id = "category:" + directory, Title = category.Link!.Title ?? category.Label ?? DocumentCatalog.RemoveNumericPrefix(Path.GetFileName(directory)),
                    Description = category.Link.Description, Slug = category.Link.Slug ?? DocumentCatalog.DefaultId(directory + ".md") };
                documents.Add(new(new(options.Id, variant.Version, variant.Locale, frontMatter.Id), (directory + "/_category_.json").TrimStart('/'), frontMatter,
                    SiteRoute.ForDirectoryIndex(variant.RoutePrefix.Trim('/') + "/" + frontMatter.Slug.Trim('/'), context.Site.BaseUrl)));
            }
        }
        foreach (var registration in registrations)
        {
            var sourceLocale = registration.Options.SourceLocale ?? registration.Options.Variants.First(variant => variant.Version == registration.Variant.Version).Locale;
            if (!registration.Options.Variants.Any(variant => variant.Version == registration.Variant.Version && variant.Locale == sourceLocale))
                throw new ArgumentException("The source locale must be registered for each version.");
            var translated = documents.Where(page => page.Key.Collection == registration.Options.Id && page.Key.Version == registration.Variant.Version && page.Key.Locale == registration.Variant.Locale).Select(page => page.Key.Id).ToHashSet(StringComparer.Ordinal);
            var missing = documents.Where(page => page.Key.Collection == registration.Options.Id && page.Key.Version == registration.Variant.Version && page.Key.Locale == sourceLocale && !translated.Contains(page.Key.Id))
                .Select(page => page.Key with { Locale = registration.Variant.Locale }).ToArray();
            missingTranslations.AddRange(missing);
            if (registration.Options.MissingDocuments == MissingDocumentPolicy.Error && missing.Length > 0)
                throw new SiteBuildExtensionException(missing.Select(key => new LithoSharp.Diagnostics.SiteDiagnostic("LSDOC001", LithoSharp.Diagnostics.SiteDiagnosticSeverity.Error, "Missing translated document: " + key)));
        }
        catalog = new(documents);
        foreach (var registration in registrations)
        {
            var sidebars = registration.Options.Sidebars;
            registration.Navigation = sidebars.Count == 0
                ? new Dictionary<string, IReadOnlyList<DocumentNavigationItem>> { ["default"] = catalog.Sidebar(registration.Options.Id, registration.Variant.Version, registration.Variant.Locale, categories: registration.Categories) }
                : sidebars.ToDictionary(pair => pair.Key, pair => catalog.Sidebar(registration.Options.Id, registration.Variant.Version, registration.Variant.Locale, pair.Value, registration.Categories));
            var collection = registration.Collection!;
            registration.Collection = new(collection.Id, collection.InputRoot, collection.Entries, collection.RouteConvention, collection.PublicationMapper,
                declaredDependencies: [ContentDependency.FromValue("docs.navigation", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { registration.Navigation, registration.Options.Variants,
                    Switches = catalog.Pages.Select(page => new { page.Key, page.Route.PublicPath, page.FrontMatter }), browserHead, Messages = UiMessages(registration), Layout = Layout.GetType().FullName })))),
                    .. browserAssets.Select(asset => ContentDependency.FromAsset(asset.Id)),
                    .. Browser is { Search: true, Algolia: null } ? new[] { ContentDependency.FromAsset("docs:search:" + collection.Id.Value) } : []], transformationId: collection.TransformationId, isCacheable: collection.IsCacheable);
        }
        var prepared = blogs.Contribute(await mdx.PrepareAsync(context, cancellationToken).ConfigureAwait(false), context);
        if (Browser is { Search: true, Algolia: null })
        {
            foreach (var registration in registrations)
            {
                var collection = registration.Options.UseMdx ? prepared.ContentCollections.OfType<SiteContentCollection<DocumentFrontMatter, MdxDocument>>().FirstOrDefault(value => value.Collection.Id == registration.Collection!.Id)?.Collection : registration.Collection;
                var documentsForSearch = (collection?.Entries ?? []).Where(entry => !entry.FrontMatter.Unlisted && !entry.FrontMatter.SearchExclude
                    && PagePublicationPolicy.ShouldPublish(collection!.PublicationMapper(entry), context.BuildTimestamp, context.Options.EnvironmentName)).Select(entry =>
                {
                    var html = registration.Options.UseMdx ? entry.Body.ToHtmlString() : new SiteGenerator().RenderMarkdown(entry.Body.Body);
                    var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
                    foreach (var element in document.QuerySelectorAll("script,style,nav,noscript")) element.Remove();
                    return new LithoSharp.Search.SearchDocument(entry.FrontMatter.Title, entry.FrontMatter.Description ?? "", entry.FrontMatter.Tags,
                        collection!.RouteConvention(entry).PublicPath, "", SiteGenerator.NormalizeForIndex(document.Body?.TextContent ?? ""))
                    { Collection = registration.Options.Id, Version = registration.Variant.Version, Locale = registration.Variant.Locale, Sections = SiteGenerator.ExtractSearchSections(html) };
                }).ToArray();
                var path = registration.Variant.RoutePrefix.Trim('/') + "/_search.json";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(documentsForSearch, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                browserAssets.Add(new("docs:search:" + registration.Collection!.Id.Value, path, bytes));
                registration.SearchUrl = SiteUrl.ForFile(path, context.Site.BaseUrl).Value + "?v=" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
            }
        }
        if (Browser is { Offline: true })
        {
            var urls = catalog.Pages.Where(page => !page.FrontMatter.Unlisted).Select(page => page.Route.PublicPath)
                .Concat(prepared.Assets.Concat(browserAssets).Select(asset => SiteUrl.ForFile(asset.RelativeOutputPath, context.Site.BaseUrl).Value))
                .Concat(new[] { "assets/site.css" }.Select(path => SiteUrl.ForFile(path, context.Site.BaseUrl).Value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var revision = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { urls, context.Site,
                sources = registrations.SelectMany(registration => registration.Collection!.Entries).Select(entry => entry.SourceFingerprint),
                assembly = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(DocumentationSite).Assembly.Location)) })))[..24];
            var prefix = "lithosharp:" + SiteUrl.ForDirectory("", context.Site.BaseUrl).Value + ":";
            using var resource = typeof(DocumentationSite).Assembly.GetManifestResourceStream("LithoSharp.Mdx.Browser/offline.mjs".Replace('/', '.'))!;
            using var reader = new StreamReader(resource);
            var workerSource = "const config=" + JsonSerializer.Serialize(new { urls, cache = prefix + revision, prefix }) + ";\n" + reader.ReadToEnd();
            browserAssets.Add(new("docs:service-worker", "lithosharp-sw.js", System.Text.Encoding.UTF8.GetBytes(workerSource)));
            var manifest = JsonSerializer.Serialize(new { name = context.Site.Title, short_name = context.Site.Title, start_url = urls.FirstOrDefault(url => url.EndsWith('/')) ?? SiteUrl.ForDirectory("", context.Site.BaseUrl).Value,
                scope = SiteUrl.ForDirectory("", context.Site.BaseUrl).Value, display = "standalone", lang = context.Site.Language });
            browserAssets.Add(new("docs:manifest", "site.webmanifest", System.Text.Encoding.UTF8.GetBytes(manifest)));
            browserHead += "<link rel=\"manifest\" href=\"" + SiteUrl.ForFile("site.webmanifest", context.Site.BaseUrl).ToAttributeValue() + "\">";
        }
        else if (Browser is not null)
        {
            var prefix = JsonSerializer.Serialize("lithosharp:" + SiteUrl.ForDirectory("", context.Site.BaseUrl).Value + ":");
            var retire = "self.addEventListener('install',()=>self.skipWaiting());self.addEventListener('activate',event=>event.waitUntil((async()=>{for(const key of await caches.keys())if(key.startsWith(" + prefix + "))await caches.delete(key);await self.registration.unregister();for(const client of await self.clients.matchAll({type:'window'}))client.postMessage({type:'lithosharp:offline-retired'});})()));";
            browserAssets.Add(new("docs:service-worker", "lithosharp-sw.js", System.Text.Encoding.UTF8.GetBytes(retire)));
        }
        var clientIndex = browserAssets.FindIndex(asset => asset.Id == "docs:client");
        if (clientIndex >= 0)
        {
            var client = browserAssets[clientIndex];
            browserAssets[clientIndex] = new(client.Id, client.RelativeOutputPath, client.Bytes, client.Inputs,
                browserAssets.Where(asset => asset.Id == "docs:service-worker" || asset.Id.StartsWith("docs:search:", StringComparison.Ordinal)).Select(asset => asset.Id));
        }
        var collections = prepared.ContentCollections.ToList();
        foreach (var registration in registrations.Where(registration => !registration.Options.UseMdx))
            collections.Add(new SiteContentCollection<DocumentFrontMatter, MdxDocument>(registration.Collection!,
                (entry, rendering) => Render(registration, entry, rendering)) { RendererFingerprint = Layout is DocsPageLayout ? "docs-v1" : null, IsThreadSafe = Layout is DocsPageLayout });
        foreach (var registration in registrations)
        {
            var id = registration.Collection!.Id;
            var categories = catalog.Pages.Where(page => page.Key.Collection == registration.Options.Id && page.Key.Version == registration.Variant.Version && page.Key.Locale == registration.Variant.Locale && page.Key.Id.StartsWith("category:", StringComparison.Ordinal)).ToArray();
            if (categories.Length > 0)
            {
                var categoryCollection = new ContentCollection<DocumentFrontMatter, DocumentPage>(new(id.Value + ":categories"), registration.Variant.InputDirectory,
                    categories.Select(page => new ContentEntry<DocumentFrontMatter, DocumentPage>(new(page.Key.Id), page.SourcePath,
                        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(page.FrontMatter))), page.FrontMatter, page)),
                    entry => entry.Body.Route, entry => new PageMetadata(entry.FrontMatter.Title, entry.FrontMatter.Description) { Language = registration.Variant.Locale, Document = entry.Body.Key },
                    declaredDependencies: registration.Collection.DeclaredDependencies, transformationId: new("docs-categories-v1"), isCacheable: true);
                collections.Add(new SiteContentCollection<DocumentFrontMatter, DocumentPage>(categoryCollection, (entry, rendering) =>
                    rendering.RenderDocument("<h1>" + Html.Encode(entry.FrontMatter.Title) + "</h1><p>" + Html.Encode(entry.FrontMatter.Description ?? "") + "</p>" +
                        Cards(registration, registration.Collection.Entries.Where(page => !page.FrontMatter.Unlisted && page.SourcePath.StartsWith(entry.Id.Value["category:".Length..] + "/", StringComparison.Ordinal)
                            && PagePublicationPolicy.ShouldPublish(registration.Collection.PublicationMapper(page), context.BuildTimestamp, context.Options.EnvironmentName)).ToArray())))
                    { RendererFingerprint = "docs-categories-v1" });
            }
            collections.Add(registration.Collection.GeneratePages(new(id.Value + ":tags"), entry => entry.FrontMatter.Unlisted ? [] : entry.FrontMatter.Tags,
                group => new SitePage<IReadOnlyList<ContentEntry<DocumentFrontMatter, MdxDocument>>>(new("tag:" + group.Key),
                    SiteRoute.ForDirectoryIndex(registration.Variant.RoutePrefix.Trim('/') + "/tags/" + Uri.EscapeDataString(group.Key), context.Site.BaseUrl),
                    group.Entries, new PageMetadata(group.Key) { Language = registration.Variant.Locale, RightToLeft = registration.Variant.RightToLeft }),
                (page, rendering) => rendering.RenderDocument("<h1>" + Html.Encode(page.Metadata.Title!) + "</h1>" + Cards(registration, page.Content)),
                transformationId: new("docs-tags-v1"), isCacheable: true));
        }
        return new() { ContentCollections = collections, Assets = [.. prepared.Assets, .. browserAssets] };
    }

    private string Render(Registration registration, ContentEntry<DocumentFrontMatter, MdxDocument> entry, ContentPageRenderingContext context)
    {
        var key = new DocumentKey(registration.Options.Id, registration.Variant.Version, registration.Variant.Locale, entry.Id.Value);
        string T(string id, string fallback) => Message(registration, id, fallback);
        var selected = entry.FrontMatter.DisplayedSidebar ?? registration.Options.DefaultSidebar ?? registration.Navigation.Keys.First();
        if (!registration.Navigation.TryGetValue(selected, out var navigation)) throw new ArgumentException($"Unknown sidebar '{selected}'.");
        var breadcrumbs = DocumentCatalog.Breadcrumbs(key, navigation);
        var adjacent = catalog.Adjacent(key, navigation);
        var article = registration.Options.UseMdx ? entry.Body.ToHtmlString() : context.RenderMarkdown(entry.Body.Body);
        context.SetDerivedContent(article);
        var body = (registration.Variant.Banner is { } banner ? "<aside role=\"note\">" + Html.Encode(banner) + "</aside>" : "")
            + "<nav aria-label=\"" + Html.Encode(T("breadcrumb", "Breadcrumb")) + "\"><ol>" + string.Concat(breadcrumbs.Select(item => "<li>" + Link(item.Label, item.Url) + "</li>")) + "</ol></nav>"
            + (!entry.FrontMatter.HideTitle && !SiteGenerator.ExtractTemplateHeadings(article).Any(heading => heading.Level == 1) ? "<h1>" + Html.Encode(entry.FrontMatter.Title) + "</h1>" : "")
            + article
            + "<nav aria-label=\"" + Html.Encode(T("adjacent", "Previous and next documents")) + "\">" + PageLink(adjacent.Previous, "prev") + PageLink(adjacent.Next, "next") + "</nav>";
        var edit = entry.FrontMatter.CustomEditUrl ?? (registration.Options.EditUrl is { } prefix ? prefix.TrimEnd('/') + "/" + string.Join('/', entry.SourcePath.Split('/').Select(Uri.EscapeDataString)) : null);
        if (edit is not null) body += "<p>" + Link(T("edit", "Edit this page"), SiteUrl.FromAbsolute(edit)) + "</p>";
        if (entry.FrontMatter.LastUpdate is { } update)
            body += "<p class=\"docs-last-update\">" + Html.Encode(T("lastUpdated", "Last updated")) + " "
                + (update.Date is { } date ? "<time datetime=\"" + date.ToString("O") + "\">" + Html.Encode(date.ToString("d", System.Globalization.CultureInfo.GetCultureInfo(registration.Variant.Locale))) + "</time> " : "")
                + Html.Encode(update.Author ?? "") + "</p>";
        body += "<nav aria-label=\"" + Html.Encode(T("variants", "Version and language")) + "\">" + string.Concat(registration.Options.Variants.Select(variant =>
        {
            try { return Link((variant.Label ?? variant.Version) + " · " + variant.Locale, SiteUrl.FromRoute(catalog.Switch(key, variant.Version, variant.Locale, variant.FallbackDocumentId).Route)); }
            catch (KeyNotFoundException) { return "<span aria-disabled=\"true\">" + Html.Encode((variant.Label ?? variant.Version) + " · " + variant.Locale) + "</span>"; }
        })) + "</nav>";
        body = "<script type=\"application/json\" data-ls-messages>" + JsonSerializer.Serialize(UiMessages(registration)) + "</script>" + body;
        if (Browser is { Search: true }) body = "<section data-ls-search data-index=\"" + Html.Encode(registration.SearchUrl ?? "") + "\" data-collection=\"" + Html.Encode(key.Collection)
            + "\" data-version=\"" + Html.Encode(key.Version) + "\" data-locale=\"" + Html.Encode(key.Locale) + "\"><label>" + Html.Encode(T("search", "Search this documentation"))
            + "<input type=\"search\" aria-label=\"" + Html.Encode(T("search", "Search this documentation")) + "\"></label><p role=\"status\" aria-live=\"polite\"></p><div data-results></div></section>" + body;
        var layoutContext = context.CreateLayoutContext();
        return Layout.Render(new SitePage<PageLayoutContent>(new("docs:" + key), context.Route,
            new(Html.UnsafeRaw(body)) { Sidebar = Html.UnsafeRaw(RenderNavigation(navigation, key)), Head = Html.UnsafeRaw(browserHead), IncludeDefaultScript = Browser is null,
                TableOfContents = entry.FrontMatter.HideTableOfContents ? null : new TableOfContentsComponent().Render(SiteGenerator.ExtractTemplateHeadings(article), layoutContext) }, context.Metadata), layoutContext).ToHtmlString();
    }
    private string Cards(Registration registration, IReadOnlyList<ContentEntry<DocumentFrontMatter, MdxDocument>> entries) =>
        "<ul>" + string.Concat(entries.Select(entry => "<li>" + Link(entry.FrontMatter.Title, SiteUrl.FromRoute(catalog.Resolve(
            new(registration.Options.Id, registration.Variant.Version, registration.Variant.Locale, entry.Id.Value)).Route)) + "</li>")) + "</ul>";
    private static string Link(string label, SiteUrl? url) => url is null ? Html.Encode(label) : "<a href=\"" + url.ToAttributeValue() + "\">" + Html.Encode(label) + "</a>";
    private static string PageLink(DocumentPage? page, string relation) => page is null ? "" : "<a rel=\"" + relation + "\" href=\"" + SiteUrl.FromRoute(page.Route).ToAttributeValue() + "\">" + Html.Encode(page.FrontMatter.Title) + "</a>";
    private static string RenderNavigation(IReadOnlyList<DocumentNavigationItem> items, DocumentKey current)
    {
        string List(IReadOnlyList<DocumentNavigationItem> nodes) => "<ul>" + string.Concat(nodes.Select(node => "<li>" +
            (node.Children.Count > 0 ? "<details" + (!node.Collapsed || DocumentCatalog.Breadcrumbs(current, [node]).Count > 0 ? " open" : "") + "><summary>" + Link(node.Label, node.Url) + "</summary>" + List(node.Children) + "</details>"
                : node.Document == current ? "<a aria-current=\"page\" href=\"" + node.Url!.ToAttributeValue() + "\">" + Html.Encode(node.Label) + "</a>" : Link(node.Label, node.Url)) + "</li>")) + "</ul>";
        return "<aside id=\"docs-sidebar\" class=\"docs-sidebar\" data-docs-sidebar><nav aria-label=\"Documentation navigation\">" + List(items) + "</nav></aside>";
    }
    private MdxPublicData PublicData(Registration registration, ContentEntry<DocumentFrontMatter, MdxDocument> entry)
    {
        var messages = UiMessages(registration);
        foreach (var pair in Translations?.Select(registration.Variant.Locale, MessageKeys.Concat(TranslationCatalog.ExtractKeys(entry.Body.Body)), TranslationPolicy) ?? new Dictionary<string, string>()) messages[pair.Key] = pair.Value;
        return new(JsonSerializer.SerializeToElement(new { frontMatter = new { title = entry.FrontMatter.Title }, messages }),
            JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false, properties = new {
                frontMatter = new { type = "object", additionalProperties = false, properties = new { title = new { type = "string" } } },
                messages = new { type = "object", additionalProperties = false, properties = messages.Keys.ToDictionary(key => key, _ => new { type = "string" }) }
            } }));
    }
    private string Message(Registration registration, string id, string fallback)
    {
        if (Translations is null) return fallback;
        try { return Translations.Get(registration.Variant.Locale, id, TranslationPolicy) ?? ""; }
        catch (KeyNotFoundException) when (TranslationPolicy == MissingTranslationPolicy.Source) { return fallback; }
    }
    private Dictionary<string, string> UiMessages(Registration registration) => new Dictionary<string, string>
    {
        ["lastUpdated"] = "Last updated", ["theme"] = "Theme", ["system"] = "system", ["light"] = "light", ["dark"] = "dark", ["dismiss"] = "Dismiss", ["offlineUpdate"] = "Update offline content",
        ["searching"] = "Searching…", ["results"] = "{count} results", ["searchUnavailable"] = "Search unavailable. You can still browse this documentation.",
        ["copy"] = "Copy", ["copied"] = "Copied", ["copyUnavailable"] = "Copy unavailable"
        , ["breadcrumb"] = "Breadcrumb", ["adjacent"] = "Previous and next documents", ["variants"] = "Version and language", ["edit"] = "Edit this page", ["search"] = "Search this documentation"
    }.ToDictionary(pair => pair.Key, pair => Message(registration, pair.Key, pair.Value), StringComparer.Ordinal);
    private static async Task<IReadOnlyDictionary<string, DocumentCategory>> ReadCategoriesAsync(string root, CancellationToken cancellationToken)
    {
        var categories = new Dictionary<string, DocumentCategory>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "_category_.*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetExtension(file) is not (".json" or ".yml" or ".yaml")) continue;
            var bytes = await ContentPath.ReadAllBytesAsync(root, file, cancellationToken).ConfigureAwait(false);
            DocumentCategory category;
            if (Path.GetExtension(file) == ".json")
            {
                using var json = JsonDocument.Parse(bytes);
                void Check(JsonElement element)
                {
                    if (element.ValueKind != JsonValueKind.Object) return;
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new ArgumentException("Duplicate category key: " + property.Name); Check(property.Value); }
                }
                Check(json.RootElement);
                category = json.RootElement.Deserialize<DocumentCategory>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }) ?? throw new ArgumentException("Category metadata must be an object.");
            }
            else
            {
                var parsed = MarkdownContentCollectionLoader<DocumentCategory>.ParseYaml(System.Text.Encoding.UTF8.GetString(bytes), Path.GetRelativePath(root, file), 1, cancellationToken);
                if (!parsed.IsSuccess) throw new SiteBuildExtensionException(parsed.Diagnostics);
                var bound = new ReflectionContentFrontMatterBinder<DocumentCategory>().Bind(parsed.Value!, new Diagnostics.SiteSourceLocation(Path.GetRelativePath(root, file)));
                if (!bound.IsSuccess) throw new SiteBuildExtensionException(bound.Diagnostics);
                category = bound.Value!;
            }
            if (category.Link is { Type: not ("doc" or "generated-index") }) throw new ArgumentException("Category link type must be doc or generated-index.");
            categories.Add(Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Replace('\\', '/').Trim('.'), category);
        }
        return categories;
    }
    private static async Task<DocumentUpdate?> ReadGitUpdateAsync(string root, string sourcePath, CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "log", "-1", "--format=%aI%n%an", "--", sourcePath }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException("Git metadata failed: " + await error.ConfigureAwait(false));
        var lines = (await output.ConfigureAwait(false)).Split('\n');
        return DateTimeOffset.TryParse(lines[0], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? new() { Date = date, Author = lines.ElementAtOrDefault(1)?.TrimEnd('\r') } : null;
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync() => mdx.DisposeAsync();

    private sealed class Registration(DocumentationCollection options, DocumentVariant variant) : IContentCollectionLoader<DocumentFrontMatter, MdxDocument>
    {
        internal DocumentationCollection Options { get; } = options;
        internal DocumentVariant Variant { get; } = variant;
        internal ContentCollection<DocumentFrontMatter, MdxDocument>? Collection { get; set; }
        internal string? SearchUrl { get; set; }
        internal IReadOnlyDictionary<string, DocumentCategory> Categories { get; set; } = new Dictionary<string, DocumentCategory>();
        internal IReadOnlyDictionary<string, IReadOnlyList<DocumentNavigationItem>> Navigation { get; set; } = new Dictionary<string, IReadOnlyList<DocumentNavigationItem>>();
        public ValueTask<ContentLoadResult<DocumentFrontMatter, MdxDocument>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ContentLoadResult<DocumentFrontMatter, MdxDocument>.Success(Collection ?? throw new InvalidOperationException("Prepare documentation before its MDX extension.")));
    }
}
