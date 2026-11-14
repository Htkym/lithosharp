# Golden paths

[日本語](golden-path.ja.md)

The following workflows target LithoSharp 3.0.0 and extension 0.5.0.
Check the [version matrix](../eng/version-matrix.json) before installation;
candidate validation does not establish formal package publication.
Each workflow explains when dependencies are restored and project code runs.
For existing sites, follow the [upgrade guide](migration-3.0.md).

## Markdown-only site

```sh
dotnet new install LithoSharp.ProjectTemplates::3.0.0
dotnet tool install LithoSharp.Tool --version 3.0.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release --port 4317
```

New Docs use a C# schema, generated binder/catalog, typed references and
`RenderDocs`. Quality is explicitly enabled at Error without external HTTP.
Use the [Quick Start](quickstart.md) for edits, the check-command scope table and
the single Docs CI sequence. The project build can restore NuGet dependencies;
no Node.js is needed.
`serve` builds in a child process, watches the project directory, and serves
on loopback. See [Quick Start](quickstart.md).

## MDX site

```sh
.tools/lithosharp new mdx MyMdx -o MyMdx
dotnet build MyMdx -c Release
.tools/lithosharp restore-mdx MyMdx/bin/Release/net10.0/worker
.tools/lithosharp build MyMdx -c Release
.tools/lithosharp serve MyMdx -c Release --port 4317
```

`restore-mdx` is the only step that installs npm dependencies
(`npm ci --ignore-scripts --no-audit --no-fund` from the packaged lockfile).
Builds never install npm packages. Build only trusted MDX; MDX and component code execute as trusted build code. TypeScript is
transpiled, not type-checked.

## Existing project updates

```sh
dotnet restore MySite
.tools/lithosharp build MySite -c Release
.tools/lithosharp check-output --output MySite/dist --base-url https://example.com/guide/ --format json
```

`build` compiles the project first, then generates incrementally into the
configured output. `check-output` inspects that existing distribution; use its
actual output path and published base URL. It does not prove source freshness.
`check` instead generates into temporary output and validates without publishing.
See [existing output checks](generated-output-checks.md). `clean` removes only owned unchanged artifacts, never
caches; `cache clean -o <directory>` reclaims one output partition explicitly.

## VS Code extension

Install [LithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp):

```sh
code --install-extension htkym.lithosharp
```

Install the .NET 10 runtime for the bundled language server and LithoSharp.Tool 3.0.0 with the .NET 10 SDK for builds. Run `LithoSharp: Select Project`, `LithoSharp: Build`, and `LithoSharp: Open Preview`. MDX diagnostics also require Node.js 24.13.0 and `LithoSharp: Restore MDX Worker`. Untrusted workspaces support detection only. See the [extension guide](../extensions/lithosharp-vscode/README.md) for settings and troubleshooting.

## Multi-root workspace

Add each folder once. `LithoSharp: Select Project` stores one project per
workspace folder plus project path; same-name projects stay distinct. Each
project owns its serve state machine and preview panels; stopping one never
touches another project's processes. Language sessions connect Markdown/MDX
documents per folder; diagnostics never cross projects.

## Execution boundaries

- .NET restore/build: `build`, `serve`, and `check` compile the selected project
  with `dotnet build`, which may restore NuGet packages from configured feeds.
  Project code and MSBuild tasks are trusted executable inputs.
- npm install: `restore-mdx` (MDX worker) and third-party site restores only.
- No automatic npm restore occurs inside `build`, `serve`, `check`, or editor
  keystrokes. The editor does not run MSBuild or a site factory per keystroke.
  Migration does not execute JavaScript configuration. The extension collects
  no telemetry.

For the typed C# schema, generated reference, editor and output-validation flow,
follow the [Docs sample walkthrough](../samples/LithoSharp.DocsSample/README.md#from-a-c-model-to-checked-output).
It distinguishes compiler proof from runtime checks and unavailable context,
and keeps deliberately invalid examples outside the normal site inputs.
