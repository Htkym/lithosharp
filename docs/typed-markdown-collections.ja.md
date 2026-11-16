# 型付き Markdown コレクション

`MarkdownContentCollectionLoader<TFrontMatter>` は、Markdown ファイルを `ContentCollection<TFrontMatter, string>` に読み込みます。
互換性用の `MarkdownPostReader` の基盤にある、型付きの置き換えです。

C# スキーマ、生成参照、Markdown、エディター、サイト、出力検査までの一連の手順は、
[Docs サンプル](../samples/LithoSharp.DocsSample/README.ja.md)を使ってください。
[ソースジェネレーターのガイド](source-generators.ja.md)はコンパイラー入力と生成バインダーを、このページは実行時の読み込みと生成を説明します。

既存のサイドバー、目次、前後のリンクを使って Docs として表示するには、読み込んだコレクションの
[`RenderDocs`](docs-template.ja.md) を使います。
LithoSharp 3.0.0 の Docs テンプレートは、この経路を用意しています。
コンパイル時と実行時の入力の一致、Quality との接続は、[クイックスタート](quickstart.ja.md)を参照してください。

## サイト生成との接続

`Collection` を使う前に `ContentLoadResult.IsSuccess` を確認し、`SiteGenerationOptions.ContentCollections` に登録します。
ジェネレーターは、各項目のルートと公開マッパーを評価し、公開するすべてのルートと出力の所有権を検証してから、
不可分の出力トランザクションを開始します。
ナビゲーション、検索、RSS、サイトマップ、ソーシャル画像、`llms.txt` は、同じ公開対象を使います。
未公開の項目はルートや派生成果物を所有しません。

```csharp
var registration = new SiteContentCollection<ArticleFrontMatter, string>(
    result.Collection!,
    (entry, context) => context.RenderDocument(
        $"<h1>{Html.Encode(entry.FrontMatter.Title)}</h1>" +
        context.RenderMarkdown(entry.Body)));

var generation = await new SiteGenerator().GenerateWithOptionsAsync(
    site,
    posts: [],
    "_site",
    clean: true,
    customization,
    new SiteGenerationOptions
    {
        BuildTimestamp = DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
        ContentCollections = [registration]
    },
    cancellationToken);
```

`ContentPageRenderingContext` は、正規化したルート、対応付けたメタデータ、任意のレイアウトの識別情報、
解決したビルド時刻と環境を公開します。
`RenderMarkdown` と `RenderDocument` は、ジェネレーターの既存の Markdown と文書レイアウトの経路を再利用します。
Markdown のリンク先は、セキュリティの許可リストではありません。
出力の未対応の URL スキームを診断するには、[サイト品質検査](site-quality.ja.md)を有効にしてください。
JSON、CSV、YAML のコレクションも、同じ登録 API を使います。

キャッシュ可能なコレクションでは、固定した `ContentTransformationId` を設定し、
取り込むファイルや値の依存関係をすべて宣言します。
ページのビルドノードは、コレクションと項目の識別情報、ソースのフィンガープリント、ルート、メタデータ、
レイアウト、描画済みの内容、関連する設定、解決した依存関係のフィンガープリントを含みます。
宣言したファイルの欠落は、出力を変える前に明示的に失敗します。
ソーシャル画像のパスは、正規化した出力ルートの SHA-256 を使うので、
ルートを変えずにソースファイルを変更しても、アセット名は変わりません。

## Markdown プロファイルと静的コンポーネント

本文の解釈は `SiteGenerationOptions.Markdown` で選びます。
フロントマターは引き続きネイティブ YAML を使い、本文の解析前に分離します。

```csharp
var markdown = new MarkdownOptions
{
    ProfileId = MarkdownOptions.DocumentationProfile,
    Components = new MarkdownComponentRegistry([
        MarkdownComponentRegistration.Create("version-badge",
            new VersionBadgeComponent(), GeneratedVersionBadges.Binder,
            "version-badge:v1")
    ])
};
```

属性の完全なモデル、生成バインダーの宣言、HTML をエンコードするコンポーネントは、
[Docs サンプル](../samples/LithoSharp.DocsSample/MarkdownComponents.cs)と
[モデル](../samples/LithoSharp.DocsSample/SampleModels.cs)にあります。
サンプルは `SiteGenerationOptions.Markdown` に登録し、Node.js なしで動きます。

| プロファイル | 本文の動作 |
|---|---|
| `lithosharp-markdown/1` | 既存の拡張構文と見出しの文字列のポリシーです。脚注はリテラルのままで、診断を出します。これが既定値です。 |
| `commonmark/0.31.2` | 文書用の拡張がない CommonMark の解釈です。サイトの描画処理は HTML をエンコードします。 |
| `syntamark-docs/2` | 文書用の拡張、繰り返し参照の戻るリンクを持つ解決済み脚注、明示的な静的コンポーネントです。 |

```markdown
:::component name="version-badge" version="3.0" stability="Stable"
:::
```

コンポーネントはリテラルの属性値と空の本文を受け付けます。
`name` 属性が登録を選びます。属性モデルに `name` というメンバーがある場合は、別の `ContentName` を使います。
波括弧は通常の文字列データです。
必須、未知の属性、列挙型、スカラーの属性は登録したバインダーを使い、診断には属性の位置を保持します。
`:::feature` などの通常のコンテナーは既存の動作を維持します。
登録のない明示的なコンポーネントは `LSM020`、不正な呼び出しは `LSM021` です。
生成スキーマのプロバイダーにより、バインダーやコンポーネントを実行せず、エディターで静的に検査できます。
スキーマのない独自のバインダーは、ビルドが必要と報告します。診断は `LSM022` です。

コンポーネントが使う値、ファイル、登録済みアセットを、すべて依存関係として宣言します。
ファイルの依存関係には、明示した `dependencyRoot` が必要です。
取り込む設定が変わったら、`configurationFingerprint` を変えてください。
登録、実装の識別情報、スキーマ、宣言した入力、選択したプロファイルは、生成キャッシュのキーに参加します。

文書を参照する属性には、`semanticRules: new([DocumentSemanticRule.Reference("target")])` などの共通のルールを登録に渡します。
そのリテラルの文書 ID を、現在のコレクション、バージョン、言語で解決します。
`DocumentationSite` は、取得した現在の文書と参照対象の範囲を提供します。
コンポーネントは、`context.Document` と `context.DocumentSemantics` を読んで、解決したルートを描画できます。
欠落、未公開、対象範囲外の参照先は、文書メタデータのルールと同じ構造化診断を維持します。
文脈を利用できない場合、編集時は判定を保留し、描画時は `LSM024` で失敗します。

プロファイルを変えると、解釈や見出しのアンカーが変わる場合があります。
最後に公開した互換性マニフェストを保持し、生成したページを確認し、
新しい出力の公開前に [`check-output` の公開比較](site-quality.ja.md)を実行します。
選択したプロファイルはパーサーと出力のキャッシュも無効にします。

## 集約ページの生成

コレクションの公開項目からタグ、年、カテゴリ、独自の一覧を作るには、`GeneratePages<TPageContent>` を使います。
セレクターは一つ以上の文字列キー、ファクトリは通常の `SitePage<TPageContent>` を返します。
描画処理は項目ページと同じ `ContentPageRenderingContext` を使います。

```csharp
var tagPages = articles.GeneratePages(
    new ContentCollectionId("article-tags"),
    entry => entry.FrontMatter.Tags,
    group => new SitePage<TagIndex>(
        new PageId($"tag:{group.Key}"),
        SiteRoute.ForDirectoryIndex($"tags/{group.Key}"),
        new TagIndex(group.Key, group.Entries),
        new PageMetadata($"Articles tagged {group.Key}")),
    (page, context) => context.RenderDocument(
        RenderTagIndex(page.Content)),
    groupKeys: ["featured"],
    layoutId: new ContentLayoutId("tag-index:v1"),
    declaredDependencies:
    [
        ContentDependency.FromValue("tag-index-locale", "en"),
    ],
    transformationId: new ContentTransformationId("tag-index:v1"),
    isCacheable: true,
    derivedSurfaces: GeneratedPageDerivedSurfaces.Default
        | GeneratedPageDerivedSurfaces.Navigation);
```

`tagPages` を `SiteGenerationOptions.ContentCollections` に追加します。
項目ページも出力する場合は、通常の `SiteContentCollection<TFrontMatter, TBody>` を別に登録します。

グループ化の前に、公開対象を絞ります。`groupKeys` で、意図的な空のグループを用意できます。
キーは Unicode NFC に正規化し、大文字・小文字を区別して比較します。
`groupOrderingComparer` がなければ ordinal 順にし、項目は元のコレクションの決定的な順序を維持します。
一つの項目で正規的に等価なキーは、一度だけ追加します。
不正なキーは `LSG001`、生成する `PageId` の重複は `LSG002`、ルートの衝突は既存の `SiteRouteTable` の診断です。

`GeneratedPageDerivedSurfaces` は、ジェネレーターが所有する集約ページの派生対象を制御します。
安全側の既定値は、検索、サイトマップ、ページごとのソーシャル画像、`llms.txt` を含み、従来の発見しやすさを維持します。
大量のタグや年がメニューを占める可能性があるグローバルナビゲーションと、
集約ページが 20 項目の予算を使う可能性がある RSS は除外します。
フラグを組み合わせて有効にし、すべての対応対象には `All`、すべて無効にするには `None` を使います。
通常の型付き項目ページ、従来の Markdown、追加ページの動作は維持します。

生成ページのビルドノードは、選択した派生対象に加え、正確な元項目の集合とフィンガープリント、
元と生成の変換の識別情報、統合した宣言済み依存関係、ルート、メタデータ、レイアウト、描画した出力を含みます。
各派生成果物は、その対象に選択した集約ページだけをフィンガープリントに含めます。
`GeneratePages` の戻り値は `SiteContentCollection` なので、生成ページを再帰的に元のコレクションにはできません。

## ファイルの検出と識別情報

- 入力ディレクトリから `.md` を再帰的に探し、拡張子は大文字・小文字を区別しません。名前が `_` で始まるファイルとディレクトリは、公開とフロントマターのバインドから除外します。Markdown は、それらの部分ファイルを文書モジュールとして取り込みません。
- 入力ルート相対のパスを `/` 区切りで NFC に正規化し、ordinal 順で処理します。
- `.md` を含む相対パスが `ContentEntryId` です。
- NFC に正規化すると同じになるパスは、ファイルシステムの列挙順で選ばず、診断します。
- `ContentEntry.Body` は、フロントマターの終端後のデコードした文字列で、宣言したコードの取り込みを解決したものです。取り込みがなければ正確な末尾部分です。型付きローダーはトリミングや描画を行いません。
- `ContentEntry.SourceFingerprint` は、UTF-8 BOM と改行も含め、正確なファイルのバイト列の小文字の SHA-256 に `sha256:` を付けたものです。
- Markdown ソースには、有効な UTF-8 が必要です。
- リンク、リパースポイント、安全でないパス、正規化後に衝突するパスは拒否します。
- 検出前、列挙・処理中、ファイル I/O の前後にキャンセルを確認します。

## フロントマターの区切り

最初の論理行は正確に `---` である必要があります。
後続の論理行の、正確な `---` がフロントマターを閉じます。
空のフロントマター、開始・終了マーカーの欠落、マッピングでない YAML のルートは、コンテンツのエラーです。
先頭の UTF-8 BOM に対応し、元の診断位置を維持します。
共通の Syntamark のヘッダー走査には、UTF-16 コード単位で 1 Mi のヘッダー予算があります。
確認できた終端マーカー後の本文は、この予算を使いません。
境界が利用できない `Unknown` は `LSM013` であり、ヘッダーの欠落や未終端を証明しません。
コンパイラーは利用できない境界を `LSG002` として報告します。

## コードの取り込みと本文解析

フェンス付きブロックはコンテンツの入力ルート内のコードファイルを読めます。

````markdown
```csharp source="_examples/Hello.cs" region="Greeting"
```
````

`source` は Markdown ファイルからの相対パスです。
任意の `region` は、名前付きの `#region` を選びます。
取り込みは、フェンスの言語やほかのメタデータではなく、本文を置き換えます。
参照先の欠落や読み取り不能は `LSM011`、領域の欠落や未終端は `LSM012` です。
不完全な Markdown 解析には、取り込みの失敗とは独立して `LSM013` を使います。
参照先は入力ルートから出ることも、リンク・リパースポイントの検査を回避することもできません。

ソースのフィンガープリントは引き続き元の Markdown のバイト列をハッシュします。
参照先ファイルと解決した取り込み内容は、別のビルド依存関係になるので、コードだけの編集でもページを無効にします。
元ソースの警告は元の位置を維持します。
変換した本文の診断は、その変換後の文字列を参照し、挿入後のソース位置を推測しません。

リーダーは、UTF-16 コード単位で最大 8 Mi の予算で、元の本文の警告を調べます。
最終的に解析する投稿・コレクションの本文には、取り込んだコードも含め、より厳しい 1 Mi の上限があります。
上限の失敗は `LSM013` の Error になり、公開を停止します。
描画だけの Markdown スニペットは別の経路なので、その描画の成功は文書全体の解析に成功する根拠になりません。
[既知の制限](known-limitations.ja.md)と[増分ビルド](incremental-builds.ja.md)を参照してください。

## YAML の規則

型付き Markdown の読み込みは、次の決定的な規則を使います。

- マッピングのキーは大文字・小文字を区別する文字列です。
- 重複するキーは、二つ目のキーで拒否します。
- YAML のアンカーとエイリアスは常に拒否します。共有する変更可能なグラフや、入力を増幅するエイリアスの展開を避けます。
- 既定のリフレクションバインダーは未知のフィールドを拒否します。
- YAML の `null`、`~`、空の通常のスカラーは null になります。`"null"` など、引用した値は文字列のままです。
- null は null を許容しない値型・参照型のメンバーに代入できません。
- メンバーが欠落した場合は、公開された引数なしコンストラクターやメンバーの初期化子の値を維持します。`required` メンバーは入力に必要です。
- スカラーの変換は、カルチャに依存しません。真偽値は .NET の `true`・`false`、列挙型の名前は大文字・小文字を区別します。数値、`Guid`、`DateTime`、`DateTimeOffset`、`DateOnly`、`TimeOnly`、`TimeSpan`、`Uri` に対応します。
- シーケンスは配列と互換性のある `List<T>` の抽象型にバインドします。文字列キーのマッピングは、互換性のある `Dictionary<string, T>` の抽象型か、入れ子のフロントマターオブジェクトにバインドします。
- プロパティ名とフィールド名は既定で snake_case です。`[ContentName("custom_name")]` は生成名を上書きし、`[ContentIgnore]` はメンバーを除外します。両属性は `LithoSharp.Content` にあります。

YAML の構文は Syntamark の `native-yaml/1` プロファイルによります。
型付きの実行時入力とコンパイラー入力は、入れ子の深さの上限 64 を明示的に共有します。
ほかの上限は、ネイティブの既定値である UTF-8 で 4 MiB、UTF-16 コード単位で 1 Mi、ノード 131,072、
スカラーあたり UTF-16 コード単位で 1 Mi、走査単位 16,777,216 です。
未対応のアンカー、エイリアス、明示的タグ、ディレクティブ、複雑なキー、マージ展開は診断を出します。
文字列キーとしてのリテラルの `<<` は許可します。
部分的な解析や上限に達した解析は、必須メンバー・未知フィールドの検証前に拒否します。
ネイティブのスカラー分類は、引用した文字列を保持し、日付を推論しません。
型付きバインダーは、引用した真偽値や数値の文字列も含め、デコードした文字列を宣言した CLR 型に明示的に変換します。
数値形式、オーバーフロー、非有限値は、既存のカルチャに依存しない .NET の対象型の変換規則に従います。

`ReflectionContentFrontMatterBinder<TFrontMatter>` は、参照型に公開された引数なしコンストラクターを要求します。
`LithoSharp.Generators` は、生成する `Binder` プロパティで `IContentFrontMatterBinder<TFrontMatter>` を提供します。
Docs サンプルと同様に、`frontMatterBinder: GeneratedArticles.Binder` として渡します。
静的検査は、明示的に登録したコンパイラー入力を対象にし、実行時の読み込みも、実際に検出する内容を検査します。
意図的に拡張を許すスキーマだけで、`rejectUnknownFields` を `false` にしてください。

### リーダー、ライター、属性の移行

`[YamlMember(Alias = "custom_name")]` を `[ContentName("custom_name")]`、`[YamlIgnore]` を `[ContentIgnore]` に置き換えます。
YamlDotNet の属性とシリアライザーの設定は、使わなくなりました。

`MarkdownFrontMatterYaml.Deserialize<T>` は、`ReflectionContentFrontMatterBinder<T>` が対応する DTO のマッピングを受け付けます。
公開された引数なしコンストラクター、snake_case の名前、required・null 許容の検査、未知フィールドの拒否を適用します。
camelCase の YAML キーは snake_case に置き換えるか、`ContentName` を宣言してください。
スカラー、シーケンス、辞書、型を指定しない `object` のルートは、この汎用リーダーの対象ではありません。
型を指定しない文書には、Syntamark の `YamlParser` とネイティブノードを使います。
不正・未対応の YAML とバインド失敗は `FormatException`、未対応の対象型は `NotSupportedException` を投げます。
投稿専用の `Deserialize(string)` リーダーは未知フィールドを無視する従来の動作を維持します。

`Serialize(PostFrontMatter)` は、固定した順序、LF 改行で新しい YAML を作り、文字列、日付、数値に見える文字列を引用します。
任意の公開・サイドバーの値が null の場合は省略します。
このライターは、既存文書のコメント、表記、バイト列を保持しません。
未編集の単独 YAML には、Syntamark の `YamlParser.ParseUtf8` と `YamlDocument.GetOriginalUtf8` を使ってください。
Markdown のヘッダーと本文を一緒に保持する場合は、元の Markdown のバイト列を別に保持します。
一般的な書式保持の編集 API はありません。

## 診断と失敗時の動作

想定するソースのエラーは、`ContentLoadResult.Diagnostics` で返し、例外を投げません。
すべての YAML とバインドの診断には、正規化した相対ソースパスと、分かる場合は 1 始まりの行と列を含めます。
検出、安全でないパス、読み込み失敗、不正な UTF-8 も、コンテンツの診断になります。
不正な API 設定とキャンセルは引き続き例外を投げ、利用者のコールバックも例外を投げる場合があります。
一部の項目が個別に有効でも、読み込みに失敗した場合は、利用できるコレクションはありません。

エディターは、任意の C# のコンストラクター、ゲッター、セッター、検証処理を実行しません。
明示した Build または Inspect が、バージョン付きの生成スキーマのメタデータを取得した後、
対応する独自フィールド、型、必須、列挙型、リテラルの条件付き検査を、現在のネイティブ YAML のスナップショット上で静的に行います。
初期化子に依存する要件と独自コードには、引き続きビルドが必要です。
`FrontMatterSchemas.For<T>` は、T の構築やプロパティ値の読み取りをせず、メタデータを読みます。
`Required` は明示した静的要件、`RequiresBuildValidation` はメタデータでは確定できない制約を示します。
[エディターのスキーマスナップショット](language-server.ja.md)を参照してください。
`NoContext` は、プロジェクトに結び付くスキーマ・ルートの根拠が利用できない状態、`Partial` は解析が不完全な状態です。
どちらも、コンテンツが有効である証明ではありません。
型付き参照の矛盾の検査にも、完全で現在の状態に対応した `Closed` カタログが必要であり、動的ルートは未証明のままです。

`MarkdownPostReader` は、互換性のため、型付きローダーのコンテンツ診断を `InvalidOperationException` に変換します。
既存の出力パス、本文のトリミング、必須のタイトル・日付の検査、新しいものからの順序を維持します。

## 例

例では、`content/articles` 内の Markdown 入力と、呼び出し側が渡す `cancellationToken` を想定します。
このスキーマでは、文書のフロントマターに少なくとも `title: Example` が必要で、`published_on` と `tags` は任意です。

```csharp
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

var loader = new MarkdownContentCollectionLoader<ArticleFrontMatter>(
    new ContentCollectionId("articles"),
    "content/articles",
    entry => SiteRoute.ForDirectoryIndex(entry.Id.Value[..^3]),
    entry => new PageMetadata(entry.FrontMatter.Title));

var result = await loader.LoadAsync(cancellationToken);
if (!result.IsSuccess)
{
    foreach (var diagnostic in result.Diagnostics)
    {
        Console.Error.WriteLine(
            $"{diagnostic.Location?.FilePath}:{diagnostic.Location?.Line}:{diagnostic.Location?.Column}: " +
            $"{diagnostic.Id}: {diagnostic.Message}");
    }
}

public sealed class ArticleFrontMatter
{
    public required string Title { get; init; }
    public DateOnly PublishedOn { get; init; }
    public List<string> Tags { get; init; } = [];
}
```
