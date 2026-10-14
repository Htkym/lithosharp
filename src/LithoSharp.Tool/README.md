# LithoSharp.Tool

.NET 10 CLI for C# static sites. Requires the .NET SDK and ASP.NET Core shared framework.

## Preflight

The preflight commands below are available in the 2.0.0 development candidate. They are not included in the published 1.1.0 Tool. Install the exact reviewed candidate Tool package from its candidate feed and use matching candidate Core/MDX packages for trusted project validation.

`capabilities --format json` advertises this Tool's `preflight-static-inputs` and `preflight-trusted-catalog` commands as Preview capabilities in schema 1.0. The report does not evaluate a project: its bundled Core identity must not be substituted for the selected project's Core. A trusted preflight response reports the factory assembly's Core reference separately as `projectCoreVersion`; it does not certify every feature of that referenced Core or a future render.

`lithosharp preflight --mode static --input page.md --input page.mdx --format json` inspects only the explicitly supplied UTF-8 source buffers. It does not evaluate MSBuild, a factory, imported JavaScript modules, project config, plugins, SSR or a bundler. MDX uses the existing analysis-only worker; restore its pinned dependencies explicitly and use `--worker-directory` to select that installation when needed. Set `--node-executable` to the explicit Node executable used for worker restore/live analysis; omission uses `node` from PATH. The setting applies to both source checks around trusted compilation. No installation or Markdown fallback occurs during preflight.

`lithosharp preflight site.csproj --mode trusted --input page.md --format json` explicitly admits project compilation and factory/catalog code. Source Error stops before compilation or factory activation. Compiler failures retain structured SARIF rule IDs and positions. After compilation the selected source files are re-read, then a separate host acquires the actual catalog and validates its routes before any renderer is called. The command never publishes output. Unknown extension prepare hooks remain Deferred and are not executed. Final HTML, template-owned output, assets, dynamic code and the full site universe remain outside this snapshot; a successful result means no Error in the reported phase.

JSON `preflight` and SARIF `runs[0].properties` carry the same phase metadata. `coverage` is always `Partial`; `sourceCoverage` is `NotRun` for no explicit inputs, `Inspected` when the existing analysis API returned for each selected input, and `Incomplete` otherwise. `Inspected` does not assert complete schema/project-context validation or absence of Error diagnostics; coverage does not replace `success` or the process exit code. Each `sources` entry records the absolute input `file`, SHA-256 `sourceHash` of the exact raw bytes read (including a UTF-8 BOM), `readState` (`NotRead`, `Read`, or `Utf8`), and `analysisState` (`NotRun`, `Inspected`, `Unsupported`, or `Failed`). A null hash means bytes were not acquired. Decoding and inspection use that same buffer; no second read supplies its hash. Trusted compilation failures retain the initial input observation, while catalog/factory responses use the post-compilation re-read. These observations do not certify subsequent disk contents, compiled C# sources, catalog-owned content, or future rendering.

`ruleVersion` identifies the bundled Tool, Core, MDX bridge and Syntamark implementation modules by informational version and actual module version ID. These identities differ from the wire `schemaVersion`; a version label alone is not an implementation fingerprint. `unverifiedRuleSources` explicitly excludes the selected external MDX worker/dependencies and arbitrary factory/extension code from this implementation identity. It is neither a full ruleset digest nor an assertion that every listed module ran. Text output retains its phase/diagnostic/Deferred summary. Exit codes remain 0 for no Error in the reported phase, 1 for validation/compiler/host failure, 2 for invalid usage, and 130 for cancellation; compiler logs and host output stay on stderr in JSON/SARIF mode.

Library callers can use `SitePreflight.InspectMarkdown` or the MDX analysis-only session, compose `SitePreflight.FromDiagnostics`, and call `SiteGenerator.PreflightFactoryAsync`. `PreflightCatalogAsync` inspects an already acquired definition. Inspectors implementing `ISiteCatalogPreflightExtension` are explicitly trusted nonrendering catalog code. Preflight snapshots do not authorize later rendering against changed inputs.

Normal generation validates the captured Core catalog and all available extension catalogs before calling any prepare hook. The built-in MDX, Blog and Documentation extensions hand that operation's captured catalog to preparation; MDX route/publication callbacks and loaders are not repeated, and changed captured inputs stop preparation. Report-only preflight discards its preparation snapshot. Extensions without inspection or a snapshot handoff remain explicitly Deferred. Build JSON includes the catalog phase, and the Core build report includes informational Deferred diagnostics; neither claims complete source coverage or a guarantee about a future build. Runtime guards and the final HTML publication gate remain in place.

The following install and site creation example uses the published 1.1.0 packages and their existing commands:

```sh
dotnet tool install LithoSharp.Tool --version 1.1.0 --tool-path .tools
dotnet new install LithoSharp.ProjectTemplates::1.1.0
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

On Windows use `.tools/lithosharp.exe`. MDX is optional and requires Node.js
24.13.0 plus explicit worker restore; see the
[Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md).

Use `check --format text|json|sarif` for quality validation, `inspect --format json`
for build graph and cache information, and `clean` to remove unchanged owned files.
`build --clean` performs a full output replacement. Sites export one public
parameterless `ISiteFactory`; ordinary library callers can use the same definition.

Testing migration is a static report over explicit source and isolated legacy metadata:

```sh
lithosharp migrate testing --dry-run --source Tests.cs --reference /isolated/1.1/LithoSharp.Testing.dll --reference /isolated/1.1/AngleSharp.dll --format json
```

The existing JSON envelope adds `migration` with byte SHA256, original UTF-16 locations, `ReadCandidate`/`Manual`/`Unknown`, reasons and informational previews. The report does not execute user assemblies, projects, build targets, generators or scripts, and does not edit source or remove dependencies. Unknown binding never becomes an automatic conversion; even a read preview requires review. Constant selectors use the current Testing grammar. Findings inspect bound legacy symbols independently of warning suppression and generated-code exclusion. Indexed uses require manual review of the full outer expression. LSA1501 keeps its bound-symbol Warning and suppression policy; ordinary compiler errors stay errors. Exit codes are 0 for analysis without compiler errors, 1 for input/analysis failure, 2 for usage and 130 for cancellation. See [Testing migration](https://github.com/Htkym/lithosharp/blob/feature/2.0.0/docs/Testing-v2-api.md#migration) for the mapping and coverage limits.

See the [CLI guide](https://github.com/Htkym/lithosharp/blob/main/docs/cli.md) for
factory examples, output behavior, development server limitations and deployment support.
