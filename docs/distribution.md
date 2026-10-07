# Distribution and installation

[日本語](distribution.ja.md)

LithoSharp 1.1.0 is available on NuGet. [LithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp) is available on the VS Code Marketplace with the identity `htkym.lithosharp`.

## NuGet packages

Use matching 1.1.0 versions of the packages your project needs: `LithoSharp`, `LithoSharp.Generators`, `LithoSharp.Images`, `LithoSharp.Tool`, `LithoSharp.Testing`, `LithoSharp.Mdx`, and `LithoSharp.ProjectTemplates`. The tool and templates are separate packages.

```sh
dotnet new install LithoSharp.ProjectTemplates::1.1.0
dotnet tool install LithoSharp.Tool --version 1.1.0 --tool-path .tools
```

See the [Quick Start](quickstart.md) to create and serve a site, and [upgrade notes](release-1.1.md) before updating an existing project.

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
