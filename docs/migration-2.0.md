# Upgrade to 2.1.3

[日本語](migration-2.0.ja.md)

This guide covers upgrading an existing site to 2.1.3, including its changes from 1.x. The README and [Quick Start](quickstart.md) installation examples use 2.1.3. Core packages and the VS Code extension have separate release channels; publishing Core does not publish the 0.3.0 extension to the Marketplace.

For a 0.x site, also review the earlier behavior and dependency changes in the [upgrade history](history/README.md). Those documents retain their original version-specific instructions; the target package version for this guide is 2.1.3. Compare generated output in a fresh directory before deploying it.

## Package versions

For the stable release, keep all selected LithoSharp packages at 2.1.3.

When an exact version and candidate feed are provided, keep Core, MDX, Tool, Testing, Generators and the other selected LithoSharp packages at that version. Optional Analyzers and CodeFixes must match it too. Candidate versions such as `2.1.3-ci.…` and `2.1.3-rc.…` differ from the `2.1.3` base version declared in the projects.

Templates default to `2.1.3`. When using their generated projects with a candidate feed, set the existing `LithoSharpVersion` property to the actual candidate version before restore. Install the Tool with that exact version and the feed's NuGet configuration as well. The `preflight` and `migrate testing` commands described below are absent from the 1.1.0 Tool.

The Markdown runtime depends on the independent [Syntamark](https://github.com/Htkym/syntamark) component; Generators and Analyzers embed code from its corresponding source artifact. The candidate feed must also supply the Syntamark runtime selected by that candidate. Applications do not need a direct Syntamark.Source reference. Keep the fixed runtime/source pair together; do not substitute different versions or relabel it with the LithoSharp candidate version. See [known limitations](known-limitations.md) for the supported Markdown syntax and MDX environment requirements.

LithoSharp 2.1.3 uses the published Syntamark `1.2.0` runtime/Source pair from NuGet. Keep both artifacts at this exact version. The fixed feed contains the NuGet repository-signed packages; their payloads match the sealed formal release. See [release provenance](../eng/markdown/release-provenance.json) for the source identity and package hashes. The default profile remains `lithosharp-markdown/1`; updating the dependency does not select CommonMark or replace MDX body analysis.

Generator and splitter headers use the separate `frontmatter-scan/1` contract. It recognizes no body after a closing delimiter; its 1 Mi UTF-16 header budget excludes that body. `Closed` establishes delimiters and header Unicode, while YAML/schema validation remains in LithoSharp. `Unknown` input/resource failures report `LSG002` in the Generator or `LSM013` in runtime consumers, and never become missing or confirmed unterminated front matter. The header result has no body facts or document hash. The manifest fingerprint independently covers the original input and stays unavailable for invalid body Unicode.

Reader and collection-loader eager warnings describe the original body before includes and trimming, preserving its source positions. Bodies up to 1 Mi UTF-16 code units use the strict analysis limits; larger originals allow up to 8 Mi. Both cap nesting at 200. An original larger than 1 Mi can still shrink through includes or trimming before final compilation.

Final post and collection Markdown analysis uses explicit body input with the strict 1 Mi UTF-16 input limit; editor inspection supports 8 Mi. Parsed blocks survive later projection resource stops, but unavailable facts remain unavailable. Incomplete input or resource analysis reports `LSM013` Error: the reader retains that eager error, the collection loader fails, and generation stops before output is published. Reuse requires identical body text, source identity, parser, profile and strict analysis options. Changed snapshots and larger-tier original results require separate final-body analysis. Original and transformed warning snapshots stay separate, without merging or deduplicating by syntax ID. Transformed collection diagnostics have file-only locations unless exact original-body reuse proves their source mapping.

Generation reuses final-body compilation diagnostics when a post has no supplied reader diagnostics, including no-op builds. The post parse cache v2 persists only diagnostic-free results; warning-bearing posts use current analysis or an exact retained snapshot. Collection rendering captures diagnostics from each distinct Markdown string rendered through its page context. Verified collection artifact caches persist both warning-bearing and known-empty diagnostic outcomes and replay them on no-op builds. A missing outcome is unknown and forces rendering again.

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

Set the generated project's `LithoSharpVersion` to `2.1.3` (or the exact candidate version when using a candidate feed), then add this reference to the `ItemGroup` of the project that should receive diagnostics:

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
