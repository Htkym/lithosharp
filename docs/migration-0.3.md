# Upgrade from 0.2.0 to 0.3.0

[日本語](migration-0.3.ja.md)

0.3.0 is a stable release in the 0.x series, not a 1.0 API stability
promise. Future minor releases may change public APIs. The published 0.2.0
package remains available; this release does not replace or unlist it.

## Update an existing Markdown site

For the 0.3.1 patch, regenerate and review custom CSS against the refreshed
Docs/Blog themes. Theme switching is enabled by default; use
`Theme = new SiteThemeOptions { EnableThemeSwitching = false }` in your
`SiteCustomization` for fixed light colors. See [theme configuration](layout-css-contract.md#theme-switching--テーマ切り替え)
for fixed dark colors and browser requirements. Routes and existing signatures
remain unchanged.

Before:

```xml
<PackageReference Include="LithoSharp" Version="0.2.0" />
```

After:

```xml
<PackageReference Include="LithoSharp" Version="0.3.0" />
```

Run `dotnet restore --force-evaluate`, review the lockfile, then build and generate
into a new output directory before deploying it. Both versions target .NET 10.
The candidate was verified with SDK 10.0.300. Markdown-only builds require neither
Node.js nor React. Core adds AngleSharp 1.7.3; Markdig 1.3.2, YamlDotNet 18.0.0,
SkiaSharp 3.119.4 and its Linux native package retain their dependency versions.

## Source and binary compatibility

Package validation against the actual NuGet 0.2.0 found no removed or incompatible
public API signatures. An isolated consumer using the old reader, generator,
Docs, Blog and custom-template APIs compiled unchanged against 0.3.0. Its binary
compiled against 0.2.0 also generated all three sites with the 0.3.0 runtime and
dependency graph. No new obsolete warnings or namespace moves were found.
This covers the tested APIs and inputs, not every possible custom extension.

The existing `MarkdownPostReader`, `SiteGenerator.GenerateAsync`,
`SiteCustomization` and `ISiteTemplate` entry points remain usable. The default
template is still Docs; select `BlogSiteTemplate` explicitly for a blog.
No existing Core API was moved into a required replacement package.

## Output and behavior changes

The upgrade fixture retained all 33 public artifact paths across Docs, Blog and
custom templates, including nested Unicode routes, extra pages, CSS, JavaScript,
search, RSS, sitemap and `llms.txt`. Text, links and major landmarks were retained.
Output bytes are not a compatibility guarantee:

- Text output no longer includes the old UTF-8 BOM; line endings are normalized.
- Missing social images no longer produce dangling Open Graph or Twitter image
  metadata. The Twitter card becomes `summary` when no image is emitted.
- Search timestamps use the resolved build timestamp; the search index query
  uses a content fingerprint. Do not parse the old timestamp-shaped query value.
- Output ownership metadata is added. Do not edit or distribute private cache or
  ownership files as site content.

Set `SiteGenerationOptions.BuildTimestamp` or a valid `SOURCE_DATE_EPOCH` for
repeatable builds. Invalid timestamp configuration fails instead of silently
falling back to the current time.

Publication fields are now active: `draft: true`, future `publish_from`, expired
`publish_until`, and an `environments` list excluding the current environment
remove a post from output and derived artifacts. The default environment is
`Production`. NuGet 0.2.0 ignored these fields; the four upgrade probes changed
from one published post to zero. If old content used these keys as unrelated
metadata, rename or correct them before upgrading. `PostCount` now reflects the
published count. This is a documented behavior-breaking change for those inputs.

Stricter safety validation can reject previously tolerated unsafe inputs. A
tested `ftp:` base URL was accepted by 0.2.0 and rejected by 0.3.0. Routes
must stay beneath output and cannot use reserved names, ambiguous encodings,
case-only collisions or file/directory conflicts. Input, public assets and caches
must not overlap output. See the [route contract](compatibility-contract.md) and
[asset rules](assets-and-images.md). Correct these inputs rather than disabling
validation. Treat reliance on such inputs as a behavior-breaking migration.
Case-only output collisions were already rejected; their exception is now the
structured `SiteRouteValidationException`, still an `InvalidOperationException`.
Update code that checks the exact exception type or old message text.

The legacy YAML reader keeps its permissive unknown-key behavior. Strict duplicate
and unknown-key diagnostics belong to the opt-in typed collection binders and
MDX/documentation loaders. Moving a legacy collection to these loaders requires
matching its declared schema; it is not an automatic configuration conversion.

## Cache, themes and optional features

Generate into a fresh directory for the first comparison. With `clean: false`,
unrelated files and older trees without proven ownership are preserved; do not
expect an upgrade to remove every stale 0.2.0 artifact. Preserve ownership
sidecars with their corresponding local output during normal incremental builds.
Caches are disposable and are not ownership evidence. See
[incremental builds](incremental-builds.md) for invalidation and transaction rules.

Existing theme CSS and semantic landmarks remain covered by the
[layout contract](layout-css-contract.md). Compare custom selectors and snapshots
semantically rather than requiring identical serialized HTML. Search, versioned
Docs and locale variants are explicit configuration; upgrading a legacy site does
not silently enable them or change its `.html` URLs.

The optional packages `LithoSharp.Generators`, `LithoSharp.Images`,
`LithoSharp.Testing`, `LithoSharp.Mdx`, `LithoSharp.Tool` and
`LithoSharp.ProjectTemplates` are supplied at 0.3.0. They are new published package
IDs for this release, not packages a 0.2.0 Core consumer must install. Keep their
versions aligned. Generated code is rebuilt from your `AdditionalFiles`; use
`PrivateAssets="all"` for the generator and review its build diagnostics.

The CLI and templates are optional new entry points. Follow the
[Quick Start](quickstart.md); an existing console application remains supported.
For MDX only, use Node.js 24.13.0 and explicitly restore the packaged worker with
`lithosharp restore-mdx`. The lockfile pins MDX 3.1.1, React 19.2.4 and esbuild
0.25.12. Normal site builds do not install npm packages. MDX executes trusted
code: read [Known limitations](known-limitations.md) before enabling it.

## Roll back

Restore the 0.2.0 package reference and its lockfile, rebuild the application, and
generate into a separate clean directory. Do not run the older writer over an
output transaction or cache created by the newer version. Restore a complete
previous deployment, including assets; retain hashed assets needed by open
browser sessions. New optional APIs and MDX sites cannot run on Core 0.2.0.
