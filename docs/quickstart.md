# Quick Start

[日本語](quickstart.ja.md)

Use the .NET 10 SDK (verified with 10.0.300). MDX additionally uses Node.js
24.13.0; Markdown-only sites do not use Node. The SDK supplies the ASP.NET Core
shared framework used by `serve`. Work in a new directory. Commands below target
0.3.0 after publication; candidate verification uses a local package source.

## Install and create Markdown Docs

```sh
dotnet new install LithoSharp.ProjectTemplates::0.3.0
dotnet tool install LithoSharp.Tool --version 0.3.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release --port 4317
```

On Windows use `.tools/lithosharp.exe`. Open `http://localhost:4317/`, then stop
the server with Ctrl+C. The template installation affects your template catalog;
the tool is local to `.tools`. For isolated candidate checks, the repository's
[template test](../eng/Test-Templates.ps1) uses a separate template hive, CLI home,
NuGet cache and local package source. It checks all four templates without
changing your installed templates or tools.

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
the default summary validator runs when validation is requested. The typed
documentation loader below has its own schema and does not require these legacy
date/summary fields.

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
products. See [migration](migration-0.3.md), [testing](testing.md),
[performance](performance.md) and [Known limitations](known-limitations.md).
