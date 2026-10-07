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
- .NET 10 runtime for the bundled language server. Building and serving sites also require the .NET 10 SDK, version 10.0.300 or later, and LithoSharp.Tool 1.1.0. The SDK includes the required ASP.NET Core shared framework.
- Node.js 24.13.0 for MDX analysis. Markdown-only diagnostics do not require Node.js.

Create a site using the [Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md), open its folder in VS Code, and trust the workspace if you trust its code. Run `LithoSharp: Select Project`, then `LithoSharp: Build` and `LithoSharp: Open Preview`.
For MDX diagnostics, also run `LithoSharp: Restore MDX Worker` to install the bundled worker's locked dependencies.

## Project selection and commands

- Finds projects from static files, including `*.csproj` references, tool manifests and central package settings. Detection does not evaluate MSBuild.
- `LithoSharp: Select Project` selects a project explicitly. Multi-root workspaces keep selections per folder and distinguish projects with the same name.
- `LithoSharp: Build` builds the site. `LithoSharp: Inspect Site` shows CLI inspection results.
- `LithoSharp: Start Server` and `LithoSharp: Stop Server` control the selected project's development server. Each project has its own server; stopping one does not stop another project's processes.
- The output channel and status bar show the selected CLI path and version.

## Editing and preview

Markdown/MDX diagnostics retain their original IDs. Changes are debounced by 150 ms by default; stale diagnostic results are discarded. Heading symbols appear in the Outline. Typing does not run a full site build, SSR, or user modules.

`LithoSharp: Open Preview`, `LithoSharp: Refresh Preview`, and `LithoSharp: Open in Browser` show the site's served output. Preview uses routes from site inspection. Unknown, draft, or unbuilt documents show an explanation. If an update fails, the last successful result remains labeled. A server started for preview stops when its last preview panel closes; a server started with `LithoSharp: Start Server` keeps running.

Preview shows saved, built documents; unsaved buffers have no preview. The MDX worker transpiles TypeScript without type-checking.

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
- Build or server unavailable: install LithoSharp.Tool 1.1.0 and the .NET 10 SDK, then check the selected project and CLI path in the output channel.
- Preview unavailable: build the document and check its route, draft status and server output.

See [known limitations](https://github.com/Htkym/lithosharp/blob/main/docs/known-limitations.md) for Markdown compatibility, execution boundaries and long-running stability coverage.
