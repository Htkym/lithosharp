# Decide whether LithoSharp fits

[日本語](adoption-guide.ja.md)

LithoSharp is aimed first at .NET teams maintaining product documentation,
handwritten technical guides and structured knowledge sites. It connects C#
content contracts, generated references, compiler checks and final-output checks
in the same build workflow. This guide records fit and evaluation scope, not a
ranking or a claim of reduced effort.

## Fit and operating cost

Choose a small representative Docs section before migrating a whole site.
The team needs to maintain a .NET 10 project, C# front-matter types and route
declarations, and review compiler and generation diagnostics in CI. Markdown
authors do not need to write C#, but schema, layout and build changes need a
C# maintainer. The new Docs template supplies the typed starting point; see
[Quick Start](quickstart.md) and [Docs settings](docs-template.md).

Markdown-only generation needs no Node.js or React. Optional MDX needs the
pinned Node.js/worker dependencies and explicit worker restore. Site factories,
MSBuild tasks and MDX execute as trusted build code. HTML checks are not a code
sandbox. Deploy generated static files, not the build environment.

Keep an existing generator when its authoring model, plugins or hosting workflow
already fit and the cost of changing schemas, routes or components is not
justified. Evaluate preserved URLs, navigation, custom Markdown/MDX syntax,
theme/CSS and CI behavior separately. A successful import is not full behavioral
compatibility. See [migration](migration-2.0.md) and [limitations](known-limitations.md).

## Choose from existing assets and use

| Existing assets or intended use | What to evaluate |
| --- | --- |
| Astro components and a content-driven web site | Astro keeps that web/component ecosystem. Evaluate LithoSharp when the team wants C# content contracts and .NET build integration; plan to adapt components and configuration. |
| A Starlight documentation site | Starlight builds on Astro's documentation conventions. Check existing sidebar, theme, search and integration needs before replacing them with LithoSharp's Docs surfaces. |
| Docusaurus with React/MDX plugins and versioned docs | Preserve the value of that plugin/component investment. LithoSharp has optional MDX and a bounded [migration path](docusaurus-migration.md); arbitrary plugins, JavaScript config and React behavior do not transfer automatically. |
| DocFX and .NET API-reference assets | DocFX may fit an API-reference-first workflow already in place. LithoSharp can combine handwritten typed guides with XML .NET API/OpenAPI inputs; evaluate the required reference and xref output on real inputs. |
| Statiq pipelines and custom C# processing | Statiq may fit an existing pipeline investment. Evaluate LithoSharp's site/content APIs, generated references and staged output checks against the processing and extension behavior you need to retain. |

These are selection questions, not measured performance or feature-parity claims.
See [reference times](performance.md). Results vary with the environment, configuration, and content.

## Try the Docs sample

The [Docs sample](../samples/LithoSharp.DocsSample/README.md) connects
`ArticleFrontMatter`, a generated binder/catalog and typed references to Docs
navigation and final HTML. Its walkthrough includes separate invalid inputs:

| Problem | Check |
| --- | --- |
| Missing required title | `LSG006` during compilation |
| Proven entry/route contradiction | Analyzer `LSA1402` |
| Missing output target or anchor | Quality `LSQ001` / `LSQ002` |

Match compiler input roots and exclusions to runtime loading. Decide which
required fields and URLs to preserve before migrating. Dynamic references and
unavailable editor context remain unproven; custom C# schemas require compilation.
Output inspection does not establish source freshness or external HTTP availability.
