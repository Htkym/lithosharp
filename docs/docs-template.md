# Configure the Docs template

[日本語](docs-template.ja.md)

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
must still maintain its dependency and validation. See the [2.0 migration
guide](migration-2.0.md) and [output inspection](generated-output-checks.md).
