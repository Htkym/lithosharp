# LithoSharp.Generators

[English](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Generators/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Generators/README.ja.md)

C# のビルド時に、.NET のドキュメント用スキーマからバインダー、カタログ、型付き参照を生成します。Roslyn の増分ジェネレーターが、明示的に宣言された静的な Markdown コレクションを検査します。必須フィールドの不足は後のレイアウト処理で失敗するのではなく、コンパイラー診断として報告されます。
同じバージョンの LithoSharp 3.0.0 パッケージを使用してください。`LithoSharp` は通常の参照として追加し、このパッケージは利用するプロジェクトだけの Analyzer として追加します。

```xml
<PackageReference Include="LithoSharp.Generators" Version="3.0.0" PrivateAssets="all" />
```

ジェネリックでないトップレベルの `static partial` クラスに `StaticContentCollectionAttribute` を指定します。Markdown を `AdditionalFiles` に含め、次の3つのメタデータをすべて指定してください。

```xml
<AdditionalFiles Include="content\**\*.md">
  <LithoSharpCollection>guides</LithoSharpCollection>
  <LithoSharpId>%(Filename)</LithoSharpId>
  <LithoSharpRoute>guides/%(Filename)/</LithoSharpRoute>
</AdditionalFiles>
```

属性を指定したクラスには、`Binder`、`SchemaJson`、`Catalog` と、入れ子の `Pages`、`Entries`、`Ids` クラスが生成されます。`EmitJsonSchema = true` を指定すると、任意で行う実行時の書き出し用に `WriteJsonSchema(string outputPath)` も生成されます。このオプションの指定にかかわらず `SchemaJson` は生成されます。

`Catalog` は検証済みの AdditionalFiles の構成をスナップショットとして保持します。内容は既存のコレクション ID、エントリー ID、プロジェクトからの相対ソースパス、ルートです。`Pages`、`Entries`、`Ids` は同じスナップショットを参照します。ルートは AdditionalFiles に一度だけ宣言し、実行時の既存の `ContentRouteConvention` として `entry => Guides.Catalog.GetRoute(entry.Id)` を使用できます。別のルート一覧を維持する必要はありません。未宣言のエントリー ID は `KeyNotFoundException` になります。実行時のエントリー ID は宣言済み ID と一致させる必要があります。Markdown ローダーは拡張子を含む入力ルートからの相対パスを使用するため、平坦な入力では `LithoSharpId` に `%(Filename)%(Extension)` を指定できます。カタログは元のローダーのコンストラクターが生成するエントリーを改名しません。カタログ内のパスは `MSBuildProjectDirectory` からの相対パスであり、実行時のローダーの入力ルートとは異なる場合があります。

既存コレクションを、入力構成を検査する方式へ移行するには、属性に `InputRoot = "content"` を設定し、`content/**/*` を AdditionalFiles に含めます。共通の Markdown ポリシーは `.md` を大文字と小文字を区別せずに選択し、スキーマ検証前にアンダースコアで始まるファイルとディレクトリを除外します。パスを NFC に正規化し、大文字と小文字だけが異なるパスや、正規化後に衝突するパスをすべてのホストで拒否します。既存の ID とルートのメタデータを保ち、カタログに基づくローダーで実行時も両方を維持します。

```csharp
var loader = MarkdownContentCollectionLoader<GuideFrontMatter>.FromCatalog(
    Guides.Catalog, projectDirectory,
    entry => new PageMetadata(entry.FrontMatter.Title),
    frontMatterBinder: Guides.Binder);
```

入力の追加、不足、移動、分類の変更があると、ローダーはバインド前に `LSC0010` を報告します。入力構成を変更した後は、同じ宣言からカタログを再ビルドしてください。段階的な移行のため、元のローダーのコンストラクターと、`InputRoot` を持たない宣言も引き続き使用できます。それらが動的に検出する入力は、検査済みの静的な入力集合には含まれません。

`InputProfile = ContentInputProfile.OpenKnowledgeFormat` は明示的なバンドルルートを必要とし、すべてのファイルを保持します。小文字で完全一致する `index.md` と `log.md` はすべての階層で予約されています。README、AGENTS、アンダースコアで始まる名前を含む、その他の Markdown ファイルは概念です。それ以外のファイルは補助入力です。カタログのエントリーはプロジェクトのパスや公開ルートとは別に、`InputPath`、`Kind`、概念だけが持つ `ConceptId` を公開します。この入力検出プロファイルは OKF メタデータを検証せず、生成されたルート検索の網羅範囲を Open のままにします。専用の OKF 利用側を使用してください。通常の型付き Markdown ローダーはこのプロファイルを拒否します。

サイトやバリアントを分けるには、`StaticContentCollectionAttribute` に `Site` と `Variant` を設定し、対応する AdditionalFiles に一致する `LithoSharpSite` と `LithoSharpVariant` のメタデータを指定します。空の値は既存の既定スコープを維持します。コレクションの同一性と出力の衝突は各 site／variant の範囲内で検査します。スコープがないか異なる場合は `LSG002` となり、別のサイトのコレクションに暗黙に加わることはありません。利用するサイトは自分のスコープに対応するカタログを選ぶ必要があります。これらの識別情報が実行時の設定を選択するわけではありません。

カタログが記述するのは宣言済みの入力です。実行時のコレクションが登録済みであること、完全に読み込まれたこと、公開されたことを証明せず、ルートの網羅範囲が Closed であることも証明しません。動的ローダー、カスタムリゾルバー、条件付きの公開は、引き続き実行時の検証が必要です。コンパイル時の YAML／スキーマ診断 `LSG005` と `LSG006` は、このジェネレーターだけが担当します。Analyzer が重複して報告してはいけません。

NuGet は必要な Analyzer メタデータを `buildTransitive` でインポートします。ソースツリーの `ProjectReference` を使用する場合は、`OutputItemType="Analyzer"` と `ReferenceOutputAssembly="false"` を設定し、このプロジェクトの `buildTransitive/LithoSharp.Generators.props` をインポートします。Docs サンプルに、プロジェクト参照の設定全体があります。ジェネレーターを通常のアセンブリ参照として追加しないでください。

診断 `LSG001` から `LSG006` は、無効な宣言や入力メタデータ、重複した識別情報や生成名、無効または衝突するルート、不正な YAML や不明なフィールド、宣言した front matter の型にバインドできない値を対象とします。動的ローダーのデータは引き続き実行時に検証します。

生成名の `Binder`、`SchemaJson`、`Catalog`、`Pages`、`Entries`、`Ids` と、private な `BinderImpl` は、属性を指定した partial クラス内で予約されています。コレクション型自体と、いずれかの partial 宣言にある既存メンバーに、これらの名前を使用してはいけません。`EmitJsonSchema` を有効にした場合は `WriteJsonSchema` も予約されます。衝突すると、AdditionalFiles がない場合も、生成前にソースの宣言位置で `LSG001` を報告します。

## 任意の静的ルート検索

`[StaticContentCollection(typeof(Front), typeof(string), "guides", EmitStaticSiteManifest = true)]` を指定すると、生成された参照と同じ `Guides.Catalog` から作る、不変で完全一致のルート検索用の `Guides.Manifest` も生成します。`Guides.Manifest.GetUrl("/intro/")` は `SiteUrl` を返し、完全一致するパスがなければ `KeyNotFoundException` になります。この任意のメンバーで Closed になるのは、選択した検索対象の範囲だけです。カタログ、任意のサイト登録、HTML アンカー、アセットの公開範囲が Closed になるわけではありません。既定は無効なので、既存の宣言と生成メンバー名は以前の動作を維持します。

コンパイラーのフィンガープリントには、元の C# と AdditionalText のバッファー、関連するメタデータ／プロパティ、参照アセンブリの MVID を記録します。検証済みで最新の生成マニフェストでは、確実に存在しないパスに対して Analyzer の `LSA1201` を報告できます。入力が変わると `LSA1205` の Info を報告し、不存在の判定を保留します。ジェネレーターの宣言／入力／スキーマにエラーがあれば、出力する検索対象は Open になります。動的な構成では `StaticSiteManifest.Open(catalog, resolver)` を使用してください。Analyzer がリゾルバーや getter を実行することはありません。`StaticSiteManifest.Create` 単体や、手書きの由来情報の属性は、コンパイラーによる検証の証明になりません。これらの API が示すのは公開パスの完全一致による所属であり、出力の公開や、通常の HTML リンクの解決ではありません。

## Markdown の解析

ジェネレーターは Markdown とネイティブ YAML の解析用に、対応する Syntamark 2 の Source 成果物を埋め込みます。YamlDotNet には依存しません。Core や Syntamark のランタイム DLL をコンパイラーホストに読み込みません。属性、対応する YAML、パッケージのバージョンは[移行ガイド](https://github.com/Htkym/lithosharp/blob/main/docs/migration-3.0.ja.md)を参照してください。

ヘッダーだけを対象とする `frontmatter-scan/1` の解析は、閉じ区切りで停止します。ヘッダーの上限である 1,048,576 UTF-16 コード単位に、後続の本文は含みません。`Closed` が証明するのはヘッダーの区切りと Unicode であり、YAML／スキーマや本文の妥当性ではありません。YAML／スキーマのエラーは `LSG005/006`、ヘッダーを解析できない場合は `LSG002` となり、生成される検索対象は Open のままです。元の BOM／CRLF に対応した位置を維持します。ヘッダー／スキーマの検証が成功しても、本文の Unicode が不正なためにマニフェストのフィンガープリントを取得できない場合があります。その場合、検索対象が Closed であることは証明できません。

## 固定された完全な HTML プラン

`FixedHtmlSiteAttribute` と `FixedHtmlPageAttribute` を指定すると、static partial クラスが限定された固定ファイル群の所有元になります。生成される `FixedHtmlPlan` は宣言したページメソッドだけを呼び、その HTML を厳密な UTF-8 の生成アセットとしてスナップショットに保持し、`FixedHtmlSitePlan.GenerateAsync` を通じて公開します。無効な宣言形式と出力ファイルの重複は `LSG001` のエラーになります。動的なページメソッドは有効な実行時コンテンツですが、コンパイラーによる Closed の証明にはなりません。サイト全体のアンカーマニフェストや、任意のファクトリー、レイアウト、拡張、Markdown 見出しの網羅性は生成しません。[LSA1202 の対応範囲と例](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Analyzers/README.ja.md#lsa1202)を参照してください。
