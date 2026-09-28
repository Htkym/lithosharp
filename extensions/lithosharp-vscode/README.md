# LithoSharp for VS Code (0.1.0)

[日本語](README.ja.md)

Everyday entry point for LithoSharp sites. This shell finds the target
project and controls the CLI. It never guesses a project and never runs code
without trust.

## What it does

- Finds LithoSharp projects from static files (`*.csproj` with a LithoSharp
  reference, tool manifest, central package settings). MSBuild evaluation
  never runs for detection.
- Keeps multi-root selections apart by workspace folder plus project path.
  Same-name projects stay distinguishable.
- `LithoSharp: Select Project` saves an explicit selection. Ambiguity prompts
  instead of guessing.
- Shows the adopted CLI path and version in the output channel and status bar.

## Trust

The extension declares limited workspace trust. Project detection works
without trust. Every command that could start a process first requires trust
and otherwise explains itself. Workspace-provided executable paths
(`lithosharp.cliPath` is a restricted configuration) are ignored until the
workspace is trusted. No telemetry is collected in 0.1.0.

## Settings

- `lithosharp.cliPath`: explicit CLI executable. Empty resolves
  automatically (project-local dotnet tool, then global PATH). Resolution
  never executes; the version is queried once on explicit selection.
- `lithosharp.projectPath`: selected project file.

## Scope

Build, serve, diagnostics, symbols, and preview arrive in later tasks
(V110-16 onwards). This shell owns no processes: stopping it disposes only
the channel, status item, and listeners.

## Commands (V110-16)

- `LithoSharp: Build`, `LithoSharp: Start Server`, `LithoSharp: Stop Server`,
  `LithoSharp: Inspect Site` run the real CLI with machine output and show the
  result. Build validates only; it is never a publish approval.
- Each project has its own serve state machine
  (Stopped/Starting/Running/Rebuilding/Failed/Stopping) driven by real
  process events. Normal stops use structured stdin shutdown; process-tree
  recovery is the last resort after a timeout and never touches other processes.

## Editing (V110-17)

- Markdown/MDX documents open a single editing LSP session with debounced
  change traffic (150 ms, 50-1000 ms configurable). Diagnostics carry the
  original IDs with version-guarded display; stale results never resurface.
- Symbols feed the Outline from owned heading ranges. No full-site build, SSR,
  or user module execution happens on keystrokes.
