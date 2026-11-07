# LithoSharp.Generators

Roslyn incremental generators for explicitly declared static Markdown collections.
Reference `LithoSharp` normally, then add this package as a private analyzer:

```xml
<PackageReference Include="LithoSharp.Generators" Version="2.1.3" PrivateAssets="all" />
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
not rename entries produced by a loader. Paths in the catalog are relative to
`MSBuildProjectDirectory`, which can differ from a runtime loader's input root.

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

The generator compiles the portable source from the fixed Syntamark.Source pair selected by `Directory.Build.props`, with YamlDotNet 18.1.0 in its analyzer payload. The generator uses the published `1.2.0` runtime/Source pair from NuGet. `SyntamarkSourceVersion` must match the selected runtime/source pair; a mismatch fails the build. Core and the Syntamark runtime DLL are not loaded into the compiler host. See the [current version and body-analysis limits](https://github.com/Htkym/lithosharp/blob/main/docs/migration-2.0.md#package-versions).

The actual `StaticContentGenerator` path passes the original immutable AdditionalText buffer to `MarkdownSourceHostAdapter`, verifies the parser/profile/options identity, and calls `ScanFrontMatter`. Its separate `frontmatter-scan/1` result contains raw header ranges and status, with no body facts, YAML tree, document hash or full-analysis coverage. It stops recognizing input at a closing delimiter, so malformed, deeply nested or oversized bodies do not consume the header budget. The default header input limit is 1,048,576 UTF-16 code units; cancellation propagates.

`Closed` establishes header delimiters and Unicode, not valid YAML or schema. Existing YamlDotNet validation reads the original `YamlSpan`, excluding delimiter lines, and reports `LSG005/006` in the original BOM/CRLF-aware UTF-16 coordinates. `Unknown` or missing required ranges report `LSG002` and keep the generated lookup Open; known absence or confirmed EOF untermination retains its existing strict diagnostic. No separate normalized buffer or local boundary scanner is used.

The manifest fingerprint still covers the original input independently. Invalid body Unicode can leave that fingerprint unavailable even when header/schema validation succeeds, so this does not certify a Closed lookup or validate the body. The compatibility full-facts adapter remains separate from the header-only path.

## Fixed full HTML plans

`FixedHtmlSiteAttribute` and `FixedHtmlPageAttribute` opt a static partial class into the limited fixed-file owner. The generated `FixedHtmlPlan` calls only the declared page methods, snapshots their HTML as strict UTF-8 generated assets, and publishes through `FixedHtmlSitePlan.GenerateAsync`. Invalid declaration shapes and duplicate output files are `LSG001` errors. Dynamic page methods are valid runtime content but are not Closed compiler evidence. No site-wide anchor manifest or closure for arbitrary factories, layouts, extensions or Markdown headings is emitted. See the [LSA1202 subset and example](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Analyzers/README.md#lsa1202).
