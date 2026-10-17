# Migrating to 2.0 from 1.x

[日本語](migration-2.0.ja.md)

This guide covers 2.0.0 and its changes from 1.x. The README and [Quick Start](quickstart.md) installation examples target 1.1.0. Core packages and the VS Code extension have separate release channels; publishing Core does not publish the 0.2.0 extension to the Marketplace.

## Package versions

For the stable release, keep all selected LithoSharp packages at 2.0.0.

When an exact version and candidate feed are provided, keep Core, MDX, Tool, Testing, Generators and the other selected LithoSharp packages at that version. Optional Analyzers and CodeFixes must match it too. Candidate versions such as `2.0.0-ci.…` and `2.0.0-rc.…` differ from the `2.0.0` base version declared in the projects.

Templates default to `2.0.0`. When using their generated projects with a candidate feed, set the existing `LithoSharpVersion` property to the actual candidate version before restore. Install the Tool with that exact version and the feed's NuGet configuration as well. The following commands are absent from the published 1.1.0 Tool.

The Markdown runtime depends on the independent [Syntamark](https://github.com/Htkym/syntamark) component; Generators and Analyzers embed code from its corresponding source artifact. The candidate feed must also supply the Syntamark runtime selected by that candidate. Applications do not need a direct Syntamark.Source reference. Keep the fixed runtime/source pair together; do not substitute different versions or relabel it with the LithoSharp candidate version. The current fixed pair is Syntamark 1.0.0, published on NuGet. See [known limitations](known-limitations.md) for the supported Markdown syntax and MDX environment requirements.

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

Set the generated project's `LithoSharpVersion` to `2.0.0` (or the exact candidate version when using a candidate feed), then add this reference to the `ItemGroup` of the project that should receive diagnostics:

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
