# Static content source generators / 静的コンテンツのSource Generator

## Package setup

Reference the runtime package normally and add `LithoSharp.Generators` as a private
analyzer. Its NuGet package imports the `AdditionalFiles` metadata contract
automatically.

```xml
<PackageReference Include="LithoSharp" Version="0.3.0" />
<PackageReference Include="LithoSharp.Generators" Version="0.3.0" PrivateAssets="all" />
```

For a source-tree project reference, use analyzer output and do not reference the
generator assembly at runtime:

```xml
<ProjectReference Include="..\src\LithoSharp.Generators\LithoSharp.Generators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
<Analyzer Include="$(NuGetPackageRoot)yamldotnet/18.0.0/lib/netstandard2.0/YamlDotNet.dll" />
<Import Project="../src/LithoSharp.Generators/buildTransitive/LithoSharp.Generators.props" />
```

NuGetではruntimeパッケージを通常参照し、`LithoSharp.Generators` をprivate analyzerとして
追加します。NuGetパッケージは `AdditionalFiles` 用のメタデータ契約を自動的に読み込みます。
ソースツリーの `ProjectReference` ではanalyzer出力を指定し、上記のpropsを明示的にimportします。
生成器のassemblyを実行時参照へ追加しないでください。
`ProjectReference`と`Analyzer`は`ItemGroup`内、`Import`は`Project`直下に置きます。
Place `ProjectReference` and `Analyzer` inside `ItemGroup`, and `Import` directly
under `Project`. NuGet consumers do not need the explicit YamlDotNet analyzer item.

## Declaration and inputs / 宣言と入力

The declaration must be a top-level, nongeneric `static partial` class. The attribute
specifies the front matter type, generated page-reference type, and stable collection
ID. 宣言はトップレベルで、ジェネリックではない `static partial` クラスにします。属性には
front matterの型、生成するページ参照の型、安定したcollection IDを指定します。

```csharp
[StaticContentCollection(
    typeof(ArticleFrontMatter),
    typeof(ContentEntry<ArticleFrontMatter, string>),
    "articles",
    EmitJsonSchema = true)]
public static partial class GeneratedArticles;
```

Each Markdown input needs all three metadata values. A route ending in `/` creates a
directory-index route; other routes create file routes. 各Markdown入力には3つの
メタデータを設定します。末尾が `/` のルートはdirectory indexになり、それ以外は
ファイルルートになります。

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

`LithoSharpCollection` は宣言と一致させ、`LithoSharpId` には安定したentry IDを指定します。
`LithoSharpRoute` には実行時と同じルート規則を適用します。入力は
`MSBuildProjectDirectory` 内に置きます。メンバー名はproject相対パスを基に、区切りを
アンダースコアへ変換して作られます。
entry IDは実行時のローダーとそろえます。Docsサンプルは、Markdownローダーが相対ファイル名を
IDに使うため、拡張子を含めています。

## Generated API / 生成API

- `Binder`: `IContentFrontMatterBinder<TFrontMatter>` compatible with reflection binding.
- `SchemaJson`: a Draft 2020-12 JSON Schema string, always generated.
- `Pages.<name>`: immutable `PageRef<TPage>` with a stable page ID and route.
- `Entries.<name>`: immutable `ContentRef<ContentEntry<TFrontMatter, string>>`.
- `Ids.<name>`: the corresponding `ContentEntryId`.
- `WriteJsonSchema(path)`: generated only with `EmitJsonSchema = true`; writes UTF-8 without a BOM at runtime.

`Binder` はreflection binderと同じ結果を返します。`Pages`、`Entries`、`Ids` は型付きの
既知ルートとIDを提供します。`SchemaJson` は常に生成されます。`WriteJsonSchema(path)` は
`EmitJsonSchema = true` の場合だけ生成され、実行時にBOMなしのUTF-8でschemaを書き出します。

`PageRef<TPage>` and `ContentRef<TEntry>` expose stable IDs and a validated `SiteRoute`
without setters. `GetUrl()` returns the declared URL; `GetUrl(site.BaseUrl)` rebases it
for a deployed subpath. Assigning an unrelated generic reference type is a C# error.

`PageRef<TPage>` と `ContentRef<TEntry>` はsetterを持たず、安定したIDと検証済みルートを
公開します。`GetUrl(site.BaseUrl)` は配置先のサブパスを反映します。異なるジェネリック型
への代入はC#のコンパイルエラーになります。

## Diagnostics and dynamic content / 診断と動的コンテンツ

| ID | Compile-time error / コンパイル時エラー |
| --- | --- |
| `LSG001` | Invalid declaration, collection ID, or declared type / 宣言、collection ID、型が不正 |
| `LSG002` | Missing metadata, unreadable input, or input outside the project / メタデータ不足、読み取り不能、project外の入力 |
| `LSG003` | Duplicate collection or entry ID, or generated name / collection、entry ID、生成名の重複 |
| `LSG004` | Invalid or conflicting route / 不正または競合するルート |
| `LSG005` | Malformed YAML or unknown field / 不正なYAMLまたは未知のフィールド |
| `LSG006` | Value incompatible with the front matter type / front matter型と互換性がない値 |

All `LSG` diagnostics default to errors. Analyzer severity can be configured in
`.editorconfig`, for example `dotnet_diagnostic.LSG005.severity = warning`.
Changing severity does not create references for rejected inputs or bypass runtime
validation. DateOnly/TimeOnly static validation requires a compiler host that
provides those runtime types; an unsupported host reports `LSG001` and should use
a current `dotnet build`.

`LSG`診断の既定の重大度はErrorです。`.editorconfig`の
`dotnet_diagnostic.LSG005.severity = warning`などで重大度を設定できます。
重大度を変えても、拒否した入力の参照は生成されず、実行時の検証も残ります。
DateOnly／TimeOnlyの静的検証には、その型を提供するコンパイラーホストが必要です。
対応しないホストではLSG001を出すため、新しい.NET SDKの`dotnet build`を使います。

Moving or deleting an input removes its member; old references then produce normal
compiler errors such as `CS0117`. A wrong generic assignment produces `CS0029`.
入力の移動や削除でメンバーが消えると、古い参照には `CS0117` などが出ます。異なる型への
代入は `CS0029` になります。

The generator validates only explicit compiler inputs. Content discovered by a dynamic
loader retains the existing loader and site-generation diagnostics at runtime.
Defaults assigned by constructor code or a referenced assembly cannot be established
from source syntax. Missing-value checks for those defaults remain in the runtime
binder; the schema only requires fields whose requirement is known statically.
生成器は明示した静的入力だけを検証します。動的ローダーが見つけるコンテンツは、既存の
loaderとサイト生成時の診断で実行時に検証します。
コンストラクターや参照先アセンブリが設定する既定値は、構文だけでは確定できません。
その場合の欠落値は実行時のbinderで検証し、schemaでは静的に必須と分かるフィールドを
必須として扱います。
