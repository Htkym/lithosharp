# LithoSharp

[![build](https://github.com/Htkym/lithosharp/actions/workflows/build.yml/badge.svg)](https://github.com/Htkym/lithosharp/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/LithoSharp.svg)](https://www.nuget.org/packages/LithoSharp)

[English](README.md) | [日本語](README.ja.md)

A small, batteries-included static site generator for .NET. Give it Markdown and a
bit of site configuration, and it renders a Docusaurus-inspired documentation site
by default. The Docs template builds a responsive hierarchy sidebar, page table of
contents, and previous/next links. The legacy Blog template remains available when
you select it explicitly.

LithoSharp is designed to be embedded in your own console app or build pipeline.
Site-specific behavior is injected through a single `SiteCustomization` object, so
the core library has no opinions about your brand, copy, or validation rules.

## Features

- Docusaurus-inspired Docs output by default, with a responsive hierarchy sidebar,
  H2/H3 table of contents, and previous/next document links
- An explicit Blog template with listing pages, archives, tags, client-side search,
  RSS (`feed.xml`), and `sitemap.xml`
- Canonical, Open Graph, and Twitter Card metadata, plus optional favicon and social
  image assets
- Front matter validation (title and date required; summary required by default)
- Optional `llms.txt` summary for language models
- Typed Markdown, JSON, and CSV collections that can emit routed pages and
  participate in publication filtering and derived site artifacts
- An optional source-generator package for static Markdown binders, schemas,
  routes, IDs, and typed page or entry references
- Graceful degradation: when favicon or social-image sources are missing, those
  outputs are skipped and a complete HTML site is still produced

## Install

```powershell
dotnet add package LithoSharp
```

LithoSharp targets `net10.0` and depends on Markdig, YamlDotNet, and SkiaSharp.

## Quick start

The whole flow is three calls: read posts, optionally validate, then generate.

```csharp
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

var site = new SiteSettings
{
    Title = "My Site",
    Description = "A static site generated with LithoSharp.",
    BaseUrl = "https://example.com/",
    Language = "en",
    Author = "Me",
    TimeZone = "UTC"
};

var customization = new SiteCustomization
{
    Theme = new SiteThemeOptions
    {
        BrandPrefix = "my site / ",
        DefaultSocialSubtitle = "Built with LithoSharp",
        AdditionalCss = ":root { --accent: #7c9eff; }"
    },
    GenerateLlmsTxt = true
};

var posts = await new MarkdownPostReader().ReadAllAsync("content");
SiteGenerator.Validate(site, "content", posts, customization);
var options = new SiteGenerationOptions
{
    EnvironmentName = "Production"
};
var result = await new SiteGenerator().GenerateAsync(
    site, posts, "_site", clean: true, customization, options, CancellationToken.None);

Console.WriteLine($"Generated {result.PostCount} post(s) into {result.OutputDirectory}.");
```

For an explicit publication timestamp and environment, use
`GenerateWithOptionsAsync`:

```csharp
var result = await new SiteGenerator().GenerateWithOptionsAsync(
    site,
    posts,
    "_site",
    clean: true,
    customization: null,
    new SiteGenerationOptions
    {
        BuildTimestamp = DateTimeOffset.Parse("2026-01-02T12:00:00Z"),
        EnvironmentName = "Production"
    },
    CancellationToken.None);
```

A runnable documentation example lives in
[`samples/LithoSharp.DocsSample`](samples/LithoSharp.DocsSample). The legacy blog
layout is shown in [`samples/LithoSharp.Sample`](samples/LithoSharp.Sample).

```powershell
dotnet run --project samples/LithoSharp.DocsSample -- --output _site
```

Add `--check` to run the pre-commit site quality checks and print their text
report. Add `--redirect-demo` to generate `old-home.html` as a redirect to the
site root. Both switches are optional, so the default sample output is unchanged.
See [site quality checks](docs/site-quality.md) for failure thresholds,
diagnostic IDs, output formats, external-link checks, and redirect rules.

The default `DocsSiteTemplate` uses the directory structure under `content` for its
left navigation. `intro.md` becomes a top-level document and
`guides/install.md` appears under a **guides** group.

## Content and front matter

`MarkdownPostReader` reads `*.md` files recursively and orders them by date
(descending), then by slug. Each file starts with YAML front matter:

```markdown
---
title: "Welcome"
date: "2026-01-02T09:00:00Z"
summary: "A short description used in listings and metadata."
sidebar_position: 1
sidebar_label: "Start here"
draft: false
publish_from: "2026-01-01T00:00:00Z"
publish_until: "2027-01-01T00:00:00Z"
environments:
  - Production
tags:
  - intro
sources:
  - type: feed
    name: Example Blog
    url: https://example.com/feed.xml
---

Body written in Markdown.
```

### Schema

| Field     | Type            | Required | Notes                                                                 |
| --------- | --------------- | -------- | --------------------------------------------------------------------- |
| `title`   | string          | Yes      | Post title. Used in listings, the `<title>`, and metadata.            |
| `date`    | string (ISO 8601) | Yes    | Publication date and time. Parsed as a `DateTimeOffset`.              |
| `summary` | string          | By default | Short description used in listings, the feed, and `og:description`. Required by the default `RequiredSummaryValidator`; replace the validator to change this. |
| `sidebar_position` | integer | No | Docs navigation order. Lower values come first; unspecified documents are ordered by label and path. |
| `sidebar_label` | string | No | Docs navigation label. Falls back to `title`. |
| `draft` | boolean | No | When `true`, excludes the post from generated pages and every derived artifact. Defaults to `false`. |
| `publish_from` | string (ISO 8601) | No | Inclusive publication start evaluated against the resolved build timestamp. |
| `publish_until` | string (ISO 8601) | No | Exclusive publication end evaluated against the resolved build timestamp. |
| `environments` | list of strings | No | Allowed generation environments, compared case-insensitively. An empty list allows every environment. |
| `tags`    | list of strings | No       | Free-form tags. Drive the tags page and client-side search.           |
| `sources` | list of objects | No       | Provenance for the post. See below.                                   |

Each entry in `sources` has:

| Field  | Type   | Required | Notes                                                                   |
| ------ | ------ | -------- | ----------------------------------------------------------------------- |
| `type` | string | No       | A free-form label the calling app interprets. There is no fixed set; common conventions are `feed`, `article`, `repo`, `doc`, or `release`. |
| `name` | string | No       | Human-readable name of the source.                                      |
| `url`  | string | No       | Link to the source.                                                     |

`sources` is parsed and exposed on `MarkdownPost.FrontMatter`, but the core
generator does not render it. Surface it yourself through a `SiteExtraPage` or a
custom layout if you want a sources list on the site. `title` and `date` are
validated when posts are read; `summary` is checked during `SiteGenerator.Validate`.

## Customization

`SiteCustomization` is the one-way extension point. LithoSharp never references your
application code; you push behavior in through these members:

- `Text` — UI strings. `SiteText.English` and `SiteText.Japanese` are included.
  Search status messages are templates that use `{count}`, `{tag}`, `{query}`, and
  `{shown}` placeholders, so the client-side search reads in the configured language.
- `Template` — the rendering contract. `DocsSiteTemplate` is the default. Set
  `new BlogSiteTemplate()` to retain the legacy blog URLs, posts, RSS, search, and
  sitemap.
- `Theme` — `SiteThemeOptions` with `BrandPrefix`, `ThemeColor`,
  `DefaultSocialSubtitle`, and `AdditionalCss`. The default markup and CSS are kept
  as-is; CSS from `AdditionalCss` is appended last and wins, so you can override
  `:root` variables and selectors without touching the markup.
- `Validators` — your own `IContentValidator` instances. When empty, only the
  default `RequiredSummaryValidator` runs.
- `ExtraPages` — additional pages emitted alongside the standard ones.
- `FaviconSourceDirectory` — where to read favicon assets from. When omitted,
  a `favicon` directory next to the executable is used if present.
- `GenerateLlmsTxt` — opt in to writing an `llms.txt` summary (off by default).

### Selecting the Blog template

```csharp
var customization = new SiteCustomization
{
    Template = new BlogSiteTemplate()
};
```

### Writing a custom template

Implement `ISiteTemplate` to generate HTML and text assets. `SiteTemplateContext`
provides rendered pages, headings, previous/next links, and a directory-based
navigation tree. Use `RenderDocument` when the standard metadata, header, and footer
fit your layout, and `RenderTableOfContents` to render the supplied page headings.
LithoSharp validates every returned path, writes it safely beneath the output
directory, and rejects a collision with common artifacts such as `llms.txt`, favicon
files, and social images.

```csharp
public sealed class LandingTemplate : ISiteTemplate
{
    public Task<SiteTemplateResult> RenderAsync(
        SiteTemplateContext context,
        CancellationToken cancellationToken = default)
    {
        var page = context.Pages[0];
        var body = $"<h1>{Html.Encode(page.Post.FrontMatter.Title)}</h1>{page.ContentHtml}";
        return Task.FromResult(new SiteTemplateResult(
        [
            new SiteTemplateFile
            {
                RelativePath = "index.html",
                Content = context.RenderDocument(new SiteTemplateDocument
                {
                    Title = context.Site.Title,
                    RelativePath = "index.html",
                    BodyHtml = body
                })
            },
            new SiteTemplateFile
            {
                RelativePath = "assets/site.css",
                Content = "body { font-family: sans-serif; }"
            }
        ]));
    }
}

var customization = new SiteCustomization { Template = new LandingTemplate() };
```

### Generated collection pages

`ContentCollection<TFrontMatter, TBody>.GeneratePages<TPageContent>` builds
typed tag, year, category, or custom index pages after publication filtering:

```csharp
var tagPages = articles.GeneratePages(
    new ContentCollectionId("article-tags"),
    entry => entry.FrontMatter.Tags,
    group => new SitePage<TagIndex>(
        new PageId($"tag:{group.Key}"),
        SiteRoute.ForDirectoryIndex($"tags/{group.Key}"),
        new TagIndex(group.Key, group.Entries),
        new PageMetadata($"Articles tagged {group.Key}")),
    (page, context) => context.RenderDocument(RenderTagIndex(page.Content)),
    transformationId: new ContentTransformationId("tag-index:v1"),
    isCacheable: true);
```

Register the returned `SiteContentCollection` in
`SiteGenerationOptions.ContentCollections`. Keys use NFC normalization,
case-sensitive identity, and deterministic ordering. See
[typed Markdown collections](docs/typed-markdown-collections.md) for empty
groups, dependency declarations, diagnostics, and integration rules.

The Docs sample contains a complete Markdown collection and aggregate-page flow
under `typed-content`. It runs the same deterministic build twice, passes the
first `BuildPlan` as `PreviousBuildPlan`, and prints the resulting
`BuildReport`. The legacy Blog sample remains unchanged for compatibility.

JSON and CSV use the same registration contract. A focused JSON loader can map
an object body without adding another site model:

```csharp
using System.Text.Json;

var loader = new JsonContentCollectionLoader<ArticleFrontMatter, JsonElement>(
    "data/articles",
    new ContentCollectionId("json-articles"),
    new ReflectionContentFrontMatterBinder<ArticleFrontMatter>(),
    element => element.Clone(),
    entry => SiteRoute.ForFile($"articles/{entry.Id.Value}.html"),
    entry => new PageMetadata(entry.FrontMatter.Title, entry.FrontMatter.Summary));
var loaded = await loader.LoadAsync(cancellationToken);
```

## Public API

- `new SiteGenerator().GenerateAsync(SiteSettings site, IReadOnlyList<MarkdownPost> posts, string outputDirectory, bool clean, SiteCustomization? customization = null, CancellationToken ct = default)`
- `new SiteGenerator().GenerateWithOptionsAsync(SiteSettings site, IReadOnlyList<MarkdownPost> posts, string outputDirectory, bool clean, SiteCustomization? customization, SiteGenerationOptions options, CancellationToken ct)`
- `static SiteGenerator.Validate(SiteSettings site, string contentDirectory, IReadOnlyList<MarkdownPost> posts, SiteCustomization? customization = null)`
- `new MarkdownPostReader().ReadAllAsync(string contentDirectory)`
- `SiteCustomization`, `SiteGenerationOptions`, `SiteThemeOptions`, `SiteText`, `SiteExtraPage`
- `SiteContentCollection<TFrontMatter, TBody>`, `ContentPageRenderingContext`,
  `ContentPageGroup<TFrontMatter, TBody>`, `GeneratePages<TPageContent>`
- `ISiteTemplate`, `SiteTemplateContext`, `SiteTemplateResult`, `SiteTemplateFile`,
  `SiteTemplatePage`, `SiteTemplatePageLink`, `SiteTemplateHeading`,
  `SiteTemplateNavigationNode`, `SiteTemplateDocument`
- `DocsSiteTemplate`, `BlogSiteTemplate`
- `IContentValidator`, `ContentValidationContext`, `RequiredSummaryValidator`

Compatibility baselines are documented in the
[generated site compatibility contract](docs/compatibility-contract.md), its
[Japanese version](docs/compatibility-contract.ja.md), and the
[public API inventory and compatibility policy](docs/public-api-inventory.md).

Namespaces: `LithoSharp`, `LithoSharp.Configuration`, `LithoSharp.Content`,
`LithoSharp.Validation`, `LithoSharp.Search`.

For reproducible output, set `SiteGenerationOptions.BuildTimestamp`. When it is
not set, LithoSharp uses a valid `SOURCE_DATE_EPOCH` Unix timestamp, then falls
back to the current UTC time. An invalid `SOURCE_DATE_EPOCH` stops generation
with an error instead of silently using the current time.

`SiteGenerationOptions.EnvironmentName` selects posts whose `environments`
front matter contains that name. The default is `Production`; posts without an
`environments` restriction remain publishable in every environment. Draft,
future, expired, or environment-mismatched posts are removed before templates
receive content, so they do not appear in navigation, indexes, search, RSS,
sitemaps, `llms.txt`, or per-post social images.

### Routes and diagnostics

`SiteRoute` represents both a public URL and its physical output path. Use
`ForFile` for the existing `.html` convention or `ForDirectoryIndex` for a
trailing-slash URL backed by `index.html`:

```csharp
var file = SiteRoute.ForFile("guides/install.html", site.BaseUrl);
var directory = SiteRoute.ForDirectoryIndex("guides", site.BaseUrl);

Console.WriteLine(file.PublicPath);          // /product/guides/install.html
Console.WriteLine(directory.RelativeOutputPath); // guides/index.html
```

The generator validates all page, template, and shared-artifact routes before
writing output. Duplicate, case-only, reserved, unsafe, or file/directory
ancestor conflicts throw `SiteRouteValidationException`; inspect
`exception.Diagnostics` for stable IDs and source locations. See the
[route and generated-site compatibility contract](docs/compatibility-contract.md)
and its [Japanese version](docs/compatibility-contract.ja.md) for details.

`SiteGenerationResult.PostCount` is the number of published Markdown posts
generated in that run. With `clean: false`, LithoSharp preserves unrelated
files but removes files recorded as generator-owned by the previous successful
run when they are absent from the current validated build plan. The ownership
manifest is committed atomically with the output. Trees created before this
manifest existed are preserved because their ownership cannot be established
safely.

### Output safety and portability

Generation validates every route and completes rendering before an atomic
same-user output transaction. The lock and transaction metadata exclude
cooperating processes running as the same user; this is not a defense against a
privileged administrator, another user with directory access, or a filesystem
without the required atomic rename semantics. Ownership is recorded in a
restricted sibling sidecar. Windows owner/group/DACL and Unix permission-mode
preservation are supported where exposed by the platform, but Unix ACLs,
extended attributes, and ownership cannot be preserved portably. The sidecar
is committed atomically; stale or pre-sidecar trees are retained rather than
guessed at, and failed cleanup may leave a recovery registration for the next
run.

Route and group identity are NFC-normalized and case-sensitive. Output paths
use `/` separators on every platform, while reserved names and case-only
collisions are rejected before mutation. Typed-loader input errors are returned
as diagnostics with stable IDs and source locations; configuration and
file-system failures remain exceptions. The migration path is to add typed
collections beside legacy posts and move one collection at a time. No physical
NuGet package split is made in 0.2 because the current API and dependency
boundary is one coherent assembly.

## Static content source generator

`LithoSharp.Generators` handles explicitly declared Markdown files at compile time.
Add the analyzer package, mark a top-level static partial class with
`StaticContentCollectionAttribute`, and attach collection, ID, and route metadata to
each `AdditionalFiles` item. It generates a front matter `Binder`, `SchemaJson`, and
the nested `Pages`, `Entries`, and `Ids` members.

```xml
<PackageReference Include="LithoSharp.Generators" Version="0.2.0" PrivateAssets="all" />

<AdditionalFiles Include="typed-content\**\*.md">
  <LithoSharpCollection>articles</LithoSharpCollection>
  <LithoSharpId>%(Filename)%(Extension)</LithoSharpId>
  <LithoSharpRoute>articles/%(Filename)/</LithoSharpRoute>
</AdditionalFiles>
```

```csharp
var binder = GeneratedArticles.Binder;
var pageUrl = GeneratedArticles.Pages.Typed_Content_First.GetUrl(site.BaseUrl);
GeneratedArticles.WriteJsonSchema("artifacts/articles.schema.json");

[StaticContentCollection(
    typeof(ArticleFrontMatter),
    typeof(ContentEntry<ArticleFrontMatter, string>),
    "articles",
    EmitJsonSchema = true)]
public static partial class GeneratedArticles;
```

Generated `PageRef<TPage>` and `ContentRef<TEntry>` values are immutable.
`WriteJsonSchema` exists only when `EmitJsonSchema` is true; `SchemaJson` is always
available. Invalid declarations, metadata, IDs, routes, YAML, or values produce
compile errors `LSG001` through `LSG006`. Moving or deleting an input removes its
generated member, so stale references fail normal C# compilation. Dynamic loader
inputs retain their existing runtime validation. See
[source generators](docs/source-generators.md) for setup and diagnostics.

## Layouts, components, safe HTML, and registered assets

Public-directory copies, declared CSS transforms, integrity hashes and cached
responsive image variants are described in [Assets and images](docs/assets-and-images.md).

For persistent page caching, bounded parallel rendering, and actual cache hit reports, see
[Incremental builds](docs/incremental-builds.md).
`LithoSharp.Images` adds PNG/JPEG/WebP and opt-in AVIF without requiring an external
tool for Core generation. Run the Docs sample with `--asset-demo --check` to try it.

Typed collections can use a layout class directly:

```csharp
// Register a loaded collection using the layout instead of a renderer delegate.
var registration = new SiteContentCollection<ArticleFrontMatter, string>(articles, new ArticleLayout());

sealed class ArticleLayout : IPageLayout<ContentEntry<ArticleFrontMatter, string>>
{
    public IHtmlContent Render(
        SitePage<ContentEntry<ArticleFrontMatter, string>> page,
        PageRenderingContext context) =>
        context.RenderDocument(page, context.RenderMarkdown(page.Content.Body));
}
```

`PageRenderingContext.Create(site)` and `ComponentRenderingContext.Create(site)` render
layouts and components without creating an output directory. Components implement
`ISiteComponent<TProps>` and return `IHtmlContent`; `context.Render(component, props)`
composes them. The built-in components cover breadcrumbs, table of contents, search,
flat navigation, previous/next links, Blog and Docs headers, footers, and document
head/SEO metadata. Their stable classes and script hooks are listed in the
[layout and CSS contract](docs/layout-css-contract.md).

Existing string renderers and `ISiteTemplate` implementations remain supported. A
legacy template can opt into a typed layout with
`SiteTemplateContext.RenderLayout(page, layout)`, which carries the template's site
settings, environment, and registered assets into the new rendering context.
`Create(site)` uses English text, the default theme, the `Production` environment, and
an empty asset registry. Use the generation-provided context when a standalone render
needs registered assets or customized text and theme.

The built-in layouts accept `SitePage<PageLayoutContent>`. `BlogPageLayout` supplies
the Blog shell; `DocsPageLayout` also places the optional `Sidebar` and
`TableOfContents` regions. `OpenGraphType`, `SocialImageUrl`, and
`IncludeBlogNavigation` control the corresponding document options.

```csharp
var page = new SitePage<PageLayoutContent>(
    new PageId("preview"),
    SiteRoute.ForFile("preview.html", site.BaseUrl),
    new PageLayoutContent(new HtmlText("Preview")),
    new PageMetadata("Preview"));

var html = new BlogPageLayout()
    .Render(page, PageRenderingContext.Create(site))
    .ToHtmlString();
```

For a Docs sidebar, render `DocsNavigationComponent` from a preordered
`SiteTemplateNavigationNode`, optional additional `NavigationLink` values, and the
current `SiteUrl`, then assign the result to `PageLayoutContent.Sidebar`.

All layout and component entry points reject a null implementation or required input.
`ComponentRenderingContext.Render`, `SiteTemplateContext.RenderLayout`, and the typed
collection adapter throw `InvalidOperationException` when an implementation returns
null. The built-in layouts also reject a null page, context, content, or body.
`DocsNavigationComponent` rejects null props, root, or additional-link list.
`RenderMarkdown` returns trusted HTML from LithoSharp's configured Markdown
pipeline; callers must use `Html.UnsafeRaw` explicitly for any other trusted raw HTML.

Use `HtmlText` for element text and `HtmlAttributeValue` inside quoted HTML attributes.
`SiteUrl.ForFile("guide.html", site.BaseUrl)` validates internal paths;
`SiteUrl.FromAbsolute` accepts HTTP(S) URLs. Convert URLs with `ToAttributeValue()`
before inserting them into an attribute. `Html.UnsafeRaw` explicitly trusts HTML;
it does not sanitize it. These types do not make JavaScript or CSS interpolation safe.

```csharp
var asset = new SiteAsset("guide", contentRoot, "guide.pdf", "downloads/guide.pdf");
var options = new SiteGenerationOptions { Assets = [asset] };
// Inside a template or content renderer:
var link = $"<a href=\"{context.Assets.GetUrl(asset).ToAttributeValue()}\">{new HtmlText("Guide & reference")}</a>";
```

The registry snapshots file bytes and uses their SHA-256 in the output filename.
URLs include the `BaseUrl` subpath. Assets participate in route conflict checks,
build dependencies and transactional output ownership. Pass the same `SiteAsset`
instance to registration and lookup; an unregistered reference fails with `LSA001`.
Invalid paths, unsafe URLs and source files that escape their input root are rejected.
The [Docs sample](samples/LithoSharp.DocsSample/Program.cs) publishes a downloadable
Markdown source through this API. Existing string HTML APIs remain available.

## Notes on assets and fonts

Favicon and social-image sources are optional. Open Graph images are drawn with
SkiaSharp using system fonts: `SocialImageGenerator` looks for a preferred font
(such as Consolas) and falls back to the default typeface. On hosts without those
fonts, rendering may differ and CJK glyphs can render as tofu.

## Build and test

Requires the .NET 10 SDK.

```powershell
dotnet restore LithoSharp.slnx --locked-mode
dotnet build LithoSharp.slnx --no-restore -c Release
dotnet test --solution LithoSharp.slnx --no-build -c Release
dotnet run --project samples/LithoSharp.DocsSample -- --output _site
dotnet run --project samples/LithoSharp.Sample -- --output _site
```

Tests use TUnit on the Microsoft.Testing.Platform runner.

## Versioning

LithoSharp follows SemVer. While the version is `0.x`, the public API may change.

## License

MIT. See [LICENSE](LICENSE). Third-party dependencies are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Build and serve C# site projects with [the CLI and site factories](docs/cli.md).
