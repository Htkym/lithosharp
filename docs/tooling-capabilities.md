# Tooling capability contract (schema 1.0)

[日本語](tooling-capabilities.ja.md)

This reference describes capability information returned by `lithosharp capabilities` and identity information for tools, projects, and language servers.

## Invocation and exit code

```powershell
lithosharp capabilities            # Human-readable text
lithosharp capabilities --format json
```

- Does not evaluate the project. Runs outside project directories or in directories with broken csproj files, and does not invoke MSBuild, C#, Node, or dependency restore.
- Standard output of `--format json` is a single JSON document (not JSON Lines).
- Process exit codes are 0 (success), 1 (processing failure), and 2 (usage error). Failures when `--format json` is specified are returned as a CLI error envelope with `schemaVersion` instead of a capability report. If a required capability is missing, disable that feature on the consumer side.

## Report fields

| field | Type | Meaning |
| --- | --- | --- |
| `schemaVersion` | string | Report schema version. Currently `1.0`. Compatible on matching major |
| `success` | bool | Whether the report was generated successfully |
| `exitCode` | int | 0 or 1 (on report generation failure) |
| `tool` | identity | Running tool (`LithoSharp.Tool`) |
| `core` | identity | Core bundled with the tool (`LithoSharp`) |
| `project` | object | Resolution status of project context: `resolved`, `coreVersion`, `reason` |
| `contracts` | array | Known Tooling contracts (name / maturity / schemaVersion / description) |
| `capabilities` | array | Provided capabilities (name / maturity / schemaVersion / description / scope) |
| `error` | string? | Description on failure |

An identity has `name` and `version`. **Core bundled with the tool, Core referenced by the project, and Core bundled with the Language Server are recorded as separate identities, and unresolved values are never filled using another identity.** An unresolved `project.coreVersion` is `null`, and the tool version is not substituted.

In a successful CLI invocation, the report's `exitCode` is 0. In a failure report created by passing `error` to the Core API `ToolingCapabilities.CreateReport`, it is 1. CLI failures are returned in a separate error envelope format, whose `exitCode` is 1 for processing failure and 2 for usage error. Do not treat the error envelope as a capability report; verify `success` and `exitCode` before reading the capability list.

## Core capability list (schema 1.0)

The following four capabilities are Stable and returned by
`ToolingCapabilities.CreateReport`. The CLI adds the Tool-only Preview capabilities below;
they are not inferred capabilities of a selected project or the language server.

| name | scope | Meaning | Client behavior when capability is missing |
| --- | --- | --- | --- |
| `document-inspection` | `markdown`, `mdx`, `syntax`, `front-matter`, `project-resolution`, `generated-output`, `runtime` | Inspection of unsaved documents. `syntax` and `front-matter` can be executed without a project, and the remainder require explicit project operations | Disable unsaved analysis and display only built results |
| `versioned-snapshot` | `syntax`, `project-resolution` | Snapshots have document version and project generation, allowing stale results to be discarded | Explain the risk of displaying stale results and disable automatic updates |
| `serve-shutdown` | (none) | Structured serve shutdown via stdin. Shutdown requests that are duplicate, in-progress, or during startup are idempotent | Limit shutdown to Ctrl+C only, and display that limitation in the UI |
| `source-route-lookup` | `project-resolution` | Source-to-route lookup preserving project / collection / variant identification. Returns multiple candidates when multiple exist | Do not guess file extensions; report the route as unavailable |

`scope` values are forward compatible. Unknown scope values are ignored, evaluating only known scopes.

### Tool-only Preview capabilities

Tool 3.0.0 advertises these commands as Preview capabilities.

| name | schemaVersion | scope | Meaning |
| --- | --- | --- | --- |
| `csharp-reference-rename` | `1.0` | `preview-csharp-rename` | Roslyn previews for explicit caller sources and metadata; incomplete scopes remain manual and no files are written. |
| `proofreading` | `1.0` | `proofread` | Explicit optional Yomiyasu execution with source-mapped findings and guarded edit previews; no project evaluation or document writes. |
| `open-knowledge-format` | `0.2` | `okf inspect`, `okf validate`, `okf export` | Explicit standalone bundle inspection, validation and export; no project evaluation or computation execution. |
| `publication-compatibility` | `1.0` | `check-output` | Explicit published URL/anchor comparison against a verified manifest; no project evaluation or automatic baseline promotion. |
| `preflight-static-inputs` | `1.0` | `markdown`, `mdx` | Explicit source-only preflight without project evaluation. MDX requires an explicitly restored analysis worker. |
| `preflight-trusted-catalog` | `1.0` | `trusted-catalog` | Explicit compilation and trusted factory/catalog inspection without rendering. The capability report itself does not resolve project support. |

All use the maturity string `Preview`; the two preflight capabilities use schema `1.0`. Do not parse maturity strings
as integer values or assume that every producer uses only the Core enum values.
The report does not establish support for the selected project. Trusted preflight
separately reports the factory assembly's referenced Core as `projectCoreVersion`;
this does not guarantee all features of that Core or future rendering results.
Use the actual report to detect capabilities instead of inferring them from a version.
Preflight reports always have Partial coverage; success means no Error in the
reported phase, not that final HTML or future rendering was checked. See
[preflight](../src/LithoSharp.Tool/README.md#preflight).

The language server's initialize response uses its own
`capabilities.experimental.lithosharp` object. It advertises
`markdownInspection: true`, `mdxInspection: "on-demand-worker"`, and both
`preflightStaticInputs` and `preflightTrustedCatalog` as `false`. A client must
not route the Tool-only commands to the server.

## Negotiation rules

- Ignore unknown fields and unknown capability names (changes are additive).
- Missing required capabilities fail with an explanation via `ToolingCapabilities.Require`, disabling the feature. Do not substitute with empty arrays or guesses.
- Schema versions are compatible on matching major version (`ToolingCompatibility.IsCompatible`). Even if the other party is a newer minor version (e.g., `1.1`), it is considered compatible and unknown fields are ignored. Major version mismatches (e.g., `2.0`) are rejected.
- To prevent older consumers from relying on numeric values when values are added to existing enums, reports output maturity as strings.

## Snapshot identity and validation state

There is no single universal editor-result envelope. Use each interface's actual
contract:

- `DocumentWorkspace.Id` identifies a workspace. `InspectVersionedAsync` accepts
  document version and project generation and rejects superseded work; returned
  `DocumentInfo` contains content facts and `ProjectStatus`, not a copy of every
  request identity field. Retain the request identity when integrating it.
- `ProjectInspectionSnapshot.ToJson()` includes `schemaVersion`, `projectId`,
  `projectGeneration`, `coreVersion`, `collection`, `language`, `schema`,
  `version`, `locale`, `acquiredAt` and `routes`. It is explicitly acquired data,
  not an instruction to load a project assembly.
- LSP diagnostics carry `uri`, `version`, `diagnostics`,
  `lithosharpOpenGeneration` and `lithosharpContextGeneration`; close clears
  diagnostics without a document version. The separate
  `lithosharp/analysisStatus` notification carries `uri`, `version`, `language`,
  `state` (`ready` or `unavailable`), `message`, and the two generation fields.
  Optional `projectStatus`, `schemaName`, `schemaScope`, `projectId`,
  `projectGeneration` and `contextAcquiredAt` describe the applied context and
  supported inspection path. Missing scope stays unknown; MDX does not infer
  Markdown binding support from a schema name. Acquisition time is not an
  all-input freshness guarantee.
  See the [language-server protocol](language-server.md).

`NoContext` permits syntax inspection but does not prove project-bound schemas
or routes. `Resolved`, `Ambiguous`, `Unresolved` and `Draft` describe route
resolution within an explicit context, not whole-site validity. Snapshot binding
supports built-in schemas and explicitly acquired generated static schemas.
Generated field checks require matching captured project/schema identities,
hashes and a unique input-membership match. Typing does not execute user DTO
constructors, setters or validation callbacks. Custom executable validation and
build-required decisions remain compiler/site-build checks. Missing or stale
schema evidence does not certify those checks.
Partial or unavailable facts and Unknown catalog evidence must not be displayed
as "checked, zero errors". `LSM013` indicates that analysis could not be completed.
A `ready` LSP status means syntax analysis is available, not that a build or
generated-output inspection has run, or that the document has no errors.

## Diagnostics and position conversion contract

- Public API / CLI positions are 1-based line and column (maintaining existing contract).
- Internal `SourceSpan` uses UTF-16 code units, 0-based, half-open intervals (maintaining existing contract).
- Conversion to the Language Server is performed in a single location, and LSP uses 0-based UTF-16 line and column.
- Severity maps Core `Info` / `Warning` / `Error` to LSP Information / Warning / Error. Do not create custom diagnostic IDs from exception messages.
- Do not attach fictional ranges to diagnostics whose positions cannot be determined. Route out-of-document diagnostics to the project output side.
