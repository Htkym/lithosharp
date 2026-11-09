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
Use [performance evidence](performance.md) only for its named version, workload,
measurement interval and environment; it does not predict migration effort.

## Self-use record: DocsSample

The repository's [DocsSample](../samples/LithoSharp.DocsSample/README.md) is a
maintained product example and verification fixture. It is self-use evidence,
not an independent external adoption or a production customer case.

| Record field | Observed implementation or reproducible example |
| --- | --- |
| Conditions and problem | Repository-owned .NET Docs site; keep Markdown metadata, C# references, Docs navigation and emitted HTML consistent. |
| Adopted contract | `ArticleFrontMatter`, a generated binder/catalog and typed references connect declared compiler inputs with the runtime loader. The sample README identifies the executable steps and separate invalid fixtures. |
| Problems the checks expose | Missing required title: `LSG006` at compilation. Proven entry/route contradiction: `LSA1402` in the analyzer. Missing internal output targets/anchors: `LSQ001`/`LSQ002` during quality inspection. These are controlled examples, not a count of incidents prevented in production. |
| Output verification | Build or run the sample, inspect actual HTML and routes, and run the output checks in its walkthrough. Record the source revision and command results when evaluating it. |
| Adoption questions | Compiler input roots/exclusions must match runtime loading; required fields and route conventions need an explicit migration decision. |
| Remaining limits | Dynamic references and unavailable editor context remain unproven. Output inspection does not prove source freshness or external HTTP availability. Custom schemas are not executed per keystroke. |

The record describes the example and its checks. It is not a new performance
measurement, an external user's feedback or evidence of production scale.

## Prepare an external evaluation

No participant or external repository is named here. The invitation and form
below are ready for a separately authorized evaluation. Do not contact anyone,
modify another repository or publish a case study from this document alone.

Use one small .NET product-documentation section with a required field, a typed
cross-page reference, an existing URL to preserve and a custom layout link.
Start with Markdown; include MDX only if the intended use requires it. Follow
Quick Start, then try the separate invalid examples in DocsSample. Capture actual
results, including confusing steps and checks that did not run. Do not add
continuous telemetry as a prerequisite.

### Invitation draft

> Would you be willing to try LithoSharp on a small section of .NET product
> documentation? The evaluation covers a C# content schema, generated references,
> Docs rendering and build/output diagnostics. Please use the Quick Start and
> record your environment, the steps you tried, what the checks found and any
> unclear or missing behavior in the form below. Keep existing URLs in the trial.
> Please exclude secrets and unrelated personal data. Sending feedback does not
> grant permission to publish your name, repository or results; we would confirm
> the publishable scope separately.

Choose the recipient, candidate package/feed and permitted source/publication
scope before sending this draft. No deadline or participation commitment is implied.

### Evaluation result form

Use this same form for self-use and external trials. Leave unknown fields as
unknown and steps not attempted as not run.

| Field | Fill in from the trial |
| --- | --- |
| Evidence type and date | Self-use or external evaluation; date and source revision/exact package versions. |
| Use conditions | Documentation type, relevant existing assets, environment, Markdown/MDX dependencies and CI constraints. |
| Problem to solve | The concrete consistency problem or migration requirement. |
| Adopted schema and references | Required fields, compiler input root/glob/exclusions, entry IDs, generated references and route rules. |
| Actions and observations | Commands, exit results, diagnostics with phase/location, output checks and evidence links. Distinguish not run, incomplete and passed checks. |
| Preserved or changed behavior | Existing URLs, navigation, component/layout behavior and deployment output. |
| Adoption uncertainty | The step, wording or dependency that required a decision or caused confusion. |
| Remaining gaps | Reproduction, impact and category: product explanation, onboarding, editing, generation, compatibility or performance. |
| Publication scope | Who may read the report and what may be quoted; obtain permission separately before publication. |

## Prioritize follow-up from evidence

| Priority | Current question or gap | Next evidence needed |
| --- | --- | --- |
| Before adopting | Can the real schema, routes and required navigation be preserved? | A representative migrated section and checked output; use the migration guide without overwriting the project. |
| Before relying on editor feedback | Which source/project facts are available at the current editing stage? | The editor's reported context/coverage and a compiler/build result for the custom schema. |
| Before a performance decision | Does the measured workload match the intended Docs/Quality or MDX use? | The named [measurement evidence](performance.md), then a reproducible run on the actual workload if needed. No unmeasured speed or effort claim is recorded. |
| After a trial | What should change next? | A completed result form with a reproduction and impact. External feedback is not yet available in this record. |

Classify new findings using the form before proposing wider optimization,
per-keystroke schema execution or full unsaved-buffer preview. A participant
still to be selected is not a blocker for independently verifiable product work.
