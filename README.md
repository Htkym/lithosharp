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
var result = await new SiteGenerator().GenerateAsync(site, posts, "_site", clean: true, customization);

Console.WriteLine($"Generated {result.PostCount} post(s) into {result.OutputDirectory}.");
```

A runnable documentation example lives in
[`samples/LithoSharp.DocsSample`](samples/LithoSharp.DocsSample). The legacy blog
layout is shown in [`samples/LithoSharp.Sample`](samples/LithoSharp.Sample).

```powershell
dotnet run --project samples/LithoSharp.DocsSample -- --output _site
```

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

## Public API

- `new SiteGenerator().GenerateAsync(SiteSettings site, IReadOnlyList<MarkdownPost> posts, string outputDirectory, bool clean, SiteCustomization? customization = null, CancellationToken ct = default)`
- `static SiteGenerator.Validate(SiteSettings site, string contentDirectory, IReadOnlyList<MarkdownPost> posts, SiteCustomization? customization = null)`
- `new MarkdownPostReader().ReadAllAsync(string contentDirectory)`
- `SiteCustomization`, `SiteThemeOptions`, `SiteText`, `SiteExtraPage`
- `ISiteTemplate`, `SiteTemplateContext`, `SiteTemplateResult`, `SiteTemplateFile`,
  `SiteTemplatePage`, `SiteTemplatePageLink`, `SiteTemplateHeading`,
  `SiteTemplateNavigationNode`, `SiteTemplateDocument`
- `DocsSiteTemplate`, `BlogSiteTemplate`
- `IContentValidator`, `ContentValidationContext`, `RequiredSummaryValidator`

Namespaces: `LithoSharp`, `LithoSharp.Configuration`, `LithoSharp.Content`,
`LithoSharp.Validation`, `LithoSharp.Search`.

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
