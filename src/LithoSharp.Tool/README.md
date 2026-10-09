# LithoSharp.Tool

.NET 10 CLI for C# static sites. Requires the .NET SDK and ASP.NET Core shared framework.

## Preflight

The preflight commands below are available in the 2.0.0 development candidate. They are not included in the published 1.1.0 Tool. Install the exact reviewed candidate Tool package from its candidate feed and use matching candidate Core/MDX packages for trusted project validation.

`lithosharp preflight --mode static --input page.md --input page.mdx --format json` inspects only the explicitly supplied UTF-8 source buffers. It does not evaluate MSBuild, a factory, imported JavaScript modules, project config, plugins, SSR or a bundler. MDX uses the existing analysis-only worker; restore its pinned dependencies explicitly and use `--worker-directory` to select that installation when needed. No installation or Markdown fallback occurs during preflight.

`lithosharp preflight site.csproj --mode trusted --input page.md --format json` explicitly admits project compilation and factory/catalog code. Source Error stops before compilation or factory activation. Compiler failures retain structured SARIF rule IDs and positions. After compilation the selected source files are re-read, then a separate host acquires the actual catalog and validates its routes before any renderer is called. The command never publishes output. Unknown extension prepare hooks remain Deferred and are not executed. Final HTML, template-owned output, assets, dynamic code and the full site universe remain outside this snapshot; a successful result means no Error in the reported phase.

Library callers can use `SitePreflight.InspectMarkdown` or the MDX analysis-only session, compose `SitePreflight.FromDiagnostics`, and call `SiteGenerator.PreflightFactoryAsync`. `PreflightCatalogAsync` inspects an already acquired definition. Inspectors implementing `ISiteCatalogPreflightExtension` are explicitly trusted nonrendering catalog code. Preflight snapshots do not authorize later rendering against changed inputs. Normal generation retains its runtime guards and final HTML publication gate; attached source or extension Error diagnostics stop before subsequent render work.

The following install and site creation example uses the published 1.1.0 packages and their existing commands:

```sh
dotnet tool install LithoSharp.Tool --version 1.1.0 --tool-path .tools
dotnet new install LithoSharp.ProjectTemplates::1.1.0
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

On Windows use `.tools/lithosharp.exe`. MDX is optional and requires Node.js
24.13.0 plus explicit worker restore; see the
[Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md).

Use `check --format text|json|sarif` for quality validation, `inspect --format json`
for build graph and cache information, and `clean` to remove unchanged owned files.
`build --clean` performs a full output replacement. Sites export one public
parameterless `ISiteFactory`; ordinary library callers can use the same definition.

See the [CLI guide](https://github.com/Htkym/lithosharp/blob/main/docs/cli.md) for
factory examples, output behavior, development server limitations and deployment support.
