# 静的コンテンツのソースジェネレーター

型付きの構成を始める標準の方法は、LithoSharp 3.0.0 の Docs テンプレートです。
[クイックスタート](quickstart.ja.md)で、生成する `Guides.Binder` と参照、拡張子を含む入力ルート相対 ID、
一致するアンダースコアの除外規則、実行時のカタログルートを確認してください。
以下のコードは個々のジェネレーターの契約を示します。例の ID 規則はテンプレートの再帰的な入力規則に代わるものではありません。

## パッケージの設定

Core と Generators は同じバージョンを使います。例の対象は 3.0.0 です。
[バージョン一覧](../eng/version-matrix.json)は、選定した開発候補と、正式リリースの提供状況を区別します。

ランタイムパッケージを通常どおり参照し、`LithoSharp.Generators` を private analyzer として追加します。
NuGet パッケージは `AdditionalFiles` のメタデータの契約を自動的に読み込みます。

```xml
<PackageReference Include="LithoSharp" Version="3.0.0" />
<PackageReference Include="LithoSharp.Generators" Version="3.0.0" PrivateAssets="all" />
```

## 宣言と入力

宣言は、トップレベルの、ジェネリックではない `static partial` クラスにします。
属性には、フロントマターの型、生成するページ参照の型、固定したコレクション ID を指定します。

```csharp
[StaticContentCollection(
    typeof(ArticleFrontMatter),
    typeof(ContentEntry<ArticleFrontMatter, string>),
    "articles",
    EmitJsonSchema = true)]
public static partial class GeneratedArticles;
```

各 Markdown 入力には、三つのメタデータ値がすべて必要です。
末尾が `/` のルートはディレクトリインデックス、それ以外はファイルルートになります。

```xml
<AdditionalFiles Include="typed-content\**\*.md">
  <LithoSharpCollection>articles</LithoSharpCollection>
  <LithoSharpId>%(Filename)%(Extension)</LithoSharpId>
  <LithoSharpRoute>articles/%(Filename)/</LithoSharpRoute>
</AdditionalFiles>
```

`LithoSharpCollection` は宣言と一致させ、`LithoSharpId` は固定した項目 ID とします。
`LithoSharpRoute` は、実行時と同じルート規則を使います。入力は `MSBuildProjectDirectory` の内側に置きます。
メンバー名はプロジェクト相対パスを基に、パスや句読点の区切りをアンダースコアに変換して生成します。
項目 ID は実行時のローダーとそろえます。Docs サンプルは、Markdown ローダーが相対ファイル名を ID に使うため、拡張子を含めます。

## 生成 API

- `Binder` は、リフレクションによるバインドと互換性のある `IContentFrontMatterBinder<TFrontMatter>` です。
- `SchemaJson` は、常に生成する Draft 2020-12 の JSON Schema 文字列です。
- `Catalog` は、宣言した項目 ID、入力集合、ルートを持つ不変の `StaticContentCatalog` です。
- `Pages.<name>` は、固定したページ ID とルートを持つ不変の `PageRef<TPage>` です。
- `Entries.<name>` は、不変の `ContentRef<ContentEntry<TFrontMatter, string>>` です。
- `Ids.<name>` は、対応する `ContentEntryId` です。
- `WriteJsonSchema(path)` は、`EmitJsonSchema = true` の場合だけ生成し、実行時に BOM なしの UTF-8 でスキーマを書きます。

`PageRef<TPage>` と `ContentRef<TEntry>` は、セッターを持たず、固定した ID と検証済みの `SiteRoute` を公開します。
`GetUrl()` は宣言された URL を返し、`GetUrl(site.BaseUrl)` は配置先のサブパスに合わせます。
無関係なジェネリック参照型への代入は、C# のエラーです。

## 入力集合の検証と入力プロファイル

`StaticContentCollectionAttribute` に `InputRoot` を明示し、コンパイル時と実行時の検出に同じ入力を登録します。
バインド前に実行時の入力集合を検査するには、
`MarkdownContentCollectionLoader<T>.FromCatalog(GeneratedArticles.Catalog, projectDirectory, publicationMapper, GeneratedArticles.Binder)` を呼びます。
入力の追加、欠落、移動、分類の変更は `LSC0010` です。意図した構成変更後はカタログを再ビルドしてください。

Markdown のプロファイルは、アンダースコアで始まるファイルとディレクトリを除外し、入力の識別情報を NFC に正規化します。
どのホストでも、大文字・小文字だけの違いや正規化後の衝突を拒否します。物理パスは元の表記を保持します。
`ContentInputProfile.OpenKnowledgeFormat` は、異なる検出規則と、明示したバンドルルートを必要とします。
通常の Markdown ローダーはこのプロファイルを拒否します。
予約されたインデックス・ログ入力、概念、補助ファイルには、専用の OKF 利用経路を使います。

宣言は `Site` と `Variant` で範囲を分けられます。
対応する `LithoSharpSite` と `LithoSharpVariant` の AdditionalFiles メタデータが、コレクションを選択します。
静的カタログは入力を説明するものであり、実行時のコレクションの登録や公開を証明しません。
動的ルートと公開の判断には、引き続き既存の実行時検査が必要です。

## 診断と動的コンテンツ

| ID | コンパイル時のエラー |
|---|---|
| `LSG001` | 不正な宣言、コレクション ID、宣言した型。 |
| `LSG002` | メタデータの欠落、読み取り不能・プロジェクト外の入力、利用できないフロントマターの境界。 |
| `LSG003` | コレクション ID、項目 ID、生成名の重複。 |
| `LSG004` | 不正または競合するルート。 |
| `LSG005` | 不正な YAML または未知のフィールド。 |
| `LSG006` | フロントマターの型と互換性がない値。 |

すべての `LSG` 診断は既定でエラーです。
`.editorconfig` では、`dotnet_diagnostic.LSG005.severity = warning` などで重大度を設定できます。
重大度を変えても、拒否した入力の参照は生成せず、実行時の検証も省略しません。
DateOnly と TimeOnly の静的検証には、その実行時の型を提供するコンパイラーホストが必要です。
対応しないホストは `LSG001` を報告するので、現在の `dotnet build` を使ってください。

入力の移動や削除でメンバーが消えると、古い参照には `CS0117` などの通常のコンパイルエラーが出ます。
不正なジェネリックの代入は `CS0029` です。

ジェネレーターが検証するのは、明示したコンパイラー入力だけです。
動的ローダーが検出するコンテンツは、実行時の既存ローダーとサイト生成の診断で検証します。
コンストラクターのコードや参照先アセンブリが設定する既定値は、ソース構文だけでは確定できません。
その既定値に関する欠落の検査は実行時のバインダーに残し、スキーマでは静的に必須と分かるフィールドだけを必須にします。
