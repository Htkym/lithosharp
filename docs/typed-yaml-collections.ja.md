# 型付き YAML コレクション

`YamlContentCollectionLoader<TFrontMatter, TBody>` は、`.yaml` と `.yml` のファイルを再帰的に検出します。
拡張子は大文字・小文字を区別せず、NFC に正規化した相対パスの ordinal 順で処理します。
各ファイルは、一つのマッピング、またはマッピングのシーケンスを含みます。

## コレクションの定義と読み込み

バインダーは `id` と `body` も含め、マッピング全体を受け取ります。
本文のファクトリが `body` を読む場合も、厳密なスキーマではこれらのフィールドを宣言します。

```yaml
id: welcome
title: Welcome
body: |
  # Welcome
  This article is loaded from YAML.
```

```csharp
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

var loader = new YamlContentCollectionLoader<ArticleFrontMatter, string>(
    "content/articles",
    new ContentCollectionId("articles"),
    new ReflectionContentFrontMatterBinder<ArticleFrontMatter>(),
    values => (string)values["body"]!,
    entry => SiteRoute.ForFile($"articles/{entry.Id.Value}.html"),
    entry => new PageMetadata(entry.FrontMatter.Title));
var result = await loader.LoadAsync(cancellationToken);
if (!result.IsSuccess)
    throw new InvalidOperationException(string.Join(Environment.NewLine,
        result.Diagnostics.Select(diagnostic => $"{diagnostic.Id}: {diagnostic.Message}")));

public sealed class ArticleFrontMatter
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
}
```

例では、呼び出し側が渡す `cancellationToken` と、`content/articles` 内の YAML ファイルを想定します。
ページを出力するには、`SiteContentCollection<ArticleFrontMatter, string>` と
`SiteGenerationOptions.ContentCollections` を通して `result.Collection!` を登録します。
共通の描画 API は[型付き Markdown のサイト生成例](typed-markdown-collections.ja.md)を参照してください。
このローダーは `Body` を自動的に Markdown として描画・解析しません。本文の使い方は、選んだ描画処理が決めます。

## 識別情報、検証、失敗

空でない `id` の文字列は、NFC に正規化した後、固定した項目 ID として使います。
指定がない場合、単一の項目では正規化した相対ファイル名を使います。
複数の項目では、そのファイル名に `#1`、`#2` などを付けます。
項目の並べ替えや移動があり得る場合は、ID を明示してください。正規化後の項目 ID の重複は `LSY003` です。

ローダーは Markdown のフロントマターと同じ厳密な YAML パーサーを使います。
重複キー、アンカー、エイリアス、null や複雑なキー、不正なルート、不正な UTF-8 を、分かる場合はソース位置とともに報告します。
未知のフィールドや、型・必須メンバーの失敗は、[バインダーの規則](typed-markdown-collections.ja.md)に従います。
単独の YAML は、Markdown の `---` によるフロントマターの囲みや、ヘッダーだけの走査を使いません。
MDX の入力でもありません。

ネイティブ構文のプロファイルと、明示した型付き入力の上限は、[型付き Markdown](typed-markdown-collections.ja.md)と共通です。
リフレクションと生成の両バインダーは、入れ子のソース位置を持つ、デコードしたスカラー値を受け取り、本文のファクトリも同じ値を受け取ります。
単独の YAML のルートは、マッピングか、マッピングのシーケンスである必要があります。
スカラーのルートや、マッピング以外のシーケンス項目は、コレクションローダーが拒否します。

ソースのフィンガープリントは、正確なファイルのバイト列の小文字の SHA-256 に `sha256:` を付けたものです。
同じ YAML シーケンスの項目はファイルのフィンガープリントを共有します。
リンク、リパースポイント、正規化後のパスの衝突は拒否します。
読み込み失敗は `LSY002`、不正な UTF-8 は `LSY001`、検出やパスの失敗は共通の `LSC` 診断です。
コレクションにアクセスする前に `IsSuccess` を確認してください。
コンテンツのエラーでは、正しい項目だけを公開せず、コレクションの読み込み全体を失敗にします。
不正な API 設定とキャンセルは、引き続き例外を投げます。

増分生成では、`isCacheable` を有効にする前に、固定した `transformationId` を指定し、
取得する依存関係を宣言し、描画処理のフィンガープリントを渡します。
YAML と Markdown のコレクションには、同じ[キャッシュの規則](incremental-builds.ja.md)を適用します。
