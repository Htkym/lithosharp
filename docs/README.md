# LithoSharp documentation

[English](README.md) | [日本語](README.ja.md)

These guides describe the 2.1.3 NuGet package line and VS Code extension 0.3.0.
Keep the LithoSharp packages you use aligned; the extension has a separate version.
For installation prerequisites, start with the Quick Start. Versioned measurement
and development records describe their original revisions, not current guarantees.

## Build your first site

1. [Quick Start](quickstart.md): install the SDK/tool/templates, create a Docs site,
   edit content, build, serve and check the generated output.
2. [Typed-content walkthrough](../samples/LithoSharp.DocsSample/README.md#from-a-c-model-to-checked-output):
   connect a C# model to generated references, Markdown, compiler diagnostics,
   editor feedback and checked HTML. Invalid and unproven examples stay outside normal content.
3. [Golden Path](golden-path.md): repeat the build, edit, diagnose, fix and check workflow.
4. [Upgrade guide](migration-2.0.md): update an existing site, including the Testing
   `Document` to `Snapshot` migration when coming from 1.x.

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

## Diagnose, test and operate

- [CLI](cli.md): factories, generation, preview, inspection, preflight and cache commands.
- [VS Code](../extensions/lithosharp-vscode/README.md) and [language server](language-server.md):
  diagnostics, section outlines, project context and trusted commands.
- [Testing](testing.md), [Testing API and migration map](Testing-v2-api.md):
  site fixtures, owned snapshots, supported selectors and migration examples.
- [Generated output checks](generated-output-checks.md), [site quality](site-quality.md):
  artifacts, HTML, links, anchors and optional external HTTP checks.
- [Incremental builds](incremental-builds.md), [build graph (Japanese)](build-graph.ja.md):
  dependencies, cache reuse, invalidation reasons and output ownership.
- [Capabilities](tooling-capabilities.md), [compatibility contract](compatibility-contract.md),
  [known limitations](known-limitations.md): what each analysis can prove and which execution boundaries apply.
- [Performance](performance.md): dated workloads, measured conditions, limits and reproduction.

## Select packages

| Package or tool | Use it for |
| --- | --- |
| LithoSharp | Core site generation, Markdown, routes and content APIs |
| LithoSharp.Generators | Compile-time binders, schemas, typed references and explicit static catalogs |
| LithoSharp.Analyzers | Compiler diagnostics for supported static API, HTML and reference patterns |
| LithoSharp.CodeFixes | The documented Roslyn fixes, installed separately from diagnostics |
| LithoSharp.Images | Image transforms and responsive image support |
| LithoSharp.Mdx | Optional MDX/React authoring and documentation features |
| LithoSharp.Testing | Site, output, DOM snapshot, component and layout assertions |
| LithoSharp.Tool | The command-line host |
| LithoSharp.ProjectTemplates | Templates used to create a new site |
| LithoSharp for VS Code | Editor commands and the bundled language server; a separate Marketplace release |

[Distribution](distribution.md) explains stable installation, isolated candidate
feeds, package validation and the independent NuGet/extension release channels.

## Languages and historical material

Most user guides have an English/Japanese pair linked at the top. The collection,
compiler, language-server, Testing API, quality and layout references include
English-only or bilingual technical detail. The [Japanese index](README.ja.md)
marks those destinations; it does not imply a translation that does not exist.

[Upgrade history](history/README.md) preserves earlier migration instructions.
Files under `development/` and the versioned `contracts/` reference retain the
revision, evidence and scope they record. They are not the entry point for a new site.

For repository build, tests, screenshots and release checks, use
[Contributing](../CONTRIBUTING.md). See [Security](../SECURITY.md) for private
vulnerability reporting and [third-party notices](../THIRD-PARTY-NOTICES.md) for
redistributed components.
