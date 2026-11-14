# Configure the Docs template

[日本語](docs-template.ja.md)

## Typed content and the Docs shell

The LithoSharp 3.0.0 `docs` project template uses `GuideFrontMatter`,
`Guides.Binder`, generated catalog routes and `RenderDocs`; it no longer needs
Blog-style dates. See [Quick Start](quickstart.md) for the exact generated files,
input rules, Quality setting and commands. Existing sites keep their reader and
rendering behavior until explicitly changed. These additions do not change the
Core default `Quality = null` or generic `RenderDocument` behavior.

For an already loaded typed collection, select presentation fields explicitly:

```csharp
var docs = collection.RenderDocs(
    entry => new DocsPagePresentation
    {
        Title = entry.FrontMatter.Title,
        Description = entry.FrontMatter.Description,
        Order = entry.FrontMatter.Order
    },
    (entry, context) => context.RenderMarkdown(entry.Body),
    rendererFingerprint: "docs-layout:v1",
    isThreadSafe: true);
```

This fragment assumes the template's `GuideFrontMatter` and a successfully loaded
`ContentCollection<GuideFrontMatter, string>`; import `LithoSharp.Content`.
Register the returned collection in `SiteGenerationOptions.ContentCollections`
and select `DocsSiteTemplate` in `SiteCustomization.Template`, as the supplied
factory does. The renderer returns the body, not a second full HTML document.
`context.RenderMarkdown` supplies the already compiled HTML/headings to the
shared Docs layout. The adapter does not parse Markdown a second time.

`DocsPagePresentation` also supports `ParentId`, `Version`, `Locale` and
`Unlisted`; the selector need not require those property names on your schema.
Routes, publication and navigation use the same resolved entries. Draft/excluded
entries do not reappear; unlisted pages and entries without
`DerivedSurfaces.Navigation` stay outside sidebar/previous-next navigation.
Navigation stays within the collection/version/locale. The standard template
uses the publication document key's version and locale when present; a conflicting
presentation selector is rejected. For typed entries, `Title`, `Description`
and `Order` supply labels, summaries and ordering. The legacy frontmatter names
described below apply to the legacy reader rather than fixing custom C# property names.
The standard template
owns the home, its start link and optional topic cards; do not register another
page at the home output path. Navigation metadata is shared without copying all
page bodies into the tree.

Use a stable renderer fingerprint only when it covers display selectors, body
rendering and captured settings, and declare any external dependencies. Set
`isThreadSafe` only when those callbacks can run concurrently. The same normal
collection cache/build-graph rules apply. Use the
[migration guide](migration-2.0.md#move-an-existing-docs-site-to-typed-content)
when retaining an existing site's URLs or schema.

## Error pages and Content Security Policy

Both built-in templates generate `404.html` with the regular navigation, theme
and a home link. Set `SiteSettings.BaseUrl` to the full publication URL,
including a project prefix such as `https://example.com/product/`. Error-page
links and assets then use `/product/` even when the requested URL is nested.
Customize `SiteText.NotFoundTitle`, `NotFoundDescription` and
`NotFoundHomeLinkLabel` for the error-page text. The page has `noindex` metadata
and is omitted from the generated search index and sitemap.

The generated file does not configure a production host or establish the HTTP
status of a missing URL. `lithosharp serve` returns this page with HTTP 404 and
honors the site's base path. GitHub Pages supports a `404.html` at the root of
its publishing artifact; see its [custom error-page instructions](https://docs.github.com/en/pages/getting-started-with-github-pages/creating-a-custom-404-page-for-your-github-pages-site).
Check a nested missing URL on the actual staging host and confirm the status,
page body, links and asset responses before deployment. A redirect or rewrite
to `index.html` returning HTTP 200 does not provide the same behavior.

For a policy that blocks inline JavaScript, opt into the external theme
restoration script:

```csharp
Theme = new SiteThemeOptions { UseExternalThemeRestoration = true }
```

The built-in templates then emit `assets/theme.js` and reference it before the
stylesheet without `defer`, so a saved theme is applied before rendering.
The default remains inline restoration for existing sites. Disabling
`EnableThemeSwitching` removes the restoration script and toggle. Custom
templates using `HeadComponent` must supply the external asset themselves.

For the built-in same-origin assets, start with this HTTP response header:

```http
Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'
```

The host supplies the header; this setting does not inject a policy or authorize
arbitrary custom HTML. Keep custom scripts and styles external, and adapt the
policy for explicitly enabled analytics, remote images, fonts or search
providers. The legacy Google Analytics snippet includes inline code and needs
its own CSP authorization or a custom external integration. MDX modules and
hydration assets use same-origin scripts; custom MDX components must also
respect the policy. The development reload script is external, and its
EventSource uses `connect-src 'self'`. See the [CSP specification](https://www.w3.org/TR/CSP3/)
for script and style source rules.

The existing browser checks accept `LS_BROWSER=chromium`, `firefox` or `webkit`.
For a CSP fixture, use `LS_CSP` with `tests/fixtures/mdx-browser/serve.mjs`, set
`LS_BASE_PATH=/product` for a prefixed site, and run
`node tests/fixtures/mdx-browser/delivery.mjs http://127.0.0.1:4317/product/`.
The other Docs, Blog, MDX and sub-path checks retain their navigation, search,
keyboard and hydration assertions. A WebKit result identifies that engine;
actual Safari acceptance requires a separate run on Safari. Generating these
files or passing unit tests does not establish browser or staging acceptance.

## Standard presentation settings

The standard Docs template works without extra settings. Enable only the features
your site needs:

```csharp
var customization = new SiteCustomization
{
    Text = SiteText.Japanese,
    Template = new DocsSiteTemplate
    {
        EnableHeaderSearch = true,
        CollapseTopics = true,
        ShowTopicCards = true,
        CompactHome = true,
        ShowStartLink = false
    },
    Footer = new SiteFooterOptions
    {
        Text = "Review this guide against the current product before use.",
        Links =
        [
            new NavigationLink("License", SiteUrl.FromAbsolute("https://example.com/license")),
            new NavigationLink("Contact", SiteUrl.ForFile("contact.html", "https://example.com/guide/"))
        ]
    }
};
```

Return this value as `SiteDefinition.Customization` from your `ISiteFactory`, or
pass it as `customization` to `GenerateWithOptionsAsync`. These template settings
change presentation; your content loader, C# schema and route declarations still
determine which pages exist. Start with the [Docs sample](../samples/LithoSharp.DocsSample/README.md)
for typed content and checked generation.

Use the site's actual base URL when constructing internal footer links, and emit
the corresponding page. Footer text and labels are escaped. Absolute links accept
HTTP or HTTPS through `SiteUrl.FromAbsolute`; file and directory links use the
normal validated route helpers. There is no raw-HTML footer setting. The generated
notice remains, and optional text and links follow it on every standard Docs or
Blog document. Omit `Footer` to retain the original footer. Reusable layouts can
pass the same data through `FooterComponentProps.AdditionalContent`.

| Setting | Default | Behavior |
| --- | --- | --- |
| `EnableSearch` | `false` | Generates the local search page, index, script, and sitemap. |
| `EnableHeaderSearch` | `false` | Also enables local search and places a GET `q` form in the header. |
| `CollapseTopics` | `false` | Uses native topic disclosures, initially opening the current page's ancestors. |
| `ShowTopicCards` | `false` | Displays top-level navigation topics and their descendants on the home page. |
| `CompactHome` | `false` | Reduces the home introduction's padding and heading size. |
| `ShowStartLink` | `true` | Shows the start link when there is a first documentation page. |

Header search uses `SiteText` labels and the site's base path. The search page
keeps its own input and omits the header form. The header form uses `aria-label`
and no fixed input ID so that content headings can keep their IDs. Search still
requires JavaScript on the results page. With JavaScript disabled, navigation and
native topic disclosures remain usable. A search header scrolls with the page
without JavaScript; with JavaScript it stays sticky and measures its wrapped height.

Topic overview links are separate from disclosure summaries. Closing a topic
hides its links until the reader opens it; it does not change routes, navigation
order, the current-page marker, table of contents, or previous/next links.
`DocsNavigationComponentProps.CollapseTopics` provides the same opt-in for a
custom layout using the built-in navigation component.

Cards use the existing ordered navigation tree. Each root child becomes a card;
an associated page supplies its overview link and plain-text front-matter
`summary`. Without that page, the topic name remains plain text and descendant
links remain available. Without a summary, the description is omitted. Nested
folders keep their existing order and hierarchy. No separate card catalog is
required. For example, `content/getting-started.md` supplies the overview for the
topic containing `content/getting-started/installation.md`.

## Choose the customization level

| Surface | Standard settings | Reuse in a custom layout | Own layout or template |
| --- | --- | --- | --- |
| Header | `EnableHeaderSearch` enables the standard search placement. | `SearchFormComponent` renders a form; `DocsHeaderComponent` renders the standard brand and menu. | Change header content, control order, or form placement. |
| Footer | `SiteCustomization.Footer` appends plain text and links. | `FooterComponent` accepts `FooterComponentProps.AdditionalContent`. | Replace the generated notice or footer structure. |
| Navigation | Front-matter `sidebar_position`/`sidebar_label` set order and labels; `CollapseTopics` adds disclosures. | `DocsNavigationComponent` renders the existing `SiteTemplateContext.Navigation` tree. | Supply a different tree or change navigation placement/structure. |
| Home | `ShowTopicCards`, `CompactHome`, and `ShowStartLink` adjust the standard home page. | `DocsPageLayout` accepts a body and optional sidebar/TOC through `PageLayoutContent`. | Provide different home content or arrange its sections differently. |

Use `IPageLayout<TPage>` for a custom typed page layout, or `ISiteTemplate` to own
the template's output. In a layout renderer, the supplied `context` carries the
site's text and theme. Components return `IHtmlContent` for placement in that layout:

```csharp
var search = context.Render(new SearchFormComponent(),
    SiteUrl.ForFile("search.html", context.Site.BaseUrl));

var footer = context.Render(new FooterComponent(),
    new FooterComponentProps(context.Site.Title, Docs: true)
    {
        AdditionalContent = new SiteFooterOptions { Text = "See the license for terms of use." }
    });

var sidebar = context.Render(new DocsNavigationComponent(),
    new DocsNavigationComponentProps(navigation, [], currentUrl) { CollapseTopics = true });
```

Here `navigation` is the existing tree from `SiteTemplateContext.Navigation`, and
`currentUrl` is the current page's validated `SiteUrl`. The navigation component
keeps the supplied order. A search component does not generate search assets;
keep `EnableSearch` enabled when reusing it with the standard Docs output. Use
only one standalone search form per document and omit it on the search page.
`DocsHeaderComponent` has no search-placement argument, and topic cards have no
separate public component. Use the standard settings when those placements fit.

Use `SiteThemeOptions.AdditionalCss` for site-specific typography, colors, and
spacing. For other placement or structure, reuse the built-in layout/components
or implement `ISiteTemplate`; see the [layout contract](layout-css-contract.md).
These settings do not provide a theme editor.

When removing an AngleSharp-based HTML postprocessor, move required notices and
links into `Footer`, enable the needed Docs options, and remove only the code and
tests whose purpose was that postprocessing. Keep source/content checks and
checks of the output you distribute. Sites that retain custom HTML processing
must still maintain its dependency and validation. See the [upgrade
guide](migration-2.0.md) and [output inspection](generated-output-checks.md).
