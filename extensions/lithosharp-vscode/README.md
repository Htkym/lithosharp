# LithoSharp for VS Code

[日本語](https://github.com/Htkym/lithosharp/blob/main/extensions/lithosharp-vscode/README.ja.md)

Build and preview LithoSharp sites from VS Code, with Markdown/MDX diagnostics and heading navigation.

## Install and get started

Install [LithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp) from the Marketplace, or run:

```sh
code --install-extension htkym.lithosharp
```

Requirements:

- VS Code `^1.139.0`.
- .NET 10 runtime for the bundled language server. Building and serving sites also require the .NET 10 SDK, version 10.0.300 or later, and LithoSharp.Tool 2.1.3. The SDK includes the required ASP.NET Core shared framework.
- Node.js 24.13.0 for MDX analysis. Markdown-only diagnostics do not require Node.js.

Create a site using the [Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md), open its folder in VS Code, and trust the workspace if you trust its code. Run `LithoSharp: Select Project`, then `LithoSharp: Build` and `LithoSharp: Open Preview`.
For MDX diagnostics, also run `LithoSharp: Restore MDX Worker` to install the bundled worker's locked dependencies.

This guide describes extension 0.3.0 and the 2.1.3 Core/tooling line. The extension and NuGet packages have separate versions; capabilities are checked from the selected tool, not assumed from a version label.

## Project selection and commands

- The extension finds projects from static files, including `*.csproj` references, tool manifests and central package settings. Detection does not evaluate MSBuild.
- `LithoSharp: Select Project` selects a project explicitly. Multi-root workspaces keep selections per folder and distinguish projects with the same name.
- `LithoSharp: Build` builds the site. `LithoSharp: Inspect Site` shows CLI inspection results.
- `LithoSharp: Start Server` and `LithoSharp: Stop Server` control the selected project's development server. Each project has its own server; stopping one does not stop another project's processes.
- The output channel and status bar show the selected CLI path and version.

## Preflight and capability reporting

`LithoSharp: Preflight` is an explicit trusted-workspace command. It probes the selected CLI's compatible capability report before offering **Static inputs** (the saved active Markdown/MDX file only) or **Trusted project catalog** (compilation and trusted factory/catalog execution). Missing capabilities, including the published Core/Tool 1.1 report, disable this operation with an explanation. Existing Build, Inspect and live diagnostics remain available. Versions alone never enable preflight, and unsaved buffers must be saved before this explicit file operation. Preflight never renders or publishes; a successful snapshot does not authorize later rendering.

The Output channel distinguishes the language server's bundled Core from the selected project's Core reference. Initialize does not acquire project capabilities. MDX worker availability is reported after actual analysis; an unavailable worker preserves Markdown diagnostics and points to `LithoSharp: Restore MDX Worker`. Restore and Restart replay already-open buffers. C# diagnostics and CodeFix remain with the Roslyn host; this extension does not implement a second C# server, `LSA1402`/`LSA1403` or FixAll.

## Editing and preview

Markdown/MDX diagnostics retain their original IDs. Changes are debounced by 150 ms by default; stale diagnostic results are discarded. Markdown Outline entries use section ranges for the whole symbol and the original heading range for navigation. Hierarchy comes from Syntamark section parent keys; unavailable section information falls back to the known heading range. MDX retains its separate heading-based path. Typing does not run a full site build, SSR, or user modules.

Without project context (`NoContext`), syntax diagnostics and navigation remain available, but project-bound schema and route checks are unavailable. Project snapshots bind only built-in schemas; custom C# schemas are checked by the compiler and site build. Partial analysis exposes only available facts. If input or resource limits leave no reliable range, the extension shows a separate analysis-unavailable status instead of inventing a Problems location. Corrected input clears that status. See the [typed-content walkthrough](https://github.com/Htkym/lithosharp/blob/main/samples/LithoSharp.DocsSample/README.md#from-a-c-model-to-checked-output) and [analysis limits](https://github.com/Htkym/lithosharp/blob/main/docs/migration-2.0.md#package-versions).

`LithoSharp: Open Preview`, `LithoSharp: Refresh Preview`, and `LithoSharp: Open in Browser` show the site's served output. Preview uses routes from site inspection. Unknown, draft, or unbuilt documents show an explanation. If an update fails, the last successful result remains labeled. A server started for preview stops when its last preview panel closes; a server started with `LithoSharp: Start Server` keeps running.

Preview shows saved, built documents; unsaved buffers have no preview. The MDX worker transpiles TypeScript without type-checking.

MDX preflight passes `lithosharp.nodeExecutable` to the CLI as `--node-executable`, using the same configured Node as live analysis. A waiting preflight capability probe is cancelled when it is superseded, the workspace/settings change, or the extension is disposed; its owned process tree is stopped.

## Workspace trust

Project detection works in untrusted workspaces. Building, serving, language-server execution and worker installation require workspace trust. Workspace-provided executable paths are ignored until the workspace is trusted. The extension collects no telemetry.

## Settings

| Setting | Purpose |
| --- | --- |
| `lithosharp.cliPath` | CLI executable. Empty resolves a project-local dotnet tool, then the global PATH. |
| `lithosharp.projectPath` | Selected project file. |
| `lithosharp.languageServerPath` | Language server executable or DLL. Empty starts the bundled server with `dotnet` on PATH. |
| `lithosharp.workerDirectory` | MDX worker directory. Empty uses extension storage. Changing it requires `LithoSharp: Restart Language Server`. |
| `lithosharp.nodeExecutable` | Node.js executable for MDX analysis. Empty resolves `node` from the language server environment. Changing it requires `LithoSharp: Restart Language Server`. |
| `lithosharp.diagnosticDebounceMs` | Diagnostic delay in milliseconds: default 150, range 50–1000. |

## Troubleshooting

- Missing diagnostics: check the LithoSharp output channel, workspace trust, and the .NET 10 runtime on PATH. Use `LithoSharp: Restart Language Server` after changing server or worker settings.
- MDX diagnostics unavailable: check Node.js 24.13.0 and run `LithoSharp: Restore MDX Worker`. Markdown diagnostics work without that restore.
- Build or server unavailable: install LithoSharp.Tool 2.1.3 and the .NET 10 SDK, then check the selected project and CLI path in the output channel.
- Preview unavailable: build the document and check its route, draft status and server output.

See [known limitations](https://github.com/Htkym/lithosharp/blob/main/docs/known-limitations.md) for Markdown compatibility, execution boundaries and long-running stability coverage.
