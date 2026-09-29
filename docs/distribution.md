# Distribution

[日本語](distribution.ja.md)

What ships, where it runs, and who approves it. Marketplace publication is
BLOCKED: no publisher account exists, so no package claims an identity and no
workflow can publish.

## NuGet packages

Seven packages publish together at one version (currently 1.0.0): `LithoSharp`,
`LithoSharp.Generators`, `LithoSharp.Images`, `LithoSharp.Tool`,
`LithoSharp.Testing`, `LithoSharp.Mdx`, `LithoSharp.ProjectTemplates`. The
test-only `LithoSharp.FixtureExtension` is never packed. `eng/Validate-Package.ps1`
checks contents, metadata, version alignment and the Markdig ban;
`eng/Test-PackageDistribution.ps1` packs, validates, installs the tool and the
templates from an isolated feed with fresh caches, and builds a template site
from packages only.

Tags in the `v*` series (`v1.1.0`) drive `.github/workflows/publish-nuget.yml`
exclusively. The extension tag series below never matches that glob.

## Language server

The server (`src/LithoSharp.LanguageServer`, version 1.1.0) ships
framework-dependent: `dotnet LithoSharp.LanguageServer.dll` plus its
`deps.json`, `runtimeconfig.json`, managed dependencies, per-RID native assets
and the worker source (`worker/*.mjs`, `runtime/`, `package.json`,
`package-lock.json`). Restored `node_modules` never ship. Trimming, single-file
and AOT are off by project file, not by flag discipline.
`eng/Test-LspDistribution.ps1` verifies the layout and smokes initialize,
Markdown diagnostics, and graceful degradation without Node or without restored
worker dependencies. The server is not a NuGet package; it travels with the
VSIX or a manual install.

## VSIX

One portable VSIX for Windows x64, Linux x64 and macOS arm64: 48 files,
119.39 KB at 0.1.0, with no native binaries (`.node`/`.dll`/`.so`/`.dylib`
scan) and no `.local`, secrets, logs, credentials, dev `node_modules`,
fixtures or tests. Contents: compiled shell, manifest, docs, and the MDX
worker source with its lockfile. `eng/Test-VsixContents.ps1` packs with
`vsce`, then asserts forbidden content, required entries, version alignment
and the absent publisher field. Pack (`vsix:pack`) never transmits anything.

## MDX worker restore

Restored npm dependencies are per-machine state, so the VSIX carries source
plus lockfile instead of `node_modules`. `LithoSharp: Restore MDX Worker`
copies the bundled source into hash-named extension storage
(`worker-<lockfile12>`) and runs `npm ci --ignore-scripts --no-audit
--no-fund` there. User projects, global npm state and user npm configuration
are never touched. `lithosharp.workerDirectory` overrides the location;
changing it needs `LithoSharp: Restart Language Server`. Without a restored
worker, Markdown diagnostics keep working and MDX stays unexplained.

## Tags and approvals

- Core: `v1.1.0` series → `publish-nuget.yml` (pack, validate, OIDC push).
- Extension: `extension/lithosharp-vscode/0.1.0` series →
  `publish-extension.yml` packs and validates on tag push only. Publishing runs
  exclusively from manual dispatch behind the `marketplace` environment
  approval plus a configured publisher; without one it fails closed.
- `eng/Test-ReleasePipeline.ps1` asserts the trigger separation, the
  dispatch-plus-approval gate, the version/tag alignment and that pack scripts
  contain no publish commands.

## Audit scope

NuGet (`dotnet list package --vulnerable`), npm (extension and worker,
`npm audit`) and worker dependency licenses (all MIT) are checked per
distribution run; findings and exceptions live in the task memo, not in a
standing "zero vulnerabilities" claim. `THIRD-PARTY-NOTICES.md` covers the
NuGet set.
