# LithoSharp

[![build](https://github.com/Htkym/lithosharp/actions/workflows/build.yml/badge.svg)](https://github.com/Htkym/lithosharp/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/LithoSharp.svg)](https://www.nuget.org/packages/LithoSharp)

[English](https://github.com/Htkym/lithosharp/blob/main/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/README.ja.md)

Manage documentation consistency with C# types and builds. LithoSharp connects
your .NET team's content schema, generated references, compiler diagnostics and
checks of the generated site. Write product documentation and technical guides
in Markdown, add MDX/React when needed, and deploy ordinary static files.

Three common failures have different checks:

- A missing required title is reported against your C# schema during compilation (`LSG006`).
- A statically proven entry/route contradiction is reported by the analyzer (`LSA1402`).
- A missing internal target or heading in final HTML is reported by output quality checks (`LSQ001`/`LSQ002`).

These checks have separate scopes. Editor syntax feedback does not execute your
custom schema on every keystroke, and a valid typed reference does not certify
the final HTML. See the [adoption guide](https://github.com/Htkym/lithosharp/blob/main/docs/adoption-guide.md)
for fit, dependencies and evaluation examples.

This checkout describes the next minor candidate, not a published release.
Use the exact supplied candidate version/feed for the typed Docs workflow.
The published 2.1.3 Docs template uses the previous reader.
For an existing site, follow the [upgrade guide](https://github.com/Htkym/lithosharp/blob/main/docs/migration-2.0.md).

## Features

- Typed C# content collections, routes and references, with strict schema validation
  and optional Roslyn source generators and build diagnostics.
- Markdown and optional MDX 3 with React, build-time HTML, page hydration and
  selective hydration with explicit islands.
- Docs, Blog and independent pages, multiple Docs collections, versions, locales,
  sidebars and local search.
- Dependency graphs, persistent caches, invalidation reasons and artifact ownership
  for inspectable incremental builds.
- Staged output transactions and rollback before publication, with route and
  ownership validation.
- Structured link, anchor, canonical, redirect and SEO diagnostics.
- XML .NET API documentation, OpenAPI 3 JSON, exact xrefs and checked examples.
- .NET testing APIs for sites, routes, artifacts, DOM, components and layouts.
- Fingerprinted assets, responsive images and explicit extension points for C#
  rendering and trusted MDX plugins.

## Quick Start

With the .NET 10 SDK installed and the supplied candidate NuGet feed configured,
replace `<candidate-version>` with its exact version:

```sh
dotnet new install LithoSharp.ProjectTemplates::<candidate-version>
dotnet tool install LithoSharp.Tool --version <candidate-version> --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
# Before building, set MyDocs/MyDocs.csproj LithoSharpVersion to the same candidate
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release
```

On Windows the executable is `.tools/lithosharp.exe`. Set the generated project's
`LithoSharpVersion` to the same candidate before restore. Edit `GuideFrontMatter.cs`
and `MyDocs/content`; `Guides.Binder`, catalog routes and `RenderDocs` connect them
to the Docs shell. The factory enables Quality at Error without external HTTP.
Configure `DocsSiteFactory.cs`, build and check, then deploy `MyDocs/dist`.
For isolated installation, Markdown authoring, MDX and an interactive component,
follow the [complete Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md).

Existing applications can use `dotnet add package LithoSharp --version <candidate-version>`
and call the library directly; a CLI host is not required.

## Sample sites

Generated from the included factories. Both built-in templates support light/dark
switching, responsive menus and keyboard navigation. Set
`SiteThemeOptions.EnableThemeSwitching = false` to disable switching; see
[theme configuration](https://github.com/Htkym/lithosharp/blob/main/docs/layout-css-contract.md#theme-switching--テーマ切り替え).

| Docs — light | Blog — dark |
| --- | --- |
| [![Docs sample with sidebar, article and table of contents](https://raw.githubusercontent.com/Htkym/lithosharp/main/docs/images/docs-light.png)](https://github.com/Htkym/lithosharp/tree/main/samples/LithoSharp.DocsSample) | [![Blog sample with featured article and navigation](https://raw.githubusercontent.com/Htkym/lithosharp/main/docs/images/blog-dark.png)](https://github.com/Htkym/lithosharp/tree/main/samples/LithoSharp.Sample) |

## Examples

Follow the [typed content walkthrough](https://github.com/Htkym/lithosharp/blob/main/samples/LithoSharp.DocsSample/README.md#from-a-c-model-to-checked-output)
to generate a valid Docs site, change one front matter value to see a compiler
diagnostic, restore it, and inspect the generated HTML and links. The same C#
model connects the binder, typed references and site layout.

| Concern | Checked at | Limits |
| --- | --- | --- |
| Required fields and typed values | Generator/compiler | Explicit content inputs and statically known schema requirements. |
| Entry/route contradictions | Analyzer | Complete, current catalogs and known values; runtime values remain unproven. |
| Headings and source diagnostics | Editor/LSP | `NoContext` has no project binding; `Partial` analysis covers only available facts. |
| Unavailable header or body analysis | Generator/build/editor | `Unknown` or input/resource limits do not establish absence or validity; see the walkthrough for diagnostics and limits. |
| Internal URLs, anchors and HTML | Generated output inspection | Current output artifacts; source freshness and external HTTP responses require separate context/checks. |

Syntamark 1.2.0 owns Markdown syntax and raw source ranges. LithoSharp retains
C# schemas, routes, display policy and product diagnostics; the default profile
remains `lithosharp-markdown/1`. See the [adoption and limits](https://github.com/Htkym/lithosharp/blob/main/docs/migration-2.0.md#package-versions).

| Goal | Start here |
| --- | --- |
| Markdown Docs, typed content and images | [Docs sample](https://github.com/Htkym/lithosharp/tree/main/samples/LithoSharp.DocsSample) |
| Blog, feed and search | [Blog sample](https://github.com/Htkym/lithosharp/tree/main/samples/LithoSharp.Sample) |
| MDX, React islands and offline navigation | [MDX sample](https://github.com/Htkym/lithosharp/blob/main/samples/LithoSharp.MdxSample/README.md) |
| Versioning, i18n, Blog/Pages and API docs | [MDX guide](https://github.com/Htkym/lithosharp/blob/main/docs/mdx.md) and [sample map](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md#examples-and-next-steps) |

## Requirements

.NET 10 is required. The minimum supported SDK is 10.0.300. The CLI development
server also uses the ASP.NET Core shared framework provided with the SDK.
Markdown-only sites do not need Node.js or React. MDX requires Node.js 24.13.0
and explicit worker restore; the lockfile pins MDX 3.1.1, React 19.2.4 and
esbuild 0.28.2. Production output needs only a static HTTP host.

## VS Code extension

Install [LithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp) for Markdown/MDX diagnostics, heading navigation, build commands and site preview. See the [extension guide](https://github.com/Htkym/lithosharp/blob/main/extensions/lithosharp-vscode/README.md).

## Documentation

Use the [documentation index](https://github.com/Htkym/lithosharp/blob/main/docs/README.md) for an ordered reading path, package choices and the full guide map.

- [Docs template settings](https://github.com/Htkym/lithosharp/blob/main/docs/docs-template.md), [existing output checks](https://github.com/Htkym/lithosharp/blob/main/docs/generated-output-checks.md)
- [CLI and site factories](https://github.com/Htkym/lithosharp/blob/main/docs/cli.md), [MDX and documentation sites](https://github.com/Htkym/lithosharp/blob/main/docs/mdx.md)
- [Typed Markdown collections](https://github.com/Htkym/lithosharp/blob/main/docs/typed-markdown-collections.md),
  [typed YAML](https://github.com/Htkym/lithosharp/blob/main/docs/typed-yaml-collections.md), [source generators](https://github.com/Htkym/lithosharp/blob/main/docs/source-generators.md)
- [Build graph (Japanese)](https://github.com/Htkym/lithosharp/blob/main/docs/build-graph.ja.md), [incremental builds](https://github.com/Htkym/lithosharp/blob/main/docs/incremental-builds.md)
- [Assets and images](https://github.com/Htkym/lithosharp/blob/main/docs/assets-and-images.md), [site quality](https://github.com/Htkym/lithosharp/blob/main/docs/site-quality.md)
- [Testing](https://github.com/Htkym/lithosharp/blob/main/docs/testing.md), [HTML/CSS contract](https://github.com/Htkym/lithosharp/blob/main/docs/layout-css-contract.md),
  [compatibility contract](https://github.com/Htkym/lithosharp/blob/main/docs/compatibility-contract.md)

## Upgrade to the 2.2 candidate

Keep selected packages, CLI and templates at the same supplied candidate version.
The 2.2 candidate changes newly created Docs projects; existing sites opt into
the typed loader/Docs adapter and Quality settings explicitly. Preserve URLs
through catalog route declarations and do not regenerate over existing files.
See the [upgrade guide](https://github.com/Htkym/lithosharp/blob/main/docs/migration-2.0.md).
From 1.x, also apply the Testing API change from `Document` to `Snapshot`.
Rebuild and check content diagnostics, final links and custom CSS. MDX is optional.

Notes for migrations between older releases are preserved in the
[upgrade history](https://github.com/Htkym/lithosharp/blob/main/docs/history/README.md).

## Known limitations

MDX and C# site code execute as trusted build code. HTML safety is not a code
sandbox. Large MDX rebuilds can still bundle many interactive entries; arbitrary
Docusaurus plugins, trimming and Native AOT are unsupported. Read
[Known limitations](https://github.com/Htkym/lithosharp/blob/main/docs/known-limitations.md) before choosing an execution environment.

## Performance

Performance results apply to the named package/source version, workload,
measurement interval and environment. Generation, Quality, MDX and editor checks
measure different work; no-op and changed-input builds also need separate results.
See the [measurement evidence, limits and reproduction](https://github.com/Htkym/lithosharp/blob/main/docs/performance.md).
No general speedup or migration-effort claim follows from a result for one fixture.

## Contribute and license

See [CONTRIBUTING](https://github.com/Htkym/lithosharp/blob/main/CONTRIBUTING.md) for build and verification commands.
MIT licensed; see [LICENSE](https://github.com/Htkym/lithosharp/blob/main/LICENSE) and [third-party notices](https://github.com/Htkym/lithosharp/blob/main/THIRD-PARTY-NOTICES.md).
