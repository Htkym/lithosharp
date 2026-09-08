# Generated site compatibility contract

This document describes the compatibility contract for legacy Markdown sites and
public APIs. Implementation details are not compatibility guarantees.

## API compatibility

Existing APIs remain available until a replacement and migration path exist.
Deprecation notices must identify the replacement, migration guidance and
intended removal version. `SiteGenerator.GenerateAsync`, `MarkdownPostReader`,
`SiteCustomization` and `ISiteTemplate` retain their supported entry points.

Public signatures are tracked in each library's `PublicAPI.Shipped.txt` and
`PublicAPI.Unshipped.txt`. Update these analyzer baselines with API changes.
CI also checks NuGet package compatibility against the published baseline.

## URL model

- Output paths use `/` as the public URL separator and are resolved beneath the
  configured output directory.
- Canonical URLs are `SiteSettings.BaseUrl` plus the relative artifact path.
  LithoSharp keeps the explicit `.html` suffix.
- Links inside HTML and the search index are root-relative and include the path
  component of `BaseUrl`. For example, a base URL of
  `https://example.com/product/` produces `/product/posts/intro.html`.
- A Markdown file keeps its directory below the content root:
  `guides/install.md` becomes `posts/guides/install.html`.
- The post social-image name is the lowercase SHA-256 hash of the normalized
  page output route. For example, `posts/guides/install.html` uses
  `assets/social/posts/efbbd97218682f5d9fb8a2b08c2e37bcb24ec6e32fb6a5ffb57b0cbadd1f9e8f.png`.
- Extra pages use `SiteExtraPage.RelativePath` unchanged for their output path,
  canonical URL, navigation link, and optional sitemap entry.

Legacy Markdown posts and extra pages can be adapted to `SitePage<TContent>`.
The `DocsRouteConvention` and `BlogRouteConvention` compatibility helpers both
use the existing relative `.html` output path and `SiteRoute.ForFile`; they do
not introduce directory-index URLs. Markdown page IDs use the
`markdown:{normalized-output-path}` prefix, while extra page IDs use
`extra:{normalized-output-path}`. Separators are normalized and Unicode is
normalized by the route and page ID types, so IDs do not depend on the host OS.
These adapters support the legacy public generator and template entry points.

Markdown front matter additionally accepts `draft`, `publish_from`,
`publish_until`, and `environments`. The timestamps are inclusive at the start
and exclusive at the end, and an empty environment list allows every
environment. At generation start, legacy Markdown posts and extra pages are
adapted to the common page model and filtered with the resolved build timestamp
and `SiteGenerationOptions.EnvironmentName`. The environment defaults to
`Production`. Extra pages have no publication conditions and are therefore
published by default.

Filtered Markdown posts are absent from template input, Docs navigation and
pagination, Blog listings, archives and tags, search data, RSS, sitemap,
`llms.txt`, and per-post social images. `SiteGenerationResult.PostCount` counts
only the Markdown posts that remain after this filter. Publication metadata is
validated before filtering. A published page with an invalid route produces an
`InvalidRoute` diagnostic with its source path, while an unpublished page does
not participate in route validation because it owns no generated route.

## Route value contract

`LithoSharp.Routing.SiteRoute` is the immutable route value used by the generator.
Legacy valid routes retain their public paths; see the
[0.3.0 migration guide](migration-0.3.md) for behavior changes from NuGet 0.2.0.

- `ForFile` maps a relative route such as `posts/intro.html` to public path
  `/posts/intro.html` and output path `posts/intro.html`.
- `ForDirectoryIndex` maps `guides/intro` to `/guides/intro/` and
  `guides/intro/index.html`. An empty path or `/` represents the site root and
  writes `index.html`.
- An absolute HTTP(S) `baseUrl` contributes only its normalized path. For
  example, `https://example.test/product/` prefixes the public path with
  `/product/` and never prefixes the output path.
- Relative route paths accept both `/` and `\` as input separators and collapse
  repeated separators. Public paths always use `/`, and output paths remain
  relative with `/` separators on every operating system.
- A `baseUrl` path follows URI path rules rather than output-file naming rules.
  Its escaped path and internal repeated separators are preserved, so URI path
  segments such as `CON`, `docs:v2`, and `app;v=1` remain unchanged in the
  public path. Literal backslashes, encoded separators, dot segments, a leading
  repeated separator, and malformed or ambiguous URI input are rejected.
- In relative route paths, literal Unicode and equivalent UTF-8 percent escapes
  have the same identity. Output segments use NFC Unicode, while public segments
  use canonical percent-encoding. Valid escapes such as `%20` are decoded for
  the output file name and re-encoded for the public path. Rendered URLs decode
  valid non-ASCII UTF-8 sequences for compatibility, but retain escapes for
  spaces and reserved or URI-unsafe ASCII.
- File routes preserve names such as `.html` and `index.html`. A file route
  ending in a slash is invalid; directory-index routes always expose a trailing
  slash and append `index.html` only to the output path.
- Query strings, fragments, relative or non-HTTP(S) base URLs, invalid percent
  escapes, invalid UTF-8 escapes, absolute route paths, rooted Windows paths,
  encoded separators, and `.` or `..` segments are rejected before an output
  path is exposed. Cross-platform unsafe or reserved file-name segments are
  rejected for relative routes that produce output files, not for `baseUrl`
  path segments.
- Equality and hashing compare the normalized public and output paths with
  ordinal, case-sensitive semantics and do not depend on host file-system
  casing.

  Before mutating the output directory, the generator completes template
  rendering and registers published page routes, built-in template artifacts,
  optional shared artifacts, and every file claimed by a custom template in one
  `SiteRouteTable`. Opaque custom templates reserve physical ownership only for
  files they return; omitted pages retain conceptual public-route uniqueness but
  do not create false output collisions. Every returned file must resolve to a
  `SiteRoute`; invalid files are diagnosed and excluded before validation fails.
  Duplicate, case-only, reserved-artifact, and file/directory ancestor conflicts
  fail with `SiteRouteValidationException`, which remains an
  `InvalidOperationException` for compatibility.

## Docs artifacts

The default `DocsSiteTemplate` emits:

| Artifact | Convention |
| --- | --- |
| Home | `index.html` |
| Markdown pages | `posts/{content-relative-path}.html` |
| Extra pages | `{SiteExtraPage.RelativePath}` |
| Stylesheet | `assets/site.css` |
| Script | `assets/site.js` |
| LLM summary | `llms.txt` when `GenerateLlmsTxt` is enabled |

By default, Docs output does not emit `archives.html`, `tags.html`, `search.html`,
`search-index.json`, `assets/search.js`, `feed.xml`, or `sitemap.xml`.
`EnableSearch = true` explicitly adds search artifacts.

## Blog artifacts

The explicit `BlogSiteTemplate` emits:

| Artifact | Convention |
| --- | --- |
| Home | `index.html` |
| Archives | `archives.html` |
| Tags | `tags.html` |
| Search page | `search.html` |
| Search data | `search-index.json` |
| Markdown pages | `posts/{content-relative-path}.html` |
| Extra pages | `{SiteExtraPage.RelativePath}` |
| Stylesheet | `assets/site.css` |
| Site script | `assets/site.js` |
| Search script | `assets/search.js` |
| RSS 2.0 feed | `feed.xml` |
| Sitemap | `sitemap.xml` |
| LLM summary | `llms.txt` when `GenerateLlmsTxt` is enabled |

The RSS channel link is the normalized absolute `BaseUrl`. Item `link` and
`guid` values are canonical post URLs, and the feed contains at most the first
20 posts in reader order.

The sitemap contains absolute canonical URLs in this meaningful order: home,
archives, tags, included extra pages in configuration order, search, then posts
in reader order. Only post entries have `lastmod`.

`search-index.json` contains `site`, the resolved `generated` timestamp, and
`documents`. Document URLs are root-relative site paths. `search.html` appends a
content-fingerprint query to the index URL. Neither timestamp nor fingerprint
value is a cross-release compatibility value.

## Shared optional artifacts

When every required favicon source file is present, both templates copy these
files beneath `assets/favicon/`:

- `favicon.ico`
- `apple-touch-icon.png`
- `android-chrome-192x192.png`
- `android-chrome-512x512.png`
- `favicon-16x16.png`
- `favicon-32x32.png`
- `favicon-48x48.png`
- `favicon-64x64.png`
- `favicon-96x96.png`
- `favicon-128x128.png`
- `favicon-180x180.png`
- `favicon-192x192.png`
- `favicon-256x256.png`
- `favicon-512x512.png`

The same condition emits `site.webmanifest`. If
`android-chrome-192x192.png` is present, social images are generated as
`assets/social/og-default.png` and
`assets/social/posts/{sha256(normalized-page-output-route)}.png`.

HTML references emitted default or post social images through absolute Open Graph
and Twitter Card URLs. When no image is emitted, image metadata is omitted and
the Twitter card uses `summary`. Favicon and manifest links are present only when
the complete favicon set is available.

With `clean: false`, the generator preserves unrelated files and removes files
recorded as generator-owned by the previous successful build when they are
absent from the current validated build plan. The deterministic ownership
manifest is committed atomically with the output and is not reported in
`SiteGenerationResult.GeneratedFiles`. Output trees created before the manifest
existed are preserved because their ownership cannot be established safely.

## Atomic-output and portability boundaries

The output transaction serializes cooperating same-user processes using a
conservative case- and normalization-folded lock alias. Ownership sidecars and
recovery registrations use the exact resolved directory identity instead, so
physically distinct names such as NFC and NFD directories never share ownership
state. This excludes a same-user cooperating race, not a privileged or hostile
administrator, another user with write access, or filesystems without the
required atomic rename guarantees. Windows owner/group/DACL and Unix permission
modes are preserved where available; Unix ACLs, extended attributes, and
ownership are not portable guarantees. A stale pending registration can be
retained for recovery after a partial cleanup failure.

Typed-loader input failures are structured diagnostics with normalized source
paths and optional line/column locations. Configuration and file-system
failures remain exceptions. Route and collection keys use NFC normalization,
ordinal case-sensitive identity, and forward-slash output paths; extension
authors must choose casing deliberately because case-only output collisions are
rejected before mutation.

## HTML compatibility

Compatibility covers semantic structure rather than serialized bytes.
Whitespace, indentation, attribute ordering, harmless wrapper changes, and
equivalent escaping are not contracts.

The following aspects are contracts:

- major landmarks and their meaningful order;
- the listed CSS classes and state classes used by built-in styling or scripts;
- canonical, navigation, pagination, table-of-contents, RSS, search, asset, and
  post links;
- document language, title, description, theme color, canonical URL, Open Graph,
  Twitter Card, article publication metadata, RSS discovery, favicon/manifest,
  stylesheet, and script metadata where applicable;
- accessibility relationships such as navigation labels, `aria-current`,
  menu-control IDs, search status regions, and `rel="prev"` / `rel="next"`;
- content/navigation ordering derived from Docs sidebar order or Blog post order.

Docs pages retain this major order:

1. `header.docs-header`
2. `div.docs-shell`
3. `aside.docs-sidebar`
4. `main.docs-main`
5. `article.docs-content`
6. optional `aside.post-toc.docs-toc`
7. `footer.docs-footer`

Docs navigation retains `docs-nav-list`, `docs-nav-folder`, `docs-nav-link`,
`is-ancestor`, and `is-current`. Post pagination retains `docs-pagination`,
`docs-pagination-previous`, and `docs-pagination-next`.

Blog pages retain this major order:

1. `header.site-header`
2. `main`
3. page-specific content
4. `footer.site-footer`

Blog navigation retains `site-nav-shell`, `site-nav`, `site-header-search`,
`site-menu-toggle`, and `rss-nav-link`. Post pages retain `post-layout`,
`article.post`, `post-title`, `summary`, `tags`, `post-toc`, `toc-nav`, and
`toc-list`. Listing/search structures retain `hero`, `eyebrow`, `card`,
`archive-row`, `archive-post-list`, `tag-cloud`, `tag-link`, `search-box`,
`search-scope`, `search-status`, and `search-results`.

This contract explicitly excludes byte-for-byte HTML compatibility and volatile
generation timestamps.
