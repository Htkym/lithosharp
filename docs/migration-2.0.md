# Upgrade to the 2.2 candidate

[日本語](migration-2.0.ja.md)

This checkout covers the next 2.2 candidate, including optional migration from
the published 2.1.3 Docs reader to typed Docs. It is not a published release.
Use the exact supplied candidate version/feed for the [Quick Start](quickstart.md).
The package base version is 2.2.0 and the matching extension candidate is 0.4.0;
Core and the extension have separate release channels. Publishing NuGet packages
does not publish an extension to the Marketplace.

For a 0.x site, also review the earlier behavior and dependency changes in the [upgrade history](history/README.md). Those documents retain their original version-specific instructions; use the exact candidate version supplied for this checkout. Compare generated output in a fresh directory before deploying it.

## Package versions

The published stable baseline is 2.1.3; keep that version together when staying
on the baseline. For this checkout's new typed Docs template, use the supplied
2.2 candidate version consistently across selected packages, Tool and Templates.

When an exact version and candidate feed are provided, keep Core, MDX, Tool, Testing, Generators and the other selected LithoSharp packages at that version. Optional Analyzers and CodeFixes must match it too. Suffixed candidate versions differ from the `2.2.0` package base version declared in the projects; use the actual supplied version, not an assumed published number.

This checkout's templates default to the `2.2.0` package base version. When using their generated projects with a candidate feed, set the existing `LithoSharpVersion` property to the actual candidate version before restore. Install the Tool with that exact version and the feed's NuGet configuration as well. The `preflight` and `migrate testing` commands described below are absent from the 1.1.0 Tool.

The Markdown runtime depends on the independent [Syntamark](https://github.com/Htkym/syntamark) component; Generators and Analyzers embed code from its corresponding source artifact. The candidate feed must also supply the Syntamark runtime selected by that candidate. Applications do not need a direct Syntamark.Source reference. Keep the fixed runtime/source pair together; do not substitute different versions or relabel it with the LithoSharp candidate version. See [known limitations](known-limitations.md) for the supported Markdown syntax and MDX environment requirements.

LithoSharp 2.1.3 uses the published Syntamark `1.2.0` runtime/Source pair from NuGet. Keep both artifacts at this exact version. The fixed feed contains the NuGet repository-signed packages; their payloads match the sealed formal release. See [release provenance](../eng/markdown/release-provenance.json) for the source identity and package hashes. The default profile remains `lithosharp-markdown/1`; updating the dependency does not select CommonMark or replace MDX body analysis.

Generator and splitter headers use the separate `frontmatter-scan/1` contract. It recognizes no body after a closing delimiter; its 1 Mi UTF-16 header budget excludes that body. `Closed` establishes delimiters and header Unicode, while YAML/schema validation remains in LithoSharp. `Unknown` input/resource failures report `LSG002` in the Generator or `LSM013` in runtime consumers, and never become missing or confirmed unterminated front matter. The header result has no body facts or document hash. The manifest fingerprint independently covers the original input and stays unavailable for invalid body Unicode.

Reader and collection-loader eager warnings describe the original body before includes and trimming, preserving its source positions. Bodies up to 1 Mi UTF-16 code units use the strict analysis limits; larger originals allow up to 8 Mi. Both cap nesting at 200. An original larger than 1 Mi can still shrink through includes or trimming before final compilation.

Final post and collection Markdown analysis uses explicit body input with the strict 1 Mi UTF-16 input limit; editor inspection supports 8 Mi. Parsed blocks survive later projection resource stops, but unavailable facts remain unavailable. Incomplete input or resource analysis reports `LSM013` Error: the reader retains that eager error, the collection loader fails, and generation stops before output is published. Reuse requires identical body text, source identity, parser, profile and strict analysis options. Changed snapshots and larger-tier original results require separate final-body analysis. Original and transformed warning snapshots stay separate, without merging or deduplicating by syntax ID. Transformed collection diagnostics have file-only locations unless exact original-body reuse proves their source mapping.

Generation reuses final-body compilation diagnostics when a post has no supplied reader diagnostics, including no-op builds. The post parse cache v2 persists only diagnostic-free results; warning-bearing posts use current analysis or an exact retained snapshot. Collection rendering captures diagnostics from each distinct Markdown string rendered through its page context. Verified collection artifact caches persist both warning-bearing and known-empty diagnostic outcomes and replay them on no-op builds. A missing outcome is unknown and forces rendering again.

## Move an existing Docs site to typed content

The next minor candidate changes newly created `docs` projects, not existing
sites. Keep the legacy `MarkdownPostReader` if its schema and routes fit. Do not
run the template over your working project. Review its files as a reference and
make selected changes in a branch; build to a separate output first.

| Concern | Legacy reader | New typed Docs template |
| --- | --- | --- |
| Input contract | `MarkdownPost` and reader rules; title/date requirements and optional summary validation | Your `GuideFrontMatter`: required title, optional description/order; no inherited date requirement |
| Compiler input | Not automatically a generated typed collection | Explicit `AdditionalFiles` for `content/**/*.md`, with matching exclusions and collection/id/route metadata |
| Runtime input | Existing reader and content directory | `MarkdownContentCollectionLoader<GuideFrontMatter>` rooted at `content`, using the generated binder |
| Entry ID | Existing post/reader conventions | Content-root-relative path including extension, normalized with `/` |
| Route | Keep the site's actual existing output rules | The new default is `guide/<relative-path-without-extension>/`; the loader gets it from `Guides.Catalog.GetRoute(entry.Id)` |
| Rendering and validation | Existing template and chosen checks | `RenderDocs` plus `DocsSiteTemplate`, with explicit Quality Error threshold and no external HTTP checks |

Keep old URLs by declaring their existing routes in the compiler input metadata
and obtaining those same routes from the generated catalog at runtime. You do
not have to adopt the new `guide/` prefix or directory-index style. Never create
a second independent route dictionary. Keep the template-owned home distinct
from content-page output paths; if old content owns `index.html`, resolve that
ownership explicitly before enabling the standard Docs home. Review redirects
only when a URL really must change.

Match root, recursive glob, underscore-prefixed exclusions and IDs in compilation
and runtime discovery. `reference/setup.md` must not collapse into `setup.md`.
Changing a filename changes its generated ID/reference symbol unless you
explicitly preserve the declaration. Add matching Core/Generators/Analyzers
references through `LithoSharpVersion`; make compiler packages `PrivateAssets="all"`.
Use the generated binder and selector rather than executing a custom schema in
the editor. `RequireStaticCoverage` remains an optional policy for supported
scopes, not a template-wide prerequisite.

Compare old/new routes, navigation, required metadata, HTML headings and links,
assets and custom CSS. Run the single [Docs CI sequence](quickstart.md#ci-for-this-docs-project)
on the new output before deployment. Retain the site's existing Quality threshold,
suppressions and external-link policy unless you choose to change them; the new
factory's explicit settings are not a global default change. Markdown needs no
Node; adding MDX still needs its explicit worker setup. See
[Docs settings](docs-template.md) for the adapter and [compatibility](compatibility-contract.md)
for output review boundaries.

## Docs settings and existing output

The new Docs settings are opt-in. Use the standard template's search, topic and home
options, and `SiteCustomization.Footer` for additional text and links; see
[Docs template settings](docs-template.md). When replacing HTML postprocessing,
retain the site's required notices and links. Remove an AngleSharp dependency only
when no remaining application code uses it.

Use `check-output` to inspect the distribution you will deploy without regeneration.
It reads current files but does not establish that they match current sources;
rebuild after source or configuration changes. See [existing output checks](generated-output-checks.md)
for the distinction from `check`, supported formats and inspection limits.

## Breaking Testing change

Replace `SiteTestDocument.Document` and third-party DOM dependencies with `Snapshot` and read-only queries. Binaries compiled against the 1.x DOM getter need updating too.

```csharp
using LithoSharp.Testing;

using var document = SiteTestDocument.Parse("<h1>Home</h1>");
var heading = document.Snapshot.Query("h1")?.TextContent;
document.AssertText("h1", "Home");
```

Move `QuerySelector` to `Snapshot.Query` and `QuerySelectorAll` to `Snapshot.QueryAll`. DOM mutation, third-party casts and custom extensions cannot be migrated with a blind rename: change the original HTML or renderer and parse again as needed. See the [Testing v2 contract and migration map](Testing-v2-api.md#migration) for selectors, exceptions and ownership. Full browser DOM/CSS equivalence is not guaranteed.

The Tool's `migrate testing --dry-run` reports uses from explicit C# sources and metadata references to the old 1.1.0 DLLs. It does not execute those DLLs, edit projects or sources, or remove user-owned package dependencies. Even a `ReadCandidate` preview requires review; `Manual` and `Unknown` are not automatic conversions. See the [command example and report format](Testing-v2-api.md#migration).

## HTML, assets and diagnostics

HTML snapshots, quality checks and supported static URL diagnostics share bounded parser code with declared support limits. Unsupported HTML and incomplete parses do not become complete validation. Enable final HTML quality checks with `SiteGenerationOptions.Quality`; see [site quality settings](site-quality.md).

Use the same asset declaration instances for registration, transforms and rendering. See [assets and images](assets-and-images.md) for `Assets`, `AssetTransforms`, `PublicDirectory` and path restrictions. Generation's `MaxDegreeOfParallelism` must be at least one and defaults to one. The analyzer's `LSA1005` also checks definite invalid values at registered generation calls.

`LithoSharp.Analyzers` is an optional compiler package. It checks definite misuse of registered APIs, supported HTML interpolation contexts and literal URLs, and references to verified catalogs. Definite absence requires a fresh Closed scope; Open, Stale, Unknown and unsupported values are not guessed. No diagnostic does not mean that every site link, anchor or asset was verified. The `LSA9001` coverage policy requires explicit opt-in.

Set the generated project's `LithoSharpVersion` to the exact supplied candidate version, then add this reference to the `ItemGroup` of the project that should receive diagnostics:

```xml
<PackageReference Include="LithoSharp.Analyzers" Version="$(LithoSharpVersion)" PrivateAssets="all" />
```

`LithoSharp.CodeFixes` is an optional package for a compatible IDE host. It offers fixes for verified direct string literals reported by `LSA1401`, subject to its documented conditions; not every diagnostic has a fix. See the [Analyzer guide](../src/LithoSharp.Analyzers/README.md) for rule scopes, severity and suppression, and the [CodeFix guide](../src/LithoSharp.CodeFixes/README.md) for fix conditions.

## CLI checks before rendering

After installing the matching Tool version, inspect explicit Markdown inputs with:

```sh
.tools/lithosharp preflight --mode static --input page.md --format json
```

Static mode does not run MSBuild, a site factory, plugins, bundling or SSR. MDX inputs require the existing explicit worker restore and matching Node/worker settings.

To compile a trusted project and inspect its factory/catalog, use:

```sh
.tools/lithosharp preflight site.csproj --mode trusted --input page.md --format json
```

Trusted mode executes project and factory/catalog code, but does not render or publish output. Coverage remains Partial; final HTML, dynamic processing and whole-site membership are outside this proof. Success means no Error in the reported phase, not permission for a later build. Runtime validation and final quality checks still apply to normal builds. See the [Tool guide](../src/LithoSharp.Tool/README.md#preflight) for the report format.
