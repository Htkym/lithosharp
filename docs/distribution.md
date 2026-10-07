# Distribution

[日本語](distribution.ja.md)

What ships, where it runs, and who approves it. The extension uses the existing
`htkym` Marketplace publisher. Publication remains pending separate release
approval and repository setup; this guide does not grant that approval.

## NuGet packages

Seven packages are prepared together at version 1.1.0: `LithoSharp`,
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

One portable VSIX for Windows x64, Linux x64 and macOS arm64. It contains the
compiled extension, its manifest and docs, the bundled framework-dependent
language server with per-RID native assets, and MDX worker source with its
lockfile. `.local`, secrets, logs, credentials, development `node_modules`,
fixtures and tests are excluded. `eng/Test-VsixContents.ps1` uses locked VSCE
4.0.0 and checks forbidden content, required entries, staged payload hashes,
version and publisher alignment, then runs three smokes against the extracted
language server. Local setup is in [the Golden Path](golden-path.md#4-vsix-install-local).
Pack (`vsix:pack`) does not publish.

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
- Extension: pushing `extension/lithosharp-vscode/0.1.0` requests publication
  through `publish-extension.yml`. The tag must match `package.json`, and its
  commit must already be on `main`. The workflow requires a configured
  `marketplace` environment with at least one required reviewer before proceeding.
- The existing installed-extension workflow produces one publisher-bearing VSIX
  and tests those same bytes on all three operating systems. Only after all jobs
  succeed and the environment reviewer approves does the publish job verify the
  producer SHA256, source commit, version and publisher, then submit that exact
  artifact with VSCE 4.0.0. It does not compile, stage or repackage it.
- Manual dispatch defaults to `publish=false` and performs validation only.
  `publish=true` requires the matching extension tag; a branch dispatch fails.
- `eng/Test-ReleasePipeline.ps1` checks trigger separation and release controls.
  `eng/Test-ExtensionPublishGuards.ps1` exercises approval-policy and artifact
  checks offline, including wrong tags, tampered bytes and identity mismatches.

## Marketplace setup before the first release

1. Confirm the release operator can publish as `htkym`. The extension identity is
   `htkym.lithosharp`; no `VSCE_PUBLISHER` variable is needed.
2. Configure the `marketplace` GitHub environment with at least one required
   reviewer. Merely creating an environment without reviewers is insufficient.
3. Add a valid `VSCE_PAT` secret to this repository or that environment. The PAT
   must have Marketplace Manage scope and access to the publisher. An existing
   secret in another repository is not automatically available here. Never paste
   the token into source files, PRs, logs or chat.
4. Merge the reviewed PR and separately approve the release before creating and
   pushing the extension tag. Tag creation is a publication request.

The [official VS Code publishing guide](https://code.visualstudio.com/api/working-with-extensions/publishing-extension)
currently documents PAT publishing and announces retirement of global Azure
DevOps PATs on December 1, 2026. The prepared workflow follows the existing
SharpDeps PAT route; identity-based authentication after that retirement needs
a separately reviewed setup change. No credentials or permissions are configured
by these scripts.

## Audit scope

NuGet (`dotnet list package --vulnerable`), npm (extension and worker,
`npm audit`) and worker dependency licenses (all MIT) are checked per
distribution run; findings and exceptions live in the task memo, not in a
standing "zero vulnerabilities" claim. `THIRD-PARTY-NOTICES.md` covers the
NuGet set.
