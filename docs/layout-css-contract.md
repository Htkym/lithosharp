# Layout and component CSS contract

<a id="layout-and-component-css-contract--レイアウトとcssの契約"></a>

This contract applies to the built-in layouts in LithoSharp 3.0.0. It covers
semantic classes and interaction hooks, not byte-identical HTML or arbitrary
custom templates. See the [generated-site compatibility contract](compatibility-contract.md)
and the [empty template's custom layout](../templates/LithoSharp.ProjectTemplates/content/empty/EmptyLayout.cs)
when replacing the built-in shell.

The built-in components retain the existing Docs and Blog classes. Override colors
through `SiteThemeOptions.AdditionalCss`; use the existing `--accent`,
`--accent-contrast`, `--muted`, and `--border` variables. These C# layout components
do not require an additional stylesheet dependency or client framework.

`BlogPageLayout` owns the Blog document shell. `DocsPageLayout` owns the Docs shell
and accepts the rendered sidebar and table of contents through `PageLayoutContent`.
`DocsNavigationComponent` produces the sidebar classes below from a precomputed
`SiteTemplateNavigationNode`; it does not reorder the supplied tree or additional
links. `CurrentUrl` adds `is-current` and `aria-current`, and marks containing folders
with `is-ancestor`.

| Surface | Classes |
| --- | --- |
| Blog header and navigation | `site-header`, `brand`, `site-nav-shell`, `site-home-link`, `site-icon-button`, `site-nav`, `site-menu-toggle`, `rss-nav-link` |
| Blog footer | `site-footer` |
| Docs layout | `docs-body`, `docs-header`, `docs-brand`, `docs-shell`, `docs-main`, `docs-content`, `docs-footer` |
| Docs navigation | `docs-sidebar`, `docs-nav-list`, `docs-nav-folder`, `docs-nav-link`, `is-current`, `is-ancestor` |
| Table of contents | `post-toc`, `docs-toc`, `toc-nav`, `toc-track`, `toc-indicator`, `toc-list`, `toc-depth-1`, `toc-depth-2`, `toc-depth-3` |
| Previous and next | `docs-pagination`, `docs-pagination-previous`, `docs-pagination-next` |
| Header search | `site-header-search`, `browser-search`, `visually-hidden` |

Keep the existing `data-*` attributes, element IDs, `aria-controls`, and accessible
labels when replacing interactive chrome: the bundled scripts use them for menus
and table-of-contents behavior. Breadcrumbs render semantic navigation without a
new styling promise; custom layouts decide their placement and appearance.

The script-facing hooks are `site-menu`, `data-site-menu-toggle`, `data-site-nav`,
`docs-sidebar`, `data-docs-menu-toggle`, `data-docs-sidebar`, `post-toc-title`,
`data-toc-item`, and `data-toc-link`. The header search contract includes
`header-search-input`, its matching label, and the native GET fields `q` and
`type="search"`. `aria-current="page"`, `rel="prev"`, and `rel="next"` remain part
of navigation semantics.

The opt-in Docs header search described in [Docs configuration](docs-template.md)
uses `aria-label` without a fixed input ID and is omitted on the search page.
Standalone `SearchFormComponent` and the Blog header retain `header-search-input`.
Additional footer content uses `site-footer-additional`; topic disclosures and
cards use `docs-nav-topic`, `docs-topic-cards`, and `docs-topic-card`.

<a id="theme-switching--テーマ切り替え"></a>

## Theme switching

Docs and Blog follow the operating system until the reader selects a palette.
The choice is saved in local storage under `lithosharp-theme` for the same origin.
Without JavaScript the system palette still applies, but the toggle is hidden.

Set `SiteThemeOptions.EnableThemeSwitching = false` to omit the built-in theme
toggle and theme preference scripts. The default is `true`. When disabled, the
built-in stylesheet stops following the operating system palette and uses light
colors; set `AdditionalCss = ":root { color-scheme: dark; }"` for a fixed dark
theme. Custom templates that reuse the built-in header and head
components inherit this setting; custom controls must honor it explicitly.

```csharp
var customization = new SiteCustomization
{
    Theme = new SiteThemeOptions
    {
        EnableThemeSwitching = false,
        AdditionalCss = ":root { color-scheme: dark; }"
    }
};
```

Omit `AdditionalCss` for fixed light colors. Regenerate after changing the option;
incremental builds update the affected HTML, CSS and JavaScript. Saved choices
are ignored while switching is disabled. When switching is enabled, blocked storage still permits switching within the
current page.

Restoration uses an inline head script before CSS loads; a strict Content Security
Policy must allow that script. Palettes require CSS `light-dark()` support.
