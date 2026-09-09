# MDX and documentation sites

`LithoSharp.Mdx` is an optional .NET 10 package. Existing Markdown sites do not
need Node.js or React. MDX sites compile with MDX 3.1.1, React 19.2.4 and esbuild
0.25.12 on Node.js 24.13.0. The worker dependency graph is locked in its
`package-lock.json`. Generated sites need only a static HTTP host.

## Start a site

```sh
dotnet new install LithoSharp.ProjectTemplates::0.3.1
dotnet tool install LithoSharp.Tool --version 0.3.1 --tool-path .tools
.tools/lithosharp new mdx MyDocs -o MyDocs
dotnet build MyDocs -c Release
.tools/lithosharp restore-mdx MyDocs/bin/Release/net10.0/worker
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release
```

On Windows the local executable is `.tools/lithosharp.exe`. Restore is an
explicit network operation using `npm ci --ignore-scripts --no-audit --no-fund`.
Normal builds never install packages. Restore project npm dependencies separately
when importing packages from the site's `node_modules`. TypeScript is transpiled;
run the project's TypeScript checker separately for type checking.

The template's `ISiteFactory` configures `DocumentationSite`. An equivalent
registration is:

```csharp
var docs = new DocumentationSite(new MdxOptions(projectDirectory)
{
    Cacheable = true,
    Hydration = "selective"
}) { Browser = new() };
docs.AddCollection(new("guide", [
    new("current", "en", Path.Combine(projectDirectory, "docs"), "guide"),
    new("current", "ja", Path.Combine(projectDirectory, "i18n/ja"), "ja/guide")
]) { UseMdx = true });
var options = new SiteGenerationOptions { Extensions = [docs] };
```

Dispose the extension after the final API build, or let the factory host dispose
it at the end of its watch session. A worker is reused across content rebuilds.
Changing C# rebuilds and reloads the factory. Failed builds retain the previously
published output.

## Author content

`MdxContentCollectionLoader<T>` reads `.mdx`. `IncludeMarkdown` explicitly opts
`.md` into MDX; the documentation preset does this when `UseMdx` is true.
Underscore-prefixed files and directories are import-only partials. Front matter
uses the same strict C# binder as Markdown, including duplicate and unknown-key
diagnostics. `DocumentFrontMatter` supports stable `id`, `slug`, title,
description, sidebar metadata, tags, publication dates, `draft`, `unlisted`,
`search_exclude`, title/TOC visibility, edit URL and previous/next overrides.

```mdx
---
title: Interactive example
---
import Counter from './_components/Counter.tsx';

# Interactive example

This text is present before JavaScript runs.

<Counter initial={3} />
```

JS, JSX, TS, TSX, JSON, CSS, CSS modules, images and `?raw` imports use esbuild.
Imports must remain within the declared project or restored worker roots.
Published entries cannot import draft, excluded or unregistered documents;
move reusable modules into underscore-prefixed partials. MDX modules keep
their JavaScript export semantics. Only selected browser data is published.

Built-ins include `@theme/Tabs`, `@theme/TabItem`, `@theme/Admonition`,
`@theme/Details`, `@theme/CodeBlock`, `@theme/TOCInline`,
`@docusaurus/Link`, `@docusaurus/BrowserOnly`, `@docusaurus/useBaseUrl`,
`@docusaurus/useDocusaurusContext` and `@docusaurus/Translate`.
The preset provides GFM, heading anchors, directive admonitions, Prism code,
math with KaTeX and Mermaid. See the sample's content files for executable
examples, including browser-only dynamic imports and code inclusion.
Unsupported Docusaurus aliases fail explicitly; arbitrary Docusaurus plugins
and configuration execution are not supported.

## Browser ownership and public data

Page hydration is the compatibility default. React server rendering waits for
the initial tree, and `hydrateRoot` uses matching props and identifier prefixes.
C# owns the surrounding layout. Custom JavaScript must not rewrite React-owned
DOM. `BrowserOnly` provides a static fallback and supports a lazy module loader
so imports that access `window` need not run on the server.

`MdxPublicData` accepts JSON plus an explicit schema. Object schemas must set
`additionalProperties: false`; functions, services and arbitrary CLR objects
are not a browser data contract. Never select credentials or private settings.
The same limited JSON schema rules apply to island props.

Selective hydration requires explicit island boundaries:

```mdx
import Counter from './_components/Counter.tsx';

<Island component={Counter} props={{initial: 3}}
  schema={{type: 'object', properties: {initial: {type: 'integer'}}, additionalProperties: false}}
  strategy="visible" />
```

Strategies are `load`, `idle`, `visible`, `media` (with a media query), and
`manual`. Dispatch `lithosharp:hydrate` with the island ID to request a manual
island. Unknown interactive components, shared context and unsafe boundaries
use page hydration. Static-only pages omit their MDX hydration entry. Shared
dependencies within one `MdxSite` are bundled once. `StaticComponents` is a
trusted author assertion, not automatic proof of purity.

## Documentation, search and extensions

Document identity is collection × version × locale × stable ID. Register variants
explicitly; their route prefixes define the default and archived URLs. Labels,
banners, noindex, RTL and switch fallback IDs belong to each variant. Automatic,
manual and mixed sidebars share breadcrumbs and previous/next links.
`_category_.json` or YAML controls labels, ordering, collapse state and generated
category indexes. Unlisted documents remain directly accessible but do not
appear in navigation, search, tags, feeds or sitemaps.

`GitMetadata = true` explicitly reads the last committed date and author with
Git. Uncommitted files have no Git update metadata. Front-matter `last_update`
with `date` and `author` takes precedence. Git is required only for this option.
`SourceLocale` and `MissingDocuments` select translation completeness checks:
exclude missing documents and list their IDs in inspection, or fail with
`LSDOC001` before publication. Missing document bodies are not silently copied
or presented as translated content.

`TranslationCatalog` supplies shared C#/React UI messages with source, omit or
error policies. Locale-specific input directories hold translated documents and
assets. Alternate links include only published variants. Local search partitions
documents by collection, version and locale and indexes rendered text and
sections. Optional Algolia configuration accepts a public search-only key.

`MdxBlogSite` supports multiple blogs, author/tag/archive lists, pagination and
RSS, Atom and JSON Feed. Excerpts and feed text are explicit plain-text fields;
interactive trees are not cut in half. Independent React pages can be imported
by MDX wrappers and use the same typed loader and C# renderer.

For mixed sites, use `DocumentationSite.AddBlog` and `AddPages` so Docs, Blog and
independent pages use one compiler graph and shared React chunks. Register the
combined documentation extension once. Separate `MdxSite` instances are separate
graphs and should not target the same output asset namespace.

`ISiteBuildExtension.PrepareAsync` contributes typed collections and generated
assets to the common build graph and transaction. Declare dependencies and
ownership; never write the public output directly. Custom layouts use
`IPageLayout<PageLayoutContent>`. `ComponentsModule` overrides browser components;
`MdxPlugin` registers trusted Remark/Rehype modules and JSON options. Plugin
imports participate in dependency fingerprints. Dynamic reads must additionally
be listed in `DeclaredInputFiles`.

`AfterBuildAsync` observes committed results, including no-op builds, in
registration order. It must not modify owned outputs. Because it runs after
commit, an exception in this notification hook cannot roll back publication.

`inspect` reports worker versions, dependencies, chunk references, public props,
hydration decisions and work counts. The CLI also offers explicit dependency
restore, version snapshots, translation extraction and a Docusaurus migration
report; use `lithosharp --help` for exact command arguments. Migration reads files
without executing JavaScript configuration and reports unsupported constructs.

`ApiReferenceSite` reads XML API documentation and OpenAPI 3 JSON into typed
collections, resolves exact member IDs and generates selected version diffs.
External OpenAPI references are diagnosed instead of fetched.

For a trusted project, explicitly call `DocumentationVerification.BuildApiAsync`
after restore, then register the selected XML file with `AddXml`. Use
`DocumentationVerification.VerifyExamplesAsync` for an existing
Microsoft.Testing.Platform test project before importing its checked code region
with `CodeBlock`. These operations execute project code, have a ten-minute
timeout and do not restore dependencies. They are not automatic MDX build hooks.

## Optional browser features

`DocumentationBrowserOptions` enables responsive navigation, theme persistence,
search, announcements and progressive internal navigation. Ordinary anchors and
static text remain usable without JavaScript. The runtime disposes roots on
navigation, updates head/history/focus and falls back to ordinary navigation
when required resources cannot load.

`Offline = true` registers an opt-in service worker and webmanifest. The worker
installs a complete revision before activation and caches matching HTML and
assets. Deploy the entire output together. Retain old hashed assets while users
may still have an older page open. Service workers require HTTPS or localhost.

Live code runs in a sandboxed iframe with static code as its fallback. Its
runtime exposes React and a `render` function; it does not execute arbitrary MDX
imports or JSX compilation in the host document. Analytics is disabled by
default and sends no events until an explicit `lithosharp:consent` event grants
consent. Revocation stops subsequent events; normal and enhanced navigation use
the same deduplicated page-view path.

This consent flow belongs to `DocumentationBrowserOptions.Analytics`. It does not
control legacy Google Analytics snippets enabled by `SiteSettings.GoogleAnalyticsMeasurementId`
or the `GA_MEASUREMENT_ID` / `GOOGLE_ANALYTICS_MEASUREMENT_ID` environment variables.
Leave those unset when using the documentation analytics consent flow.

## Trust, reproducibility and diagnosis

MDX, React modules and compiler plugins are trusted build code, not a sandbox.
They can execute with the build account's permissions. Run untrusted repositories
in a separate restricted environment. The worker receives only explicitly
selected environment values and ordinary process necessities. `NODE_OPTIONS`
and executable search-path overrides are rejected.

Enable `Cacheable` only for code whose dynamic inputs and environment are declared.
Keep the build timestamp fixed for reproducible comparisons. Cache hits validate
input hashes; no-op builds avoid compilation, rendering and bundling. Changed
dependencies invalidate affected rendering; browser bundling may still process
all entries. C# allocation counters do not include Node memory. Worker timing
and heap-used snapshots are exposed separately and are not peak RSS measurements.

`MdxOptions.Timeout` defaults to two minutes per worker request. Increase it
explicitly for a large corpus; the 10,000-page benchmark uses fifteen minutes.

`LSMDX003` indicates a malformed worker result or invalid dependency graph;
`LSMDX005` indicates input changes during compilation; `LSMDX006` indicates a
forbidden document import. Source diagnostics retain original front-matter line
offsets. A timeout, cancellation or worker crash does not publish partial output.
Lockfile updates require explicit restore and renewed package/browser tests.
Generated third-party notices accompany bundled dependencies.

For repeatable checks, run worker `npm test`, the .NET integration tests,
`eng/Test-Templates.ps1`, and `tests/fixtures/mdx-browser/run.mjs` against the
generated MDX sample served on port 4317. Cross-platform CI configuration is
not evidence of a successful remote run; consult the recorded verification
results for the environments actually tested.
