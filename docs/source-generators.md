# Static content source generators

<a id="static-content-source-generators--静的コンテンツのsource-generator"></a>

The LithoSharp 3.0.0 Docs template is the standard typed starting point: see
[Quick Start](quickstart.md) for generated `Guides.Binder`/references, content-root-relative
IDs with extensions, matching underscore exclusions and runtime catalog routes.
The snippets below describe individual generator contracts; their illustrative
ID conventions are not a replacement for that template's recursive input rules.

## Package setup

Use matching Core and Generators versions. The examples target 3.0.0; the
[version matrix](../eng/version-matrix.json) distinguishes the selected development
candidate from formal release availability.

Reference the runtime package normally and add `LithoSharp.Generators` as a private
analyzer. Its NuGet package imports the `AdditionalFiles` metadata contract
automatically.

```xml
<PackageReference Include="LithoSharp" Version="3.0.0" />
<PackageReference Include="LithoSharp.Generators" Version="3.0.0" PrivateAssets="all" />
```

<a id="declaration-and-inputs--宣言と入力"></a>

## Declaration and inputs

The declaration must be a top-level, nongeneric `static partial` class. The attribute
specifies the front matter type, generated page-reference type, and stable collection
ID.

```csharp
[StaticContentCollection(
    typeof(ArticleFrontMatter),
    typeof(ContentEntry<ArticleFrontMatter, string>),
    "articles",
    EmitJsonSchema = true)]
public static partial class GeneratedArticles;
```

Each Markdown input needs all three metadata values. A route ending in `/` creates a
directory-index route; other routes create file routes.

```xml
<AdditionalFiles Include="typed-content\**\*.md">
  <LithoSharpCollection>articles</LithoSharpCollection>
  <LithoSharpId>%(Filename)%(Extension)</LithoSharpId>
  <LithoSharpRoute>articles/%(Filename)/</LithoSharpRoute>
</AdditionalFiles>
```

`LithoSharpCollection` must match the declaration, `LithoSharpId` is the stable entry
ID, and `LithoSharpRoute` uses the runtime route rules. Inputs must be inside
`MSBuildProjectDirectory`. Member names come from the project-relative path, with path
and punctuation separators converted to underscores.
Use the same entry IDs as the runtime loader. The Docs sample includes the file
extension because its Markdown loader uses relative filenames as IDs.

<a id="generated-api--生成api"></a>

## Generated API

- `Binder`: `IContentFrontMatterBinder<TFrontMatter>` compatible with reflection binding.
- `SchemaJson`: a Draft 2020-12 JSON Schema string, always generated.
- `Catalog`: the immutable `StaticContentCatalog` with declared entry IDs, input membership and routes.
- `Pages.<name>`: immutable `PageRef<TPage>` with a stable page ID and route.
- `Entries.<name>`: immutable `ContentRef<ContentEntry<TFrontMatter, string>>`.
- `Ids.<name>`: the corresponding `ContentEntryId`.
- `WriteJsonSchema(path)`: generated only with `EmitJsonSchema = true`; writes UTF-8 without a BOM at runtime.

`PageRef<TPage>` and `ContentRef<TEntry>` expose stable IDs and a validated `SiteRoute`
without setters. `GetUrl()` returns the declared URL; `GetUrl(site.BaseUrl)` rebases it
for a deployed subpath. Assigning an unrelated generic reference type is a C# error.

## Checked discovery and input profiles

Set an explicit `InputRoot` on `StaticContentCollectionAttribute` and register the
same inputs for compiler and runtime discovery. Call
`MarkdownContentCollectionLoader<T>.FromCatalog(GeneratedArticles.Catalog,
projectDirectory, publicationMapper, GeneratedArticles.Binder)` to check the runtime
input set before binding. Added, missing, moved or reclassified inputs report
`LSC0010`; rebuild the catalog after intentional membership changes.

The Markdown profile excludes underscore-prefixed files and directories, normalizes
input identity to NFC, and rejects case-only or normalized collisions on every
host. Physical paths retain their original spelling. `ContentInputProfile.OpenKnowledgeFormat`
uses different discovery rules and requires an explicit bundle root; the ordinary
Markdown loader rejects that profile. Use the dedicated OKF consumer for its
reserved index/log inputs, concepts and auxiliary files.

`Site` and `Variant` can scope declarations; matching `LithoSharpSite` and
`LithoSharpVariant` AdditionalFiles metadata select the corresponding collection.
A static catalog describes inputs and does not prove that a runtime collection
was registered or published. Dynamic routes and publication decisions still need
the existing runtime checks.

<a id="diagnostics-and-dynamic-content--診断と動的コンテンツ"></a>

## Diagnostics and dynamic content

| ID | Compile-time error |
| --- | --- |
| `LSG001` | Invalid declaration, collection ID, or declared type |
| `LSG002` | Missing metadata, unreadable or out-of-project input, or unavailable front matter boundaries |
| `LSG003` | Duplicate collection or entry ID, or generated name |
| `LSG004` | Invalid or conflicting route |
| `LSG005` | Malformed YAML or unknown field |
| `LSG006` | Value incompatible with the front matter type |

All `LSG` diagnostics default to errors. Analyzer severity can be configured in
`.editorconfig`, for example `dotnet_diagnostic.LSG005.severity = warning`.
Changing severity does not create references for rejected inputs or bypass runtime
validation. DateOnly/TimeOnly static validation requires a compiler host that
provides those runtime types; an unsupported host reports `LSG001` and should use
a current `dotnet build`.

Moving or deleting an input removes its member; old references then produce normal
compiler errors such as `CS0117`. A wrong generic assignment produces `CS0029`.
The generator validates only explicit compiler inputs. Content discovered by a dynamic
loader retains the existing loader and site-generation diagnostics at runtime.
Defaults assigned by constructor code or a referenced assembly cannot be established
from source syntax. Missing-value checks for those defaults remain in the runtime
binder; the schema only requires fields whose requirement is known statically.
