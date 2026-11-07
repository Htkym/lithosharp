# Quick Start

[日本語](quickstart.ja.md)

Use the .NET 10 SDK (minimum supported version: 10.0.300). MDX additionally uses Node.js
24.13.0; Markdown-only sites do not use Node. The SDK supplies the ASP.NET Core
shared framework used by `serve`. Work in a new directory. Commands below target
NuGet version 2.1.3.

## Install and create Markdown Docs

```sh
dotnet new install LithoSharp.ProjectTemplates::2.1.3
dotnet tool install LithoSharp.Tool --version 2.1.3 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release --port 4317
```

On Windows use `.tools/lithosharp.exe`. Open `http://localhost:4317/`, then stop
the server with Ctrl+C. The template installation affects your template catalog;
the tool is local to `.tools`.

Add `MyDocs/content/hello.md`:

```markdown
---
title: Hello
date: 2026-01-02T09:00:00Z
summary: My first page.
---

# Hello

This is **Markdown** rendered by .NET.
```

Run the build again. The page is `MyDocs/dist/posts/hello.html`. Configure the
title, base URL and content directory in `MyDocs/DocsSiteFactory.cs`; set the real
deployment URL before publishing. The legacy reader requires title and date;
the default summary validator runs when validation is requested. The
[typed Markdown loader](typed-markdown-collections.md) uses your own C# schema;
the separate `DocumentFrontMatter` documentation preset does not require the
legacy date/summary fields.

## Try the typed-content workflow

The installed Docs template above is the small legacy-reader starting point.
For the complete typed workflow, open the repository's
[Docs sample walkthrough](../samples/LithoSharp.DocsSample/README.md#from-a-c-model-to-checked-output).
It connects `ArticleFrontMatter`, generated references and a binder, Markdown
inputs, editor navigation, site generation and checks of the emitted HTML.
Its invalid examples are separate from normal content.

| Check | Where it runs | Scope |
| --- | --- | --- |
| Required title or invalid date: `LSG006` | C# compilation | Explicit compiler inputs checked against the custom C# schema. |
| Proven reference contradiction: `LSA1402` | Analyzer | A complete, current (`Closed`) catalog; dynamic values are not proof. |
| Source diagnostics and section outline | Editor/LSP | The current Markdown snapshot. `NoContext` lacks project-bound checks; custom C# schemas are not executed per keystroke. |
| Incomplete or unavailable analysis | Parser/compiler/build | `Partial` and `Unknown` do not mean valid or absent. Unavailable header facts report `LSG002` in the generator or `LSM013` at runtime. |
| Missing output target/anchor: `LSQ001`/`LSQ002` | Generation quality checks or `check-output` | The actual inspected artifacts and anchors, not source freshness. |

LithoSharp 2.1.3 uses the Syntamark 1.2.0 runtime/Source pair and retains the
`lithosharp-markdown/1` profile. Syntamark supplies Markdown syntax and raw source
ranges; LithoSharp owns C# schemas, routes, rendering policy and diagnostics.
See [known limitations](known-limitations.md) for analysis budgets and trust boundaries.

## Add MDX and an interactive component

Create a separate MDX site using the installed tool and templates:

```sh
.tools/lithosharp new mdx MyMdx -o MyMdx
dotnet build MyMdx -c Release
.tools/lithosharp restore-mdx MyMdx/bin/Release/net10.0/worker
.tools/lithosharp build MyMdx -c Release
```

Worker restore is an explicit network operation and uses the packaged lockfile.
Normal site builds do not install npm dependencies. Restore project dependencies
separately if you add npm imports. Only build trusted MDX and components.

Create `MyMdx/content/_components/Counter.tsx`:

```tsx
import {useState} from 'react';

export default function Counter({initial = 0}: {initial?: number}) {
  const [count, setCount] = useState(initial);
  return <button onClick={() => setCount(count + 1)}>Count {count}</button>;
}
```

Create `MyMdx/content/counter.mdx`:

```mdx
---
title: Counter
---
import Counter from './_components/Counter.tsx';

# Counter

The heading and button are rendered at build time.

<Island component={Counter} props={{initial: 3}}
  schema={{type: 'object', properties: {initial: {type: 'integer'}}, additionalProperties: false}}
  strategy="load" />
```

The template selects `Hydration = "selective"`. This explicit island activates
on load; a plain `<Counter initial={3} />` uses page-hydration fallback.
TypeScript is transpiled; run a separate type checker if you need type checking.

```sh
.tools/lithosharp build MyMdx -c Release
.tools/lithosharp check MyMdx -c Release --format json
.tools/lithosharp serve MyMdx -c Release --port 4317
```

Open `http://localhost:4317/guide/counter/` and click **Count 3**. The button becomes
**Count 4**. The generated file is `MyMdx/dist/guide/counter/index.html`.
No Node process is needed to serve the production files.

## Production output

Stop the development server, set `SiteSettings.BaseUrl` to the deployment URL and
run the Release build. Deploy the complete `dist` directory to a static HTTP host,
excluding internal `.lithosharp-*` ownership metadata. Keep cache directories
outside published output. The development reload script is injected into server
responses, not written into production HTML. Preserve hashed assets needed by
older browser sessions when updating an existing deployment.

## Examples and next steps

| Goal | Existing example or verification fixture |
| --- | --- |
| Markdown, typed generator, aggregate pages, images | [Docs sample](../samples/LithoSharp.DocsSample/README.md), `--asset-demo --check` |
| Legacy Blog, feed and search | [Blog sample](../samples/LithoSharp.Sample/README.md) |
| React islands, progressive navigation, live code and PWA | [MDX sample](../samples/LithoSharp.MdxSample/README.md) |
| Multiple versions/locales, Blog/Pages and API reference | [MDX guide](mdx.md); [integration tests](../tests/LithoSharp.Tests/DocumentationTests.cs) provide executable fixtures |
| XML API builds and tested code examples | [documentation verification](../eng/Test-DocumentationVerification.ps1) |
| Custom C# layout | `lithosharp new empty MySite -o MySite` |

Tests are executable examples of advanced configurations, not additional template
products. See [upgrading to 2.1.3](migration-2.0.md), [testing](testing.md),
[performance](performance.md) and [Known limitations](known-limitations.md).
