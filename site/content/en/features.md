---
title: Features
supported_version: "4.0"
prerequisites: [introduction]
sidebar_position: 3
---

Choose a capability, try its example and follow the detailed guide.

## Write Markdown with a C# content contract

The Docs template defines the front matter in C#:

```csharp
public sealed class GuideFrontMatter
{
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public int Order { get; init; }
}
```

A page supplies values for that contract:

```markdown
---
title: Installation
description: Set up your first site.
order: 1
---

# Installation

Follow the quick start to create a documentation site.
```

Missing a required title produces `LSG006` when the template's generator runs.
Rebuild after adding, moving or removing content files.

[Create this site structure](../quickstart/) · [Explore typed Markdown and YAML](../typed-content/)

## Organize documentation for your readers

Sidebars, page navigation, a table of contents, versions and locales organize your
content. Local search supports collection, version and language scopes.

Try switching this page's language: you stay on the corresponding document.

[Configure documentation and languages](../documentation/)

## Validate and publish a knowledge bundle

The [Open Knowledge Format guide](../knowledge/) shows how to inspect and
validate a local OKF bundle and render it in Docs. This preview workflow keeps
bundle metadata separate from C# schemas for ordinary Markdown.

## Add components when plain Markdown is not enough

Choose a Markdown profile for your content. The default is
`lithosharp-markdown/1`; `syntamark-docs/2` adds documentation syntax.
A registered C# component can expose typed literal attributes. This site's
version badge is one example:

```markdown
:::component name="version-badge" version="4.0"
:::
```

See the [rendered badge and its configuration](../profiles/). Component attributes
do not execute C# expressions. For React interactivity, use the optional MDX
workflow, which requires Node.js and explicit worker setup.

[Explore templates, MDX and testing](../extended/)

## Publish API reference alongside your guides

Generate .NET reference from assembly metadata and compiler XML, or HTTP API
reference from OpenAPI 3 JSON.

Browse real generated output: [Core API](../../api/en/lithosharp/),
[MDX API](../../api/en/lithosharp.mdx/) and [Testing API](../../api/en/lithosharp.testing/).
Missing XML descriptions are shown explicitly rather than replaced with invented text.

[Learn how API documentation is generated](../api/)

## Check the output, not just the source

Build diagnostics and output checks answer different questions. After following
the quick start, inspect the saved files with:

```sh
.tools/lithosharp check-output --output MyDocs/dist --base-url https://example.com/ --format json
```

Use your configured base URL. On Windows, use `.tools/lithosharp.exe`.

Find missing internal targets (`LSQ001`) and headings (`LSQ002`). This reads
saved output, not current source; build first when you need fresh results.

[Understand operations and verification](../operations/) · [Use the CLI and editor](../tooling/)

## Extend your site

- **Images:** use existing image assets and responsive variants; optional processing
  supports a limited PNG workflow. [Image guide](../images/)
- **Knowledge bundles:** read and preserve OKF bundles with separate conformance,
  freshness and trust policies. [Knowledge guide](../knowledge/)
- **Translation review:** track reviewed content hashes and check mapped snippets
  with explicit trusted tests. [Verification guide](../operations/)
- **Existing sites:** plan package updates and preserve published URLs.
  [Migration guide](../migration/)

Each guide explains its configuration and limitations. Conditional reference checks
need adequate catalog context; unknown or incomplete evidence is not a successful
check. For the complete package and guide map, use the
[detailed documentation index](../../reference/en/README/).