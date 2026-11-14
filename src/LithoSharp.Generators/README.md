# LithoSharp.Generators

[English](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Generators/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Generators/README.ja.md)

Connect your .NET documentation schema to generated binders, catalogs and typed references during C# builds. These Roslyn incremental generators inspect explicitly declared static Markdown collections; missing required fields are compiler diagnostics rather than a late layout failure.
Use matching LithoSharp 3.0.0 packages. Reference `LithoSharp` normally, then add this package as a private analyzer:

```xml
<PackageReference Include="LithoSharp.Generators" Version="3.0.0" PrivateAssets="all" />
```

Declare a top-level, nongeneric `static partial` class with
`StaticContentCollectionAttribute`. Include Markdown as `AdditionalFiles` with all
three metadata values:

```xml
<AdditionalFiles Include="content\**\*.md">
  <LithoSharpCollection>guides</LithoSharpCollection>
  <LithoSharpId>%(Filename)</LithoSharpId>
  <LithoSharpRoute>guides/%(Filename)/</LithoSharpRoute>
</AdditionalFiles>
```

The annotated class receives `Binder`, `SchemaJson`, `Catalog`, and nested `Pages`, `Entries`,
and `Ids` classes. Set `EmitJsonSchema = true` to also generate
`WriteJsonSchema(string outputPath)` for an optional runtime export. `SchemaJson`
exists regardless of that option.

`Catalog` snapshots the validated AdditionalFiles membership, including the existing
collection ID, entry IDs, project-relative source paths, and routes. `Pages`, `Entries`,
and `Ids` refer to this same snapshot. A route is declared once in AdditionalFiles;
runtime code can use `entry => Guides.Catalog.GetRoute(entry.Id)` as the existing
`ContentRouteConvention` instead of maintaining a second route list. An undeclared
entry ID throws `KeyNotFoundException`. Runtime entry IDs must match the declared
IDs: the Markdown loader uses its input-root-relative path including the extension,
so a flat input can use `%(Filename)%(Extension)` as `LithoSharpId`. A catalog does
not rename entries produced by the original loader constructor. Paths in the catalog are relative to
`MSBuildProjectDirectory`, which can differ from a runtime loader's input root.

To migrate an existing collection to checked discovery, set `InputRoot = "content"`
on its attribute and include `content/**/*` as AdditionalFiles. The shared Markdown
policy selects `.md` case-insensitively and excludes underscore-prefixed files and
directories before schema validation. It normalizes paths to NFC and rejects
case-only and normalized path collisions on every host. Keep the existing ID and
route metadata; use the catalog-based loader to preserve both at runtime:

```csharp
var loader = MarkdownContentCollectionLoader<GuideFrontMatter>.FromCatalog(
    Guides.Catalog, projectDirectory,
    entry => new PageMetadata(entry.FrontMatter.Title),
    frontMatterBinder: Guides.Binder);
```

The loader reports `LSC0010` for added, missing, moved or differently classified
inputs before binding. Rebuild the catalog from the same declaration after input
membership changes. The original loader constructor and declarations without an
`InputRoot` remain available for gradual migration; their dynamically discovered
inputs are outside a checked static set.

`InputProfile = ContentInputProfile.OpenKnowledgeFormat` requires an explicit
bundle root and preserves every file. Exact lowercase `index.md` and `log.md` are
reserved at every level; other Markdown files, including README, AGENTS and
underscore-prefixed names, are concepts. Other files are auxiliary inputs.
Catalog entries expose `InputPath`, `Kind` and the concept-only `ConceptId`, separate
from project paths and public routes. This discovery profile does not validate OKF
metadata and leaves generated route lookup coverage Open. Use a dedicated OKF
consumer; the ordinary typed Markdown loader rejects this profile.

For separate sites or variants, set `Site` and `Variant` on
`StaticContentCollectionAttribute`, then supply matching `LithoSharpSite` and
`LithoSharpVariant` metadata on the corresponding AdditionalFiles. Empty values
retain the existing default scope. Collection identities and output conflicts are
checked within each site/variant scope; a missing or different scope produces
`LSG002`, rather than silently joining another site's collection. A consuming site
must choose its own scoped catalog; these identities do not select runtime settings.

The catalog describes declared inputs. It does not prove that a runtime collection
was registered, loaded completely, or published, and it does not establish Closed
route coverage. Dynamic loaders, custom resolvers, and conditional publication still
require runtime validation. Compile-time YAML/schema diagnostics `LSG005` and
`LSG006` remain owned solely by this generator; an analyzer must not emit them again.

NuGet imports the required analyzer metadata through `buildTransitive`. When using a
source-tree `ProjectReference`, set `OutputItemType="Analyzer"` and
`ReferenceOutputAssembly="false"`, then import this project's
`buildTransitive/LithoSharp.Generators.props`; the Docs sample shows the complete
project-reference setup. Do not add a normal assembly reference to the generator.

Diagnostics `LSG001` through `LSG006` cover invalid declarations and input metadata,
duplicate identities or generated names, invalid or conflicting routes, malformed
YAML or unknown fields, and values that cannot bind to the declared front matter
type. Dynamic loader data remains a runtime validation concern.

The generated names `Binder`, `SchemaJson`, `Catalog`, `Pages`, `Entries`, `Ids`,
and the private `BinderImpl` are reserved within the annotated partial class.
The collection type itself and existing members in any partial declaration must
not use these names. `WriteJsonSchema` is also reserved when `EmitJsonSchema` is
enabled. Conflicts produce `LSG001` at the source declaration before generation,
including when no AdditionalFiles are present.

## Optional static route lookup

`[StaticContentCollection(typeof(Front), typeof(string), "guides", EmitStaticSiteManifest = true)]`
also emits `Guides.Manifest`, an immutable exact route lookup created from the same `Guides.Catalog` used by generated references. `Guides.Manifest.GetUrl("/intro/")` returns a `SiteUrl`; a missing exact path throws `KeyNotFoundException`. This optional member closes only that selected lookup universe. Catalogs, arbitrary site registrations, HTML anchors and assets do not gain a Closed publication claim. The default is off, so existing declarations and generated member names keep their prior behavior.

The compiler fingerprint records original C# and AdditionalText buffers, relevant metadata/properties and reference assembly MVIDs. A fresh, verified generated manifest permits Analyzer `LSA1201` for definitely missing paths; changed inputs cause `LSA1205` Info and defer absence. Any generator declaration/input/schema error makes the emitted lookup Open. Dynamic membership should use `StaticSiteManifest.Open(catalog, resolver)`; the Analyzer never executes a resolver or getter. `StaticSiteManifest.Create` alone or a handwritten provenance attribute does not establish compiler evidence. These APIs describe exact public-path membership, not output publication or ordinary HTML link resolution.

## Markdown parsing

The generator embeds the matched Syntamark 2 Source artifact for Markdown and
native YAML parsing. It does not depend on YamlDotNet.
It does not load the Core or Syntamark runtime DLL into the compiler host.
See the [migration guide](https://github.com/Htkym/lithosharp/blob/main/docs/migration-3.0.md)
for attributes, supported YAML and package versions.

The header-only `frontmatter-scan/1` analysis stops at a closing delimiter. Its
1,048,576 UTF-16-code-unit header limit excludes the following body. `Closed`
establishes header delimiters and Unicode, not YAML/schema validity or body validity.
YAML/schema errors are `LSG005/006`; unavailable header analysis is `LSG002` and
keeps the generated lookup Open. Original BOM/CRLF-aware locations are retained.
Invalid body Unicode can also leave the manifest fingerprint unavailable even
when header/schema validation succeeds, so it does not establish a Closed lookup.

## Fixed full HTML plans

`FixedHtmlSiteAttribute` and `FixedHtmlPageAttribute` opt a static partial class into the limited fixed-file owner. The generated `FixedHtmlPlan` calls only the declared page methods, snapshots their HTML as strict UTF-8 generated assets, and publishes through `FixedHtmlSitePlan.GenerateAsync`. Invalid declaration shapes and duplicate output files are `LSG001` errors. Dynamic page methods are valid runtime content but are not Closed compiler evidence. No site-wide anchor manifest or closure for arbitrary factories, layouts, extensions or Markdown headings is emitted. See the [LSA1202 subset and example](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Analyzers/README.md#lsa1202).
