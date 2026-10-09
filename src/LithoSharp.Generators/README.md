# LithoSharp.Generators

Roslyn incremental generators for explicitly declared static Markdown collections.
Reference `LithoSharp` normally, then add this package as a private analyzer:

```xml
<PackageReference Include="LithoSharp.Generators" Version="1.1.0" PrivateAssets="all" />
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

## Markdownの解析

現在の開発候補は、Syntamark.Source [2.0.0-preview.3]のportable sourceを既定でコンパイルします。解析に使うYamlDotNet [18.1.0]もanalyzer payloadへ同梱します。Core runtimeやSyntamarkのruntime DLLをcompiler hostへ読み込ませません。

source-treeからビルドする場合、Directory.Build.propsが固定版を選びます。SyntamarkSourceVersionは選択済みのruntime/source pairと同じ版にする必要があり、不一致はビルド時に拒否されます。
