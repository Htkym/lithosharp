# LithoSharp.Generators

Roslyn incremental generators for explicitly declared static Markdown collections.
Reference `LithoSharp` normally, then add this package as a private analyzer:

```xml
<PackageReference Include="LithoSharp.Generators" Version="0.2.0" PrivateAssets="all" />
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

The annotated class receives `Binder`, `SchemaJson`, and nested `Pages`, `Entries`,
and `Ids` classes. Set `EmitJsonSchema = true` to also generate
`WriteJsonSchema(string outputPath)` for an optional runtime export. `SchemaJson`
exists regardless of that option.

NuGet imports the required analyzer metadata through `buildTransitive`. When using a
source-tree `ProjectReference`, set `OutputItemType="Analyzer"` and
`ReferenceOutputAssembly="false"`, then import this project's
`buildTransitive/LithoSharp.Generators.props`; the Docs sample shows the complete
project-reference setup. Do not add a normal assembly reference to the generator.

Diagnostics `LSG001` through `LSG006` cover invalid declarations and input metadata,
duplicate identities or generated names, invalid or conflicting routes, malformed
YAML or unknown fields, and values that cannot bind to the declared front matter
type. Dynamic loader data remains a runtime validation concern.
