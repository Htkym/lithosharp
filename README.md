# LithoSharp

[![build](https://github.com/Htkym/lithosharp/actions/workflows/build.yml/badge.svg)](https://github.com/Htkym/lithosharp/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/LithoSharp.svg)](https://www.nuget.org/packages/LithoSharp)

[English](README.md) | [日本語](README.ja.md)

A type-safe static site and documentation generator for .NET with C#, Markdown,
MDX, React and incremental builds. Build documentation alongside your .NET code,
validate content and links during generation, and deploy ordinary static files.

This branch prepares **0.3.0**. Installation commands below require its publication;
local candidate verification uses the generated NuGet packages instead.

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

With the .NET 10 SDK installed:

```sh
dotnet new install LithoSharp.ProjectTemplates::0.3.0
dotnet tool install LithoSharp.Tool --version 0.3.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release
```

On Windows the executable is `.tools/lithosharp.exe`. Edit `MyDocs/content` and
configure `DocsSiteFactory.cs`. Deploy `MyDocs/dist` to a static HTTP host.
For isolated installation, Markdown authoring, MDX and an interactive component,
follow the [complete Quick Start](docs/quickstart.md).

Existing applications can use `dotnet add package LithoSharp --version 0.3.0`
and call the library directly; a CLI host is not required.

## Examples

| Goal | Start here |
| --- | --- |
| Markdown Docs, typed content and images | [Docs sample](samples/LithoSharp.DocsSample) |
| Legacy Blog, feed and search | [Blog sample](samples/LithoSharp.Sample) |
| MDX, React islands and offline navigation | [MDX sample](samples/LithoSharp.MdxSample/README.md) |
| Versioning, i18n, Blog/Pages and API docs | [MDX guide](docs/mdx.md) and [sample map](docs/quickstart.md#examples-and-next-steps) |

## Requirements

.NET 10 is required; release preparation uses SDK 10.0.300. The CLI development
server also uses the ASP.NET Core shared framework provided with the SDK.
Markdown-only sites do not need Node.js or React. MDX requires Node.js 24.13.0
and explicit worker restore; the lockfile pins MDX 3.1.1, React 19.2.4 and
esbuild 0.25.12. Production output needs only a static HTTP host.

## Documentation

- [CLI and site factories](docs/cli.md), [MDX and documentation sites](docs/mdx.md)
- [Typed Markdown collections](docs/typed-markdown-collections.md),
  [typed YAML](docs/typed-yaml-collections.md), [source generators](docs/source-generators.md)
- [Build graph](docs/build-graph.ja.md), [incremental builds](docs/incremental-builds.md)
- [Assets and images](docs/assets-and-images.md), [site quality](docs/site-quality.md)
- [Testing](docs/testing.md), [HTML/CSS contract](docs/layout-css-contract.md),
  [compatibility contract](docs/compatibility-contract.md)

## Upgrade from 0.2.0

The tested legacy source and binaries work with 0.3.0. Output serialization,
missing-image metadata, search fingerprints and safety validation need review.
See the [migration guide](docs/migration-0.3.md) and [changelog](CHANGELOG.md).
Optional packages do not require an existing Markdown site to adopt MDX.
While versions remain 0.x, future minor releases may change public APIs.

## Known limitations

MDX and C# site code execute as trusted build code. HTML safety is not a code
sandbox. Large MDX rebuilds can still bundle many interactive entries; arbitrary
Docusaurus plugins, trimming and Native AOT are unsupported. Read
[Known limitations](docs/known-limitations.md) before choosing an execution environment.

## Performance

In the measured static-page fixture, selective hydration reduced JS from
449,585 to 15,817 bytes and hydration roots from one to zero. This does not
represent all island or fallback pages. The 10,000-page MDX corpus took 274.30 s
cold and 105.13 s for a no-op. See [conditions, limits and reproduction](docs/performance.md).
No equivalent competitor benchmark is claimed.

## Contribute and license

See [CONTRIBUTING](CONTRIBUTING.md) for build and verification commands.
MIT licensed; see [LICENSE](LICENSE) and [third-party notices](THIRD-PARTY-NOTICES.md).
