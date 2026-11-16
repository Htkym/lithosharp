# Distribution and installation

[日本語](distribution.ja.md)

This guide targets LithoSharp 3.0.0, Language Server 3.0.0, VS Code extension
0.5.0 and Syntamark 2.0.0. These are development targets, not a publication
announcement. The extension uses the identity `htkym.lithosharp` and has a
separate version.

The [version matrix](../eng/version-matrix.json) currently selects Syntamark
`2.0.0-preview.2026111303`. The [candidate provenance](../eng/markdown/candidate-provenance.json)
and [runtime/Source pair manifest](../eng/markdown/component-pair.json) identify
the selected artifacts. This candidate is not formally published. Formal
adoption requires a reviewed and merged runtime/Source pair published through
the formal release workflow.

## NuGet packages

Use matching 3.0.0 versions of the packages your project needs: `LithoSharp`,
`LithoSharp.Generators`, `LithoSharp.Images`, `LithoSharp.Tool`,
`LithoSharp.Testing`, `LithoSharp.Mdx`, and `LithoSharp.ProjectTemplates`.
Optional `LithoSharp.Analyzers` and `LithoSharp.CodeFixes` also target 3.0.0.
The tool and templates are separate packages.

Install only what the site uses. Core is the runtime; Generators add typed static
content; Images and MDX are optional generation extensions; Testing is for tests;
Analyzers and CodeFixes are compiler/IDE additions. Templates scaffold projects
and are not a runtime dependency.

Core references the Syntamark runtime paired with its package set; Generators and
Analyzers embed the corresponding Source artifact. Consumers do not add a direct
`Syntamark.Source` reference. For candidate use, keep the runtime and Source
versions and canonical source identity matched to the selected pair. The default profile remains
`lithosharp-markdown/1`; see [Markdown profiles](markdown-profiles.md) to opt into
CommonMark or the documentation profile.

Core no longer installs SkiaSharp or native image libraries. It references and
copies existing image files without decoding them. `LithoSharp.Images` provides
optional, bounded PNG processing. Register existing JPEG, WebP and AVIF files or
prepare those formats separately; see [assets and images](assets-and-images.md).

Run the following commands only when your configured NuGet sources provide a
matching 3.0.0 package set: published release packages, or candidate packages
from a source you explicitly supplied. Candidate use also requires the selected
matching Syntamark runtime/Source pair. The version numbers in this guide do not
establish package availability.

```sh
dotnet new install LithoSharp.ProjectTemplates::3.0.0
dotnet tool install LithoSharp.Tool --version 3.0.0 --tool-path .tools
```

See the [Quick Start](quickstart.md) to create and serve a site, and the
[3.0 upgrade notes](migration-3.0.md) before updating an existing project.
The recorded Core API compatibility baseline is 2.2.0; the
[2.0 upgrade notes](migration-2.0.md) are historical.

## VS Code extension

Use this Marketplace command only after extension 0.5.0 is available there:

```sh
code --install-extension htkym.lithosharp@0.5.0
```

For a candidate, install an explicitly supplied matching VSIX with
`code --install-extension <path-to-vsix>`.

The portable extension supports Windows x64, Linux x64 and macOS arm64. It
includes the compiled extension, the framework-dependent language server and
its managed dependencies, and MDX worker source with its lockfile. The .NET 10
runtime is required for the bundled server; building and serving sites also
require the .NET 10 SDK, version 10.0.300 or later, and a Tool version matching
the site.

Use `LithoSharp: Select Project`, `LithoSharp: Build`, and
`LithoSharp: Open Preview`. See the [extension guide](../extensions/lithosharp-vscode/README.md)
for settings and troubleshooting.

## MDX worker restore

Markdown-only sites and diagnostics need no Node.js. MDX requires Node.js
24.13.0 and an explicit worker restore.

For editor diagnostics, run `LithoSharp: Restore MDX Worker`. The extension
copies bundled source into hash-named extension storage and restores the locked
dependencies there with `npm ci --ignore-scripts --no-audit --no-fund`.
Restored `node_modules` are not included in the extension.
`lithosharp.workerDirectory` overrides the worker location; changing it requires
`LithoSharp: Restart Language Server`.

For site builds, use the CLI's `restore-mdx` command as described in the
[MDX guide](mdx.md). Building, serving and typing do not automatically restore
npm dependencies. Workspace trust is required for commands that execute code or
install dependencies.

## Manual language-server use

The bundled server runs as `dotnet LithoSharp.LanguageServer.dll` with its
`deps.json`, `runtimeconfig.json`, managed dependencies and worker source kept
together. It is not a separate NuGet package. Trimming, single-file publication
and Native AOT are unsupported. See the [language-server protocol](language-server.md)
for custom clients and [known limitations](known-limitations.md) for platform
and execution boundaries.
