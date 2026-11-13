# Quick Start

[日本語](quickstart.ja.md)

Use the .NET 10 SDK (minimum 10.0.300). Markdown-only Docs need no Node.js or
React. Optional MDX uses Node.js 24.13.0 and an explicitly restored worker.
The SDK supplies the ASP.NET Core shared framework used by `serve`.

This guide targets LithoSharp 3.0.0. It is a development target, not a claim that
3.0.0 is published. The [version matrix](../eng/version-matrix.json) records the
selected Syntamark 2 runtime/Source pair and the separate VS Code 0.5.0 target.
See [distribution](distribution.md) for package choices and candidate handling.

## Create typed Markdown Docs

Work in a new directory. Run these release-install commands only when the matching
3.0.0 Tool and Templates are available from your selected package source. For an
unpublished candidate, use its explicitly supplied packages and restore sources
as described in distribution; these commands do not locate or build a candidate.

```sh
dotnet new install LithoSharp.ProjectTemplates::3.0.0
dotnet tool install LithoSharp.Tool --version 3.0.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
```

On Windows use `.tools/lithosharp.exe`. Template installation changes your user
template catalog; the Tool is local to `.tools`. Generated projects already use
LithoSharp 3.0.0. Do not mix package versions or scaffold over an existing site.

## Edit the C# contract and Markdown

`GuideFrontMatter.cs` declares required `Title`, optional `Description` and
`Order`, and the `Guides` generated collection. The project references matching
Core, Generators and Analyzers packages, with the compiler packages private.
The factory calls `MarkdownContentCollectionLoader<GuideFrontMatter>.FromCatalog`
with `Guides.Catalog`, the project directory and `Guides.Binder`. This uses the
catalog's declared `InputRoot` and routes:

```csharp
var loaded = await MarkdownContentCollectionLoader<GuideFrontMatter>.FromCatalog(
    Guides.Catalog,
    context.ProjectDirectory,
    entry => new PageMetadata(entry.FrontMatter.Title, entry.FrontMatter.Description),
    frontMatterBinder: Guides.Binder,
    transformationId: new ContentTransformationId("docs-content:v1"),
    isCacheable: true).LoadAsync(cancellationToken);
```

The factory checks `loaded.IsSuccess` before using the collection, then calls
`RenderDocs` for the Docs shell and navigation. No date is required by this schema.

Edit `MyDocs/content/getting-started.md`:

```markdown
---
title: Getting started
description: A guide checked with the project build.
order: 1
---

# Getting started

This is **Markdown** rendered by .NET.

## First build

Build the site before distributing its output.
```

When adding a property to the C# schema, update the Markdown values and the
presentation selector if it affects display. The default template includes a
second valid page at `content/index.md`; intentionally invalid examples remain
in the [DocsSample walkthrough](../samples/LithoSharp.DocsSample/README.md#from-a-c-model-to-checked-output).

The compiler's `AdditionalFiles` and runtime loader share the `content` root.
IDs include the relative path and extension: `getting-started.md` and
`reference/getting-started.md` stay distinct. Routes are `guide/<relative-path-without-extension>/`,
using `/` on every OS. `_`-prefixed files and directories are excluded in both
paths. Add, rename or exclude files in both declared input rules and runtime
loading when changing the defaults. A runtime file missing from the generated
catalog fails route lookup rather than receiving a guessed URL; a catalog's
coverage comes from actual compiler inputs, not a hard-coded Closed promise.
`FromCatalog` rejects added, missing or moved input files until the catalog is
rebuilt. The template declares `InputRoot = "content"` explicitly.

## Use generated references

The factory already renders a link through the generated reference:

```csharp
Guides.Entries.Content_Getting_Started.GetUrl(rendering.Site.BaseUrl)
```

Use its `ToAttributeValue()` result in an HTML attribute, as the supplied factory
does. Keep generated names tied to the declared inputs; rebuilding after a
rename updates the available symbols. Do not duplicate the catalog with a
handwritten route dictionary. See [typed collections](typed-markdown-collections.md)
for custom binders, references and runtime validation.

The default Markdown profile remains `lithosharp-markdown/1`. Select other
[profiles](markdown-profiles.md) explicitly; front-matter binding and the Docs
layout are separate choices.

## Build, check and preview

Set the site's title and actual deployment base URL in `DocsSiteFactory.cs`.
The new factory explicitly enables Quality with an Error failure threshold and
external HTTP checks disabled. Core's default `Quality = null` remains unchanged.
Both the CLI and `dotnet run` use this factory. The generated Program prints
quality diagnostics and exits unsuccessfully on loading or validation failure.

```sh
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp check MyDocs -c Release --format json
.tools/lithosharp check-output --output MyDocs/dist --base-url https://example.com/ --format json
.tools/lithosharp serve MyDocs -c Release --port 4317
```

Use the configured base URL in `check-output`. Open `http://localhost:4317/` and
`http://localhost:4317/guide/getting-started/`. The home is `dist/index.html`;
the content pages include `dist/guide/index/index.html` and
`dist/guide/getting-started/index.html`. The Docs template alone owns the home.
`serve` first builds, then serves saved output and watches for changes; it is
not a read-only/no-build preview. Stop it with Ctrl+C. To preview an unchanged
distribution only, use your static HTTP host against `dist`.

| Command | Runs against | What success establishes |
| --- | --- | --- |
| `build` | Compiled project, factory and generated artifacts | Generation and the configured Quality policy succeeded before output commit. |
| `check` | Newly generated temporary output | Quality validation without publishing the configured output. |
| `check-output` | Files already saved in the specified directory | Those artifacts were inspected; current source freshness is not established. |
| `preflight --mode static` / `--mode trusted` | Explicit source buffers / compiled trusted factory and catalog | Findings in the reported Partial phase; neither renders final HTML or certifies a whole site. |

A missing required title produces `LSG006` during compilation. A statically
proven entry/route contradiction produces `LSA1402`; final missing targets or
anchors produce `LSQ001`/`LSQ002`. Explicit trusted Build or Inspect can acquire
generated static schema metadata. Editor field checks require the captured
project/schema identity, hashes and a unique input-membership match. Typing does
not execute user constructors, setters, validation delegates or components.
Schema or input-membership changes require fresh evidence; build-required checks
remain outside static schema validation. `NoContext`, `Partial`, `Unknown` and
`Deferred` are not complete validation. See [diagnostics](diagnostics.md),
[language-server evidence](language-server.md), [preflight](cli.md) and
[output checks](generated-output-checks.md).

## CI for this Docs project

After selecting matching 3.0.0 packages, keep the site's tool and package versions
aligned. Run one generation/check path before publishing the same output:

```sh
dotnet restore MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp check-output --output MyDocs/dist --base-url https://example.com/ --format json
```

Configure CI to stop on a nonzero exit code. The factory's explicit Quality
Error threshold applies to the generation step; the final command inspects the
saved distribution. Do not publish after either fails. This Markdown CI needs
no npm restore. For an existing project, adopt this configuration explicitly
using the [3.0 migration guide](migration-3.0.md).

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
products. See [upgrading to 3.0](migration-3.0.md), [testing](testing.md),
[performance](performance.md) and [Known limitations](known-limitations.md).
The [2.x migration guide](migration-2.0.md) remains historical material for older upgrades.
