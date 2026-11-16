# LithoSharp documentation

[English](README.md) | [日本語](README.ja.md)

These guides target LithoSharp 3.0.0, Syntamark 2 and VS Code extension 0.5.0.
They describe development targets; they do not establish that those releases are
published. The [version matrix](../eng/version-matrix.json) records the selected
runtime/Source pair and separate package/extension versions. Keep the LithoSharp
packages aligned at 3.0.0 when adopting that version.
Start with the Quick Start for requirements and installation, or the
[adoption guide](adoption-guide.md) to decide whether LithoSharp fits your site.

## Build your first site

1. [Quick Start](quickstart.md): install the SDK/tool/templates, create a Docs site,
   edit content, build, serve and check the generated output.
2. [Typed-content walkthrough](../samples/LithoSharp.DocsSample/README.md#from-a-c-model-to-checked-output):
   connect a C# model to generated references, Markdown, compiler diagnostics,
   editor feedback and checked HTML. Invalid and unproven examples stay outside normal content.
3. [Golden Path](golden-path.md): repeat the build, edit, diagnose, fix and check workflow.
4. [3.0 upgrade guide](migration-3.0.md): native YAML, image references, optional
   PNG processing, Markdown profiles and acquired editor evidence.

## Choose the content and presentation

- [Docs template](docs-template.md): sidebars, navigation, search, metadata and theme options.
- [Typed Markdown collections](typed-markdown-collections.md) and
  [typed YAML collections](typed-yaml-collections.md): models, binders, routes and strict input rules.
- [Source generators](source-generators.md) and [analyzers](../src/LithoSharp.Analyzers/README.md):
  declared catalogs and bounded compiler checks. Dynamic facts still need runtime validation.
- [MDX](mdx.md): React, islands, documentation collections, versions, locales and API documentation.
- [Assets and images](assets-and-images.md), [HTML/CSS contract](layout-css-contract.md):
  assets, transforms, responsive images and custom presentation.
- [Docusaurus migration](docusaurus-migration.md): inspect and convert supported inputs;
  arbitrary plugins and site code are not automatically migrated.
- Examples: [Docs](../samples/LithoSharp.DocsSample/README.md),
  [Blog](../samples/LithoSharp.Sample/README.md), [MDX](../samples/LithoSharp.MdxSample/README.md).

## Configure Markdown and editing

- [Markdown profiles](markdown-profiles.md): explicit profile selection, footnotes
  and registered typed components. Front matter and publication policy stay separate.
- [Semantic constraints](semantic-constraints.md): shared static rules and the
  catalog evidence needed for reference checks. Custom code and build-required
  decisions do not run on each editor change.
- [Reference editing](reference-editing.md), [C# rename previews](csharp-reference-rename.md):
  captured scope, version/hash/expected-text guards and unresolved manual work.
- [Code-example verification](code-example-verification.md), [proofreading](proofreading.md):
  explicitly requested execution and source-mapped findings; missing evidence is not success.
- [Open Knowledge Format](open-knowledge-format.md): bundle input rules, preservation
  and export. Ordinary documentation keeps its own metadata and publication policy.

## Diagnose, test and operate

- [Diagnostic reference](diagnostics.md): causes, detection phase, where to fix and analysis scope.

- [CLI](cli.md): factories, generation, preview, inspection, preflight and cache commands.
- [VS Code](../extensions/lithosharp-vscode/README.md) and [language server](language-server.md):
  diagnostics, section outlines, project context and trusted commands.
- [Testing](testing.md), [Testing API and migration map](Testing-v2-api.md):
  site fixtures, owned snapshots, supported selectors and migration examples.
- [Generated output checks](generated-output-checks.md), [site quality](site-quality.md):
  artifacts, HTML, links, anchors and optional external HTTP checks.
- [Incremental builds](incremental-builds.md), [build graph](build-graph.md):
  dependencies, cache reuse, invalidation reasons and output ownership.
- [Capabilities](tooling-capabilities.md), [compatibility contract](compatibility-contract.md),
  [known limitations](known-limitations.md): what each analysis can prove and which execution boundaries apply.
- [Performance](performance.md): reference times for a 100-page Docs site.

## Select packages

| Package or tool | Use it for |
| --- | --- |
| LithoSharp | Core site generation, Markdown, routes and content APIs |
| LithoSharp.Generators | Compile-time binders, schemas, typed references and explicit static catalogs |
| LithoSharp.Analyzers | Compiler diagnostics for supported static API, HTML and reference patterns |
| LithoSharp.CodeFixes | The documented Roslyn fixes, installed separately from diagnostics |
| LithoSharp.Images | Optional managed PNG transforms; other formats can be registered as existing assets |
| LithoSharp.Mdx | Optional MDX/React authoring and documentation features |
| LithoSharp.Testing | Site, output, DOM snapshot, component and layout assertions |
| LithoSharp.Tool | The command-line host |
| LithoSharp.ProjectTemplates | Templates used to create a new site |
| LithoSharp for VS Code | Editor commands and the bundled language server; a separate Marketplace release |

[Distribution](distribution.md) explains installation, package choices and the
separate NuGet and Marketplace versions.

## Languages and historical material

Most user guides have an English/Japanese pair linked at the top. The
[Japanese index](README.ja.md) links the Japanese collection, compiler,
language-server, Testing API, quality and layout guides. Code, API identifiers
and executable fixtures retain their original notation.

[Upgrade history](history/README.md) and the historical [2.x migration guide](migration-2.0.md)
preserve earlier instructions, including the 1.x Testing `Document` to `Snapshot`
migration. Use the 3.0 guide for the current upgrade.
See [Security](../SECURITY.md) for private vulnerability reporting and
[third-party notices](../THIRD-PARTY-NOTICES.md) for
redistributed components.
