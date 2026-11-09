# Distribution and installation

[日本語](distribution.ja.md)

The current source targets LithoSharp 2.2.0 and VS Code extension 0.4.0 as unpublished candidates. The stable NuGet installation examples below retain 2.1.3. The extension has a separate release channel and uses the Marketplace identity `htkym.lithosharp`. Publishing NuGet packages does not publish the extension.

## Candidate installation and compatibility

Use the exact version and isolated package feed supplied with a reviewed candidate. Set `LithoSharpVersion` to that version in generated projects, install the matching tool and templates, and keep the default user feed and template hive unchanged. A source version or local package is not evidence of publication to NuGet or Marketplace.

| Consumer | Earlier stable | 2.2.0 / 0.4.0 candidate |
| --- | --- | --- |
| Existing Core calls | Supported | Additive typed Docs adapter; default quality settings remain unchanged |
| Docs template | Stable package behavior | Generated binder/catalog/references and explicit quality Error threshold |
| CLI JSON | Schema major 1 | Same major; extension verifies required fields and exit-code agreement |
| Editor protocol | Earlier fields | Optional context fields; missing evidence stays unknown |
| Custom C# frontmatter | Compiler/build checks | Still requires the project build; no per-key editor validator is implied |

Do not mix candidate compiler packages with a different Core or generated template version. Regenerate and inspect Docs output after adopting the adapter, then build and check the complete site before deployment.

## NuGet packages

Use matching 2.1.3 versions of the packages your project needs: `LithoSharp`, `LithoSharp.Generators`, `LithoSharp.Images`, `LithoSharp.Tool`, `LithoSharp.Testing`, `LithoSharp.Mdx`, and `LithoSharp.ProjectTemplates`. Optional `LithoSharp.Analyzers` and `LithoSharp.CodeFixes` also use 2.1.3. The tool and templates are separate packages.

Install only what the site uses. Core is the runtime; Generators add typed static
content; Images and MDX are optional generation extensions; Testing is for tests;
Analyzers and CodeFixes are compiler/IDE additions. Templates scaffold projects
and are not a runtime dependency. The [release inventory](../eng/release-packages.json)
contains all nine packages and is also used by the candidate distribution gate.

Core selects Syntamark 1.2.0 at runtime; Generators and Analyzers embed the matched
Source artifact. Consumers do not add a direct Syntamark.Source reference. The
fixed pair retains `lithosharp-markdown/1` and does not opt the site into CommonMark.

```sh
dotnet new install LithoSharp.ProjectTemplates::2.1.3
dotnet tool install LithoSharp.Tool --version 2.1.3 --tool-path .tools
```

See the [Quick Start](quickstart.md) to create and serve a site, and [upgrade notes](migration-2.0.md) before updating an existing project.

For a candidate feed, install the exact supplied `<candidate-version>`
version and set the generated project's `LithoSharpVersion` to that same value
before restore. Do not mix stable and candidate LithoSharp packages. The feed
must supply the selected Syntamark runtime under its own 1.2.0 component version;
candidate suffixes do not relabel it.

## VS Code extension

```sh
code --install-extension htkym.lithosharp
```

The portable extension supports Windows x64, Linux x64 and macOS arm64. It includes the compiled extension, the framework-dependent language server with native assets for these systems, and MDX worker source with its lockfile. The .NET 10 runtime is required for the bundled server; building and serving sites also require the .NET 10 SDK, version 10.0.300 or later.

Use `LithoSharp: Select Project`, `LithoSharp: Build`, and `LithoSharp: Open Preview`. See the [extension guide](../extensions/lithosharp-vscode/README.md) for settings and troubleshooting. A downloaded VSIX can also be installed with `code --install-extension <path-to-vsix>`.

## MDX worker restore

Markdown-only sites and diagnostics need no Node.js. MDX requires Node.js 24.13.0 and an explicit worker restore.

For editor diagnostics, run `LithoSharp: Restore MDX Worker`. The extension copies bundled source into hash-named extension storage and restores the locked dependencies there with `npm ci --ignore-scripts --no-audit --no-fund`. Restored `node_modules` are not included in the extension. `lithosharp.workerDirectory` overrides the worker location; changing it requires `LithoSharp: Restart Language Server`.

For site builds, use the CLI's `restore-mdx` command as described in the [MDX guide](mdx.md). Building, serving and typing do not automatically restore npm dependencies. Workspace trust is required for commands that execute code or install dependencies.

## Manual language-server use

The bundled server runs as `dotnet LithoSharp.LanguageServer.dll` with its `deps.json`, `runtimeconfig.json`, managed dependencies, native assets and worker source kept together. It is not a separate NuGet package. Trimming, single-file publication and Native AOT are unsupported. See the [language-server protocol](language-server.md) for custom clients and [known limitations](known-limitations.md) for platform and execution boundaries.
