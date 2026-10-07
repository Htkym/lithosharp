# Golden paths

[日本語](golden-path.ja.md)

Five supported routes from install to serving. Each step states where
dependencies are restored and where builds run; nothing hidden happens.
Versions below follow [1.1 features](release-1.1.md); packages install at
1.0.0 until distribution (V110-23).

## 1. Markdown-only site

```sh
dotnet new install LithoSharp.ProjectTemplates::1.0.0
dotnet tool install LithoSharp.Tool --version 1.0.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release --port 4317
```

No `dotnet restore` beyond the template's own project restore, no Node.js.
`serve` builds in a child process, watches the project directory, and serves
on loopback. See [Quick Start](quickstart.md).

## 2. MDX site

```sh
.tools/lithosharp new mdx MyMdx -o MyMdx
dotnet build MyMdx -c Release
.tools/lithosharp restore-mdx MyMdx/bin/Release/net10.0/worker
.tools/lithosharp build MyMdx -c Release
.tools/lithosharp serve MyMdx -c Release --port 4317
```

`restore-mdx` is the only step that installs npm dependencies
(`npm ci --ignore-scripts --no-audit --no-fund` from the packaged lockfile).
Builds never install npm packages. Only trusted MDX is built. TypeScript is
transpiled, not type-checked.

## 3. Existing project updates

```sh
dotnet restore MySite -p:NuGetAudit=false
dotnet build MySite -c Release
.tools/lithosharp check MySite -c Release --format json
.tools/lithosharp build MySite -c Release
```

`check` generates into temporary output and validates without publishing.
`build` compiles the project first, then generates incrementally into the
configured output. `clean` removes only owned unchanged artifacts, never
caches; `cache clean -o <directory>` reclaims one output partition explicitly.

## 4. VSIX install (local)

The extension is not on the Marketplace in 0.1.0.

```sh
npm --prefix extensions/lithosharp-vscode ci --ignore-scripts --no-audit --no-fund
npm --prefix eng/vsix-packager ci --ignore-scripts --no-audit --no-fund
npm --prefix extensions/lithosharp-vscode run vsix:pack
code --install-extension extensions/lithosharp-vscode/lithosharp-0.1.0.vsix
```

The two `npm ci` commands restore committed lockfiles and need network access.
The packager lock fixes VSCE at 4.0.0; `vsix:pack` invokes that local CLI
without downloading another version. It compiles, stages the bundled language
server and MDX worker, then packages without publishing anything.

Then `LithoSharp: Select Project`, `LithoSharp: Build`, and `LithoSharp: Open
Preview`. The extension resolves the CLI (project-local tool, then PATH) and
starts the language server from `lithosharp.languageServerPath`; MDX
diagnostics additionally need the restored worker. Untrusted workspaces run
detection only: commands that would start a process explain instead.

## 5. Multi-root workspace

Add each folder once. `LithoSharp: Select Project` stores one project per
workspace folder plus project path; same-name projects stay distinct. Each
project owns its serve state machine and preview panels; stopping one never
touches another project's processes. Language sessions connect Markdown/MDX
documents per folder; diagnostics never cross projects.

## Execution boundaries

- .NET restore/build: explicit commands above. Builds do not restore npm packages.
- npm install: `restore-mdx` (MDX worker) and third-party site restores only.
- Hidden steps that never run: dependency restores inside `build`, `serve`,
  `check`, or editor keystrokes; JavaScript configuration execution during
  migration; telemetry (the extension collects none in 0.1.0).
