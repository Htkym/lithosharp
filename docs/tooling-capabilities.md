# Tooling capability contract (v1.1.0 / schema 1.0)

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

## Capability list (schema 1.0, all Stable)

| name | scope | Meaning | Client behavior when capability is missing |
| --- | --- | --- | --- |
| `document-inspection` | `markdown`, `mdx`, `syntax`, `front-matter`, `project-resolution`, `generated-output`, `runtime` | Inspection of unsaved documents. `syntax` and `front-matter` can be executed without a project, and the remainder require explicit project operations | Disable unsaved analysis and display only built results |
| `versioned-snapshot` | `syntax`, `project-resolution` | Snapshots have document version and project generation, allowing stale results to be discarded | Explain the risk of displaying stale results and disable automatic updates |
| `serve-shutdown` | (none) | Structured serve shutdown via stdin. Shutdown requests that are duplicate, in-progress, or during startup are idempotent | Limit shutdown to Ctrl+C only, and display that limitation in the UI |
| `source-route-lookup` | `project-resolution` | Source-to-route lookup preserving project / collection / variant identification. Returns multiple candidates when multiple exist | Do not guess file extensions; report the route as unavailable |

`scope` values are forward compatible. Unknown scope values are ignored, evaluating only known scopes.

## Negotiation rules

- Ignore unknown fields and unknown capability names (changes are additive).
- Missing required capabilities fail with an explanation via `ToolingCapabilities.Require`, disabling the feature. Do not substitute with empty arrays or guesses.
- Schema versions are compatible on matching major version (`ToolingCompatibility.IsCompatible`). Even if the other party is a newer minor version (e.g., `1.1`), it is considered compatible and unknown fields are ignored. Major version mismatches (e.g., `2.0`) are rejected.
- To prevent older consumers from relying on numeric values when values are added to existing enums, reports output maturity as strings.

## Identity contract for Editor results

New Editor-facing results return identity with at least the following field names. Value meanings use the same vocabulary as capability scope.

| field | Type | Meaning |
| --- | --- | --- |
| `workspaceId` | string | Workspace or session identification. Stable within the process |
| `projectId` | string? | Resolved project identification. `null` when unresolved |
| `documentUri` | string | Document URI. Do not unconditionally normalize casing |
| `documentVersion` | int | Document version assigned by the Editor |
| `projectGeneration` | int | Project context generation. Increments on reload |
| `languageKind` | string | `markdown` / `mdx` |
| `analysisStage` | string | `syntax` / `front-matter` / `project-resolution` / `generated-output` / `runtime` |
| `completion` | string | `complete` / `partial` / `canceled` / `failed` |
| `diagnostics` | array | Diagnostics. Positions follow the public position rules below |

Do not display stages where `analysisStage` was not executed as "success with 0 diagnostics". When there is no context, return `project-resolution` and subsequent stages as unexecuted.

## Diagnostics and position conversion contract

- Public API / CLI positions are 1-based line and column (maintaining existing contract).
- Internal `SourceSpan` uses UTF-16 code units, 0-based, half-open intervals (maintaining existing contract).
- Conversion to the Language Server is performed in a single location, and LSP uses 0-based UTF-16 line and column.
- Severity maps Core `Info` / `Warning` / `Error` to LSP Information / Warning / Error. Do not create custom diagnostic IDs from exception messages.
- Do not attach fictional ranges to diagnostics whose positions cannot be determined. Route out-of-document diagnostics to the project output side.
