using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Routing;

namespace LithoSharp.Mdx;

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
}

/// <summary>Builds Markdown or MDX documentation through the existing typed collections and asset registry.</summary>
public sealed class DocumentationSite : ISiteBuildExtension, IAsyncDisposable
{
    private readonly MdxSite mdx;
    private readonly List<Registration> registrations = [];
    private DocumentCatalog catalog = new([]);
    private string browserHead = "";
    /// <summary>Optional progressive navigation and theme controls.</summary>
    public DocumentationBrowserOptions? Browser { get; init; }
    /// <summary>Creates a documentation extension. Node is started only for an MDX collection on a cache miss.</summary>
    public DocumentationSite(MdxOptions options) => mdx = new(options);
    /// <summary>The most recently prepared published document catalog.</summary>
    public DocumentCatalog Catalog => catalog;
    /// <summary>Measured MDX work, including valid no-op cache hits.</summary>
    public MdxBuildMetrics MdxMetrics => mdx.Metrics;
    /// <inheritdoc />
    public JsonElement? GetInspection() => JsonSerializer.SerializeToElement(new { kind = "documentation", mdx = mdx.GetInspection(),
        variants = registrations.Select(value => new { collection = value.Options.Id, value.Variant.Version, value.Variant.Locale, value.Variant.RoutePrefix }),
        pages = catalog.Pages.Select(page => new { page.Key, page.Route.PublicPath, page.FrontMatter.Unlisted }) });
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
        if (Browser is { } browser)
        {
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
                footer = browser.Footer.Select(link => new { label = link.Label, url = link.Url.Value }) });
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
            var entries = raw.Entries.Select(entry => new ContentEntry<DocumentFrontMatter, MdxDocument>(
                new(entry.FrontMatter.Id ?? DocumentCatalog.DefaultId(entry.SourcePath)), entry.SourcePath, entry.SourceFingerprint,
                entry.FrontMatter, entry.Body, entry.SourceLocation)
            { DerivedSurfaces = entry.FrontMatter.Unlisted ? GeneratedPageDerivedSurfaces.None : GeneratedPageDerivedSurfaces.Default | GeneratedPageDerivedSurfaces.Navigation }).ToArray();
            registration.Collection = new(collectionId, variant.InputDirectory, entries, Route, Metadata,
                transformationId: new("docs-v1"), isCacheable: true);
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
        catalog = new(documents);
        foreach (var registration in registrations)
        {
            var sidebars = registration.Options.Sidebars;
            registration.Navigation = sidebars.Count == 0
                ? new Dictionary<string, IReadOnlyList<DocumentNavigationItem>> { ["default"] = catalog.Sidebar(registration.Options.Id, registration.Variant.Version, registration.Variant.Locale, categories: registration.Categories) }
                : sidebars.ToDictionary(pair => pair.Key, pair => catalog.Sidebar(registration.Options.Id, registration.Variant.Version, registration.Variant.Locale, pair.Value, registration.Categories));
            var collection = registration.Collection!;
            registration.Collection = new(collection.Id, collection.InputRoot, collection.Entries, collection.RouteConvention, collection.PublicationMapper,
                declaredDependencies: [ContentDependency.FromValue("docs.navigation", JsonSerializer.Serialize(new { registration.Navigation, registration.Options.Variants,
                    Switches = catalog.Pages.Select(page => new { page.Key, page.Route.PublicPath, page.FrontMatter }), browserHead })),
                    .. browserAssets.Select(asset => ContentDependency.FromAsset(asset.Id))], transformationId: collection.TransformationId, isCacheable: collection.IsCacheable);
        }
        var prepared = await mdx.PrepareAsync(context, cancellationToken).ConfigureAwait(false);
        var collections = prepared.ContentCollections.ToList();
        foreach (var registration in registrations.Where(registration => !registration.Options.UseMdx))
            collections.Add(new SiteContentCollection<DocumentFrontMatter, MdxDocument>(registration.Collection!,
                (entry, rendering) => Render(registration, entry, rendering)) { RendererFingerprint = "docs-v1", IsThreadSafe = true });
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
        string T(string id, string fallback) => Translations?.Get(registration.Variant.Locale, id, TranslationPolicy) ?? fallback;
        var selected = entry.FrontMatter.DisplayedSidebar ?? registration.Options.DefaultSidebar ?? registration.Navigation.Keys.First();
        if (!registration.Navigation.TryGetValue(selected, out var navigation)) throw new ArgumentException($"Unknown sidebar '{selected}'.");
        var breadcrumbs = DocumentCatalog.Breadcrumbs(key, navigation);
        var adjacent = catalog.Adjacent(key, navigation);
        var article = registration.Options.UseMdx ? entry.Body.ToHtmlString() : context.RenderMarkdown(entry.Body.Body);
        context.SetDerivedContent(article);
        var body = (registration.Variant.Banner is { } banner ? "<aside role=\"note\">" + Html.Encode(banner) + "</aside>" : "")
            + "<nav aria-label=\"" + Html.Encode(T("breadcrumb", "Breadcrumb")) + "\"><ol>" + string.Concat(breadcrumbs.Select(item => "<li>" + Link(item.Label, item.Url) + "</li>")) + "</ol></nav>"
            + (!entry.FrontMatter.HideTitle ? "<h1>" + Html.Encode(entry.FrontMatter.Title) + "</h1>" : "")
            + article
            + "<nav aria-label=\"Previous and next documents\">" + PageLink(adjacent.Previous, "prev") + PageLink(adjacent.Next, "next") + "</nav>";
        var edit = entry.FrontMatter.CustomEditUrl ?? (registration.Options.EditUrl is { } prefix ? prefix.TrimEnd('/') + "/" + string.Join('/', entry.SourcePath.Split('/').Select(Uri.EscapeDataString)) : null);
        if (edit is not null) body += "<p>" + Link(T("edit", "Edit this page"), SiteUrl.FromAbsolute(edit)) + "</p>";
        body += "<nav aria-label=\"Version and language\">" + string.Concat(registration.Options.Variants.Select(variant =>
        {
            try { return Link((variant.Label ?? variant.Version) + " · " + variant.Locale, SiteUrl.FromRoute(catalog.Switch(key, variant.Version, variant.Locale, variant.FallbackDocumentId).Route)); }
            catch (KeyNotFoundException) { return "<span aria-disabled=\"true\">" + Html.Encode((variant.Label ?? variant.Version) + " · " + variant.Locale) + "</span>"; }
        })) + "</nav>";
        return new DocsPageLayout().Render(new SitePage<PageLayoutContent>(new("docs:" + key), context.Route,
            new(Html.UnsafeRaw(body)) { Sidebar = Html.UnsafeRaw(RenderNavigation(navigation, key)), Head = Html.UnsafeRaw(browserHead), IncludeDefaultScript = Browser is null }, context.Metadata), context.CreateLayoutContext()).ToHtmlString();
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
        var messages = Translations?.Select(registration.Variant.Locale, MessageKeys.Concat(TranslationCatalog.ExtractKeys(entry.Body.Body)), TranslationPolicy) ?? new Dictionary<string, string>();
        return new(JsonSerializer.SerializeToElement(new { frontMatter = new { title = entry.FrontMatter.Title }, messages }),
            JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false, properties = new {
                frontMatter = new { type = "object", additionalProperties = false, properties = new { title = new { type = "string" } } },
                messages = new { type = "object", additionalProperties = false, properties = messages.Keys.ToDictionary(key => key, _ => new { type = "string" }) }
            } }));
    }
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
    /// <inheritdoc />
    public ValueTask DisposeAsync() => mdx.DisposeAsync();

    private sealed class Registration(DocumentationCollection options, DocumentVariant variant) : IContentCollectionLoader<DocumentFrontMatter, MdxDocument>
    {
        internal DocumentationCollection Options { get; } = options;
        internal DocumentVariant Variant { get; } = variant;
        internal ContentCollection<DocumentFrontMatter, MdxDocument>? Collection { get; set; }
        internal IReadOnlyDictionary<string, DocumentCategory> Categories { get; set; } = new Dictionary<string, DocumentCategory>();
        internal IReadOnlyDictionary<string, IReadOnlyList<DocumentNavigationItem>> Navigation { get; set; } = new Dictionary<string, IReadOnlyList<DocumentNavigationItem>>();
        public ValueTask<ContentLoadResult<DocumentFrontMatter, MdxDocument>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ContentLoadResult<DocumentFrontMatter, MdxDocument>.Success(Collection ?? throw new InvalidOperationException("Prepare documentation before its MDX extension.")));
    }
}
