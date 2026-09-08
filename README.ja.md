# LithoSharp

[![build](https://github.com/Htkym/lithosharp/actions/workflows/build.yml/badge.svg)](https://github.com/Htkym/lithosharp/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/LithoSharp.svg)](https://www.nuget.org/packages/LithoSharp)

[English](README.md) | [日本語](README.ja.md)

.NET 向けの小さな静的サイトジェネレーターです。Markdown とサイト設定を渡すと、Docusaurus に着想を得たドキュメントサイトを既定で生成します。Docs テンプレートはレスポンシブな階層サイドバー、ページ目次、前後ページへのリンクを生成します。従来の Blog テンプレートも明示的に選択できます。

LithoSharp はコンソールアプリケーションやビルドパイプラインへ組み込むことを想定しています。サイト固有の文言、テーマ、検証、テンプレートは `SiteCustomization` で指定するため、コアライブラリはブランドや運用方針を固定しません。

## 機能

- Docusaurus に着想を得た Docs 出力を既定で提供。レスポンシブな階層サイドバー、H2/H3 の目次、前後のドキュメントリンクを生成
- 一覧、アーカイブ、タグ、クライアント側検索、RSS (`feed.xml`)、`sitemap.xml` を備えた Blog テンプレートを明示的に選択可能
- canonical、Open Graph、Twitter Card メタデータ、任意の favicon とソーシャル画像
- front matter の検証。タイトルと日時は必須で、既定では要約も必須
- 言語モデル向けの `llms.txt` を任意で生成
- 型付き Markdown、JSON、CSV コレクションからルート付きページを生成し、公開判定と
  派生成果物へ反映
- 静的Markdown向けのbinder、schema、ルート、ID、型付き参照を生成する任意の
  Source Generatorパッケージ
- favicon やソーシャル画像の元ファイルがない場合も、該当する出力だけを省略して完全な HTML サイトを生成

## インストール

```powershell
dotnet add package LithoSharp
```

LithoSharp は `net10.0` を対象とし、Markdig、YamlDotNet、SkiaSharp に依存しています。

## クイックスタート

Markdown を読み込み、必要に応じて検証し、サイトを生成します。

```csharp
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

var site = new SiteSettings
{
    Title = "My Site",
    Description = "A static site generated with LithoSharp.",
    BaseUrl = "https://example.com/",
    Language = "en",
    Author = "Me",
    TimeZone = "UTC"
};

var customization = new SiteCustomization
{
    Theme = new SiteThemeOptions
    {
        BrandPrefix = "my site / ",
        DefaultSocialSubtitle = "Built with LithoSharp",
        AdditionalCss = ":root { --accent: #7c9eff; }"
    },
    GenerateLlmsTxt = true
};

var posts = await new MarkdownPostReader().ReadAllAsync("content");
SiteGenerator.Validate(site, "content", posts, customization);
var options = new SiteGenerationOptions
{
    EnvironmentName = "Production"
};
var result = await new SiteGenerator().GenerateAsync(
    site, posts, "_site", clean: true, customization, options, CancellationToken.None);

Console.WriteLine($"Generated {result.PostCount} post(s) into {result.OutputDirectory}.");
```

公開時刻と環境を明示する場合は、`GenerateWithOptionsAsync` を使用します。

```csharp
var result = await new SiteGenerator().GenerateWithOptionsAsync(
    site,
    posts,
    "_site",
    clean: true,
    customization: null,
    new SiteGenerationOptions
    {
        BuildTimestamp = DateTimeOffset.Parse("2026-01-02T12:00:00Z"),
        EnvironmentName = "Production"
    },
    CancellationToken.None);
```

実行可能な Docs サンプルは [`samples/LithoSharp.DocsSample`](samples/LithoSharp.DocsSample) にあります。従来の Blog レイアウトは [`samples/LithoSharp.Sample`](samples/LithoSharp.Sample) で確認できます。

```powershell
dotnet run --project samples/LithoSharp.DocsSample -- --output _site
```

`--check` を付けると、出力確定前のサイト品質検査を実行してテキスト形式のレポートを
表示します。`--redirect-demo` を付けると、サイトのルートへ移動する `old-home.html` を
生成します。どちらも任意であり、既定のサンプル出力は変わりません。失敗しきい値、診断 ID、
出力形式、外部リンク検査、リダイレクトの規則は
[サイト品質検査](docs/site-quality.md#日本語)を参照してください。

既定の `DocsSiteTemplate` は `content` 配下のディレクトリ構造から左側ナビゲーションを作ります。`intro.md` はトップレベルの文書になり、`guides/install.md` は **guides** グループの下に表示されます。

## コンテンツと front matter

`MarkdownPostReader` は `*.md` を再帰的に読み込み、日時の降順、次にスラッグ順で並べます。各ファイルは YAML front matter から始めます。

```markdown
---
title: "Welcome"
date: "2026-01-02T09:00:00Z"
summary: "A short description used in listings and metadata."
sidebar_position: 1
sidebar_label: "Start here"
draft: false
publish_from: "2026-01-01T00:00:00Z"
publish_until: "2027-01-01T00:00:00Z"
environments:
  - Production
tags:
  - intro
sources:
  - type: feed
    name: Example Blog
    url: https://example.com/feed.xml
---

Body written in Markdown.
```

### スキーマ

| フィールド | 型 | 必須 | 説明 |
| --- | --- | --- | --- |
| `title` | string | はい | 記事タイトル。一覧、`<title>`、メタデータに使用します。 |
| `date` | string (ISO 8601) | はい | 公開日時。`DateTimeOffset` として解析します。 |
| `summary` | string | 既定では必須 | 一覧、フィード、`og:description` に使用する短い説明。既定の `RequiredSummaryValidator` が必須にします。変更するには検証器を置き換えます。 |
| `sidebar_position` | integer | いいえ | Docs ナビゲーションの順序。小さい値ほど先に表示します。未指定の文書はラベル、次にパスで並べます。 |
| `sidebar_label` | string | いいえ | Docs ナビゲーションの表示名。未指定時は `title` を使用します。 |
| `draft` | boolean | いいえ | `true` の場合は、ページと派生成果物のすべてから投稿を除外します。既定値は `false` です。 |
| `publish_from` | string (ISO 8601) | いいえ | 公開開始時刻です。解決済みのビルド時刻がこの時刻と同じ場合は公開します。 |
| `publish_until` | string (ISO 8601) | いいえ | 公開終了時刻です。解決済みのビルド時刻がこの時刻と同じ場合は公開しません。 |
| `environments` | list of strings | いいえ | 公開を許可する生成環境です。大文字と小文字を区別せずに比較し、空の一覧はすべての環境を許可します。 |
| `tags` | list of strings | いいえ | 任意のタグ。タグページとクライアント側検索に使用します。 |
| `sources` | list of objects | いいえ | 記事の出典。詳細は次を参照してください。 |

`sources` の各要素は次のフィールドを持ちます。

| フィールド | 型 | 必須 | 説明 |
| --- | --- | --- | --- |
| `type` | string | いいえ | 呼び出し側で解釈する任意のラベル。`feed`、`article`、`repo`、`doc`、`release` などを利用できます。 |
| `name` | string | いいえ | 人が読める出典名。 |
| `url` | string | いいえ | 出典へのリンク。 |

`sources` は解析されて `MarkdownPost.FrontMatter` から取得できますが、コアジェネレーターは表示しません。必要に応じて `SiteExtraPage` または独自レイアウトで表示してください。`title` と `date` は読み込み時に検証され、`summary` は `SiteGenerator.Validate` で検証されます。

## カスタマイズ

`SiteCustomization` はライブラリの拡張点です。利用側のアプリケーションコードをコアライブラリが参照することはなく、次のメンバーを通じて設定を渡します。

- `Text`: UI 文言。`SiteText.English` と `SiteText.Japanese` を利用できます。検索ステータスには `{count}`、`{tag}`、`{query}`、`{shown}` のプレースホルダーを使えます。
- `Template`: 描画契約。既定は `DocsSiteTemplate` です。従来の Blog URL、記事、RSS、検索、サイトマップを使用するには `new BlogSiteTemplate()` を設定します。
- `Theme`: `BrandPrefix`、`ThemeColor`、`DefaultSocialSubtitle`、`AdditionalCss` を持つ `SiteThemeOptions`。`AdditionalCss` は既定スタイルシートの末尾に追加されます。
- `Validators`: 独自の `IContentValidator`。空の場合は既定の要約必須チェックだけを実行します。
- `ExtraPages`: 標準ページに追加して出力するページ。
- `FaviconSourceDirectory`: favicon 資産を置くディレクトリ。未指定時は実行ファイルの隣にある `favicon` ディレクトリを使用します。
- `GenerateLlmsTxt`: 言語モデル向けの `llms.txt` を生成するかどうか。既定ではオフです。

### Blog テンプレートの選択

```csharp
var customization = new SiteCustomization
{
    Template = new BlogSiteTemplate()
};
```

### 独自テンプレートの作成

`ISiteTemplate` を実装すると、独自の HTML とテキスト資産を生成できます。`SiteTemplateContext` は、変換済み本文、見出し、前後ページリンク、ディレクトリベースのナビゲーションツリーを提供します。標準のメタデータ、ヘッダー、フッターを使う場合は `RenderDocument` を、ページ見出しから目次を作る場合は `RenderTableOfContents` を使えます。

LithoSharp はテンプレートが返すパスをすべて検証し、出力先ディレクトリの配下へ安全に書き込みます。`llms.txt`、favicon、ソーシャル画像などの共通資産と衝突するパスは拒否します。

```csharp
public sealed class LandingTemplate : ISiteTemplate
{
    public Task<SiteTemplateResult> RenderAsync(
        SiteTemplateContext context,
        CancellationToken cancellationToken = default)
    {
        var page = context.Pages[0];
        var body = $"<h1>{Html.Encode(page.Post.FrontMatter.Title)}</h1>{page.ContentHtml}";
        return Task.FromResult(new SiteTemplateResult(
        [
            new SiteTemplateFile
            {
                RelativePath = "index.html",
                Content = context.RenderDocument(new SiteTemplateDocument
                {
                    Title = context.Site.Title,
                    RelativePath = "index.html",
                    BodyHtml = body
                })
            },
            new SiteTemplateFile
            {
                RelativePath = "assets/site.css",
                Content = "body { font-family: sans-serif; }"
            }
        ]));
    }
}

var customization = new SiteCustomization { Template = new LandingTemplate() };
```

### コレクションの集約ページ

`ContentCollection<TFrontMatter, TBody>.GeneratePages<TPageContent>` は、
公開判定後のエントリからタグ別、年別、カテゴリ別、任意分類の型付きページを生成します。

```csharp
var tagPages = articles.GeneratePages(
    new ContentCollectionId("article-tags"),
    entry => entry.FrontMatter.Tags,
    group => new SitePage<TagIndex>(
        new PageId($"tag:{group.Key}"),
        SiteRoute.ForDirectoryIndex($"tags/{group.Key}"),
        new TagIndex(group.Key, group.Entries),
        new PageMetadata($"{group.Key} の記事")),
    (page, context) => context.RenderDocument(RenderTagIndex(page.Content)),
    transformationId: new ContentTransformationId("tag-index:v1"),
    isCacheable: true);
```

戻り値の `SiteContentCollection` を `SiteGenerationOptions.ContentCollections`
へ登録します。キーは Unicode NFC へ正規化し、大文字と小文字を区別して決定的に
並べます。空グループ、依存宣言、診断、各出力への統合規則は
[型付き Markdown コレクション](docs/typed-markdown-collections.md)を参照してください。

Docs サンプルの `typed-content` には、Markdown コレクションの読み込みから集約ページの
生成までを一通り実行する例があります。同じ決定的な生成を 2 回実行し、1 回目の
`BuildPlan` を `PreviousBuildPlan` として渡し、`BuildReport` も表示します。
従来の Blog サンプルは `MarkdownPostReader` との互換性を確認できるよう残しています。

JSON と CSV も同じ登録契約を使用します。入力を増やしすぎず、小さなデータで
確認できます。YAML も同じ厳格な YAML ポリシーで読み込めます。YAML の例:

```csharp
var loader = new YamlContentCollectionLoader<ArticleFrontMatter, string>(
    "data/articles",
    new ContentCollectionId("yaml-articles"),
    new ReflectionContentFrontMatterBinder<ArticleFrontMatter>(),
    values => (string)values["body"]!,
    entry => SiteRoute.ForFile($"articles/{entry.Id.Value}.html"),
    entry => new PageMetadata(entry.FrontMatter.Title, entry.FrontMatter.Summary));
var loaded = await loader.LoadAsync(cancellationToken);
```

`.yaml` と `.yml` を再帰的に検出し、オブジェクトまたはオブジェクト配列を
読み込みます。重複キー、エイリアス、非文字列キー、無効な UTF-8 は診断として
報告されます。

## 公開 API

- `new SiteGenerator().GenerateAsync(SiteSettings site, IReadOnlyList<MarkdownPost> posts, string outputDirectory, bool clean, SiteCustomization? customization = null, CancellationToken ct = default)`
- `new SiteGenerator().GenerateWithOptionsAsync(SiteSettings site, IReadOnlyList<MarkdownPost> posts, string outputDirectory, bool clean, SiteCustomization? customization, SiteGenerationOptions options, CancellationToken ct)`
- `static SiteGenerator.Validate(SiteSettings site, string contentDirectory, IReadOnlyList<MarkdownPost> posts, SiteCustomization? customization = null)`
- `new MarkdownPostReader().ReadAllAsync(string contentDirectory)`
- `SiteCustomization`, `SiteGenerationOptions`, `SiteThemeOptions`, `SiteText`, `SiteExtraPage`
- `SiteContentCollection<TFrontMatter, TBody>`, `ContentPageRenderingContext`,
  `ContentPageGroup<TFrontMatter, TBody>`, `GeneratePages<TPageContent>`
- `ISiteTemplate`, `SiteTemplateContext`, `SiteTemplateResult`, `SiteTemplateFile`, `SiteTemplatePage`, `SiteTemplatePageLink`, `SiteTemplateHeading`, `SiteTemplateNavigationNode`, `SiteTemplateDocument`
- `DocsSiteTemplate`, `BlogSiteTemplate`
- `IContentValidator`, `ContentValidationContext`, `RequiredSummaryValidator`

名前空間は `LithoSharp`、`LithoSharp.Configuration`、`LithoSharp.Content`、`LithoSharp.Validation`、`LithoSharp.Search` です。

互換性の基準は、[生成サイトの互換性契約](docs/compatibility-contract.ja.md)、
[英語版](docs/compatibility-contract.md)、および
[API の互換性ポリシー](docs/compatibility-contract.ja.md#api-の互換性)に記載しています。

再現可能な出力が必要な場合は、`SiteGenerationOptions.BuildTimestamp` を設定します。
未設定の場合は、有効な Unix タイムスタンプ形式の `SOURCE_DATE_EPOCH`、現在の UTC
時刻の順に使用します。`SOURCE_DATE_EPOCH` が不正な場合は、現在時刻へ切り替えず、
エラーとして生成を停止します。

`SiteGenerationOptions.EnvironmentName` は、front matter の `environments`
に含まれる投稿を選びます。既定値は `Production` です。`environments`
を指定していない投稿は、どの環境でも公開対象になります。下書き、公開前、公開終了後、
環境不一致の投稿はテンプレートへ渡す前に除外するため、ナビゲーション、一覧、検索、
RSS、サイトマップ、`llms.txt`、投稿別ソーシャル画像にも含まれません。

### ルートと診断

`SiteRoute` は公開 URL と物理的な出力パスを一体で表します。既存の
`.html` 形式には `ForFile` を、末尾スラッシュと `index.html` の組み合わせ
には `ForDirectoryIndex` を使用します。

```csharp
var file = SiteRoute.ForFile("guides/install.html", site.BaseUrl);
var directory = SiteRoute.ForDirectoryIndex("guides", site.BaseUrl);

Console.WriteLine(file.PublicPath);             // /product/guides/install.html
Console.WriteLine(directory.RelativeOutputPath); // guides/index.html
```

ジェネレーターは、ページ、テンプレート、共通成果物のルートを出力前に
検証します。重複、大文字と小文字だけが異なるパス、予約済みパス、
安全でないパス、ファイルとディレクトリの祖先競合がある場合は
`SiteRouteValidationException` をスローします。`exception.Diagnostics` から
安定した診断 ID と定義元の位置を確認できます。詳細は
[生成サイトのルートと互換性契約](docs/compatibility-contract.ja.md)と
[英語版](docs/compatibility-contract.md)を参照してください。

`SiteGenerationResult.PostCount` は、その生成で公開条件を満たした Markdown 投稿数です。
`clean: false` では無関係なファイルを保持します。一方、直前に成功した生成で
ジェネレーター所有として記録され、現在の検証済みビルド計画にないファイルは削除します。
所有権マニフェストは出力とともに原子的に確定します。マニフェスト導入前に作られた
出力ツリーは所有権を安全に判定できないため、そのまま保持します。

### 出力の安全性と移植性

ジェネレーターはすべてのルートを検証し、描画を完了してから、同じユーザーの
協調プロセス間で原子的な出力トランザクションを実行します。これは特権ユーザーや
管理者、別ユーザーからの操作、原子的な rename を提供しないファイルシステムに
対する防御ではありません。所有権は制限された兄弟 sidecar に記録します。
Windows の owner/group/DACL と Unix の permission mode は可能な範囲で保持しますが、
Unix の ACL、拡張属性、所有者は移植可能には保持できません。sidecar 自体も原子的に
確定します。sidecar 導入前のツリーは推測で削除せず、失敗したクリーンアップは
次回実行の回復登録として残ることがあります。

ルートとグループの識別子は NFC 正規化後も大文字と小文字を区別します。出力パスは
すべての OS で `/` 区切りとし、予約名や大文字と小文字だけが異なる衝突は変更前に拒否します。
型付きローダーの入力エラーは安定した ID とソース位置を持つ診断として返し、構成や
ファイルシステムの失敗は例外として扱います。移行では従来の投稿と型付きコレクションを
並行して登録し、コレクション単位で切り替えてください。0.2 では API と依存関係が
一つのまとまったアセンブリを形成しているため、物理的な NuGet パッケージ分割は行いません。

## 資産と画像

`public`コピー、CSSの資産参照、整合性ハッシュ、画像変換の使用例は
[資産と画像](docs/assets-and-images.ja.md)を参照してください。

ページの永続キャッシュ、並列度、実際のヒット・ミスの確認方法は
[差分ビルド](docs/incremental-builds.ja.md)を参照してください。
`LithoSharp.Images`ではPNG／JPEG／WebPと、明示設定したエンコーダーによるAVIFを生成できます。
Docsサンプルの`--asset-demo --check`で確認できます。

## 静的コンテンツのSource Generator

`LithoSharp.Generators` は、明示的に宣言したMarkdownファイルをコンパイル時に処理します。
analyzerパッケージを追加し、トップレベルのstatic partialクラスへ
`StaticContentCollectionAttribute` を付けます。各 `AdditionalFiles` にはコレクション、ID、
ルートのメタデータを設定します。front matterの `Binder`、`SchemaJson`、ネストした
`Pages`、`Entries`、`Ids` が生成されます。

```xml
<PackageReference Include="LithoSharp.Generators" Version="0.2.0" PrivateAssets="all" />

<AdditionalFiles Include="typed-content\**\*.md">
  <LithoSharpCollection>articles</LithoSharpCollection>
  <LithoSharpId>%(Filename)%(Extension)</LithoSharpId>
  <LithoSharpRoute>articles/%(Filename)/</LithoSharpRoute>
</AdditionalFiles>
```

```csharp
var binder = GeneratedArticles.Binder;
var pageUrl = GeneratedArticles.Pages.Typed_Content_First.GetUrl(site.BaseUrl);
GeneratedArticles.WriteJsonSchema("artifacts/articles.schema.json");

[StaticContentCollection(
    typeof(ArticleFrontMatter),
    typeof(ContentEntry<ArticleFrontMatter, string>),
    "articles",
    EmitJsonSchema = true)]
public static partial class GeneratedArticles;
```

生成される `PageRef<TPage>` と `ContentRef<TEntry>` は不変です。
`WriteJsonSchema` は `EmitJsonSchema` がtrueの場合だけ生成され、`SchemaJson` は常に利用できます。
宣言、メタデータ、ID、ルート、YAML、値に問題がある場合は、`LSG001` から `LSG006` の
コンパイルエラーになります。入力を移動または削除すると生成メンバーも消えるため、古い参照は
通常のC#コンパイルエラーになります。動的ローダーの入力は従来どおり実行時に検証します。
設定と診断の詳細は [Source Generator](docs/source-generators.md)を参照してください。

## レイアウト、コンポーネント、安全なHTML、登録資産

型付きコレクションには、レイアウトクラスを直接渡せます。

```csharp
// 読み込んだコレクションへ、描画デリゲートの代わりにレイアウトを登録します。
var registration = new SiteContentCollection<ArticleFrontMatter, string>(articles, new ArticleLayout());

sealed class ArticleLayout : IPageLayout<ContentEntry<ArticleFrontMatter, string>>
{
    public IHtmlContent Render(
        SitePage<ContentEntry<ArticleFrontMatter, string>> page,
        PageRenderingContext context) =>
        context.RenderDocument(page, context.RenderMarkdown(page.Content.Body));
}
```

`PageRenderingContext.Create(site)` と `ComponentRenderingContext.Create(site)` を使うと、
出力ディレクトリを作らずにレイアウトやコンポーネントを描画できます。
コンポーネントは `ISiteComponent<TProps>` を実装して `IHtmlContent` を返し、
`context.Render(component, props)` で組み合わせます。組み込みコンポーネントには、
パンくず、目次、検索、フラットなナビゲーション、前後リンク、Blog／Docsのヘッダー、
フッター、headとSEOメタデータがあります。維持するCSSクラスとスクリプト用属性は
[レイアウトとCSSの契約](docs/layout-css-contract.md)に記載しています。

既存の文字列描画APIと `ISiteTemplate` も引き続き使えます。従来のテンプレートから
`SiteTemplateContext.RenderLayout(page, layout)` を呼ぶと、そのテンプレートのサイト設定、
環境名、登録資産を新しい描画コンテキストへ引き継げます。`Create(site)` は英語の文言、
既定テーマ、`Production` 環境、空の資産レジストリーを使います。登録資産や独自の文言、
テーマが必要な場合は、サイト生成時に渡されるコンテキストを使ってください。

組み込みレイアウトは `SitePage<PageLayoutContent>` を受け取ります。`BlogPageLayout` は
Blogの文書構造を描画します。`DocsPageLayout` は、任意の `Sidebar` と
`TableOfContents` も配置します。`OpenGraphType`、`SocialImageUrl`、
`IncludeBlogNavigation` で対応する文書設定を指定できます。

```csharp
var page = new SitePage<PageLayoutContent>(
    new PageId("preview"),
    SiteRoute.ForFile("preview.html", site.BaseUrl),
    new PageLayoutContent(new HtmlText("Preview")),
    new PageMetadata("Preview"));

var html = new BlogPageLayout()
    .Render(page, PageRenderingContext.Create(site))
    .ToHtmlString();
```

Docsのサイドバーには `DocsNavigationComponent` を使います。表示順に並べた
`SiteTemplateNavigationNode`、追加の `NavigationLink`、現在の `SiteUrl` から描画し、
結果を `PageLayoutContent.Sidebar` へ設定します。

レイアウトとコンポーネントの入口は、実装や必須入力が `null` の場合に拒否します。
`ComponentRenderingContext.Render`、`SiteTemplateContext.RenderLayout`、型付きコレクションの
アダプターは、実装が `null` を返すと `InvalidOperationException` をスローします。
組み込みレイアウトは、ページ、コンテキスト、コンテンツ、本文のいずれかが `null` の場合も
拒否します。`DocsNavigationComponent` は props、ルート、追加リンク一覧の `null` を拒否します。
`RenderMarkdown` はLithoSharpの設定済みMarkdownパイプラインが生成した信頼済みHTMLを返します。
それ以外のraw HTMLを信頼する場合は、呼び出し側で `Html.UnsafeRaw` を明示してください。

要素内のテキストには `HtmlText`、引用符付きHTML属性には `HtmlAttributeValue` を使います。
`SiteUrl.ForFile("guide.html", site.BaseUrl)` は内部パスを検証し、
`SiteUrl.FromAbsolute` はHTTP(S)のURLを受け付けます。属性へ挿入するURLは
`ToAttributeValue()` で変換します。`Html.UnsafeRaw` はHTMLを明示的に信頼するためのAPIであり、
サニタイズは行いません。これらの型はJavaScriptやCSSへの値の埋め込みを安全にはしません。

```csharp
var asset = new SiteAsset("guide", contentRoot, "guide.pdf", "downloads/guide.pdf");
var options = new SiteGenerationOptions { Assets = [asset] };
// テンプレートやコンテンツの描画処理内で使用します。
var link = $"<a href=\"{context.Assets.GetUrl(asset).ToAttributeValue()}\">{new HtmlText("Guide & reference")}</a>";
```

レジストリーは入力ファイルの内容を保持し、そのSHA-256を出力ファイル名へ含めます。
URLには `BaseUrl` のサブパスが反映されます。資産はルート競合の検証、ビルド依存関係、
原子的出力の所有権管理へ登録されます。登録と参照には同じ `SiteAsset` インスタンスを使い、
未登録参照は `LSA001` で失敗します。不正なパス、危険なURL、入力ルートを逸脱するファイルは拒否されます。
[Docsサンプル](samples/LithoSharp.DocsSample/Program.cs)にはMarkdown原稿をダウンロードできる利用例があります。
既存の文字列HTML APIも引き続き使えます。

## 資産とフォントに関する注意

favicon とソーシャル画像の元ファイルは任意です。Open Graph 画像は SkiaSharp とシステムフォントで描画します。`SocialImageGenerator` は Consolas などの優先フォントを探し、見つからない場合は既定の書体へフォールバックします。フォントがないホストでは描画が異なったり、CJK 文字が豆腐文字として表示されたりする場合があります。

## ビルドとテスト

.NET 10 SDK が必要です。

```powershell
dotnet restore LithoSharp.slnx --locked-mode
dotnet build LithoSharp.slnx --no-restore -c Release
dotnet test --solution LithoSharp.slnx --no-build -c Release
dotnet run --project samples/LithoSharp.DocsSample -- --output _site
dotnet run --project samples/LithoSharp.Sample -- --output _site
```

テストには Microsoft.Testing.Platform 上で動作する TUnit を使用しています。

## バージョニング

LithoSharp は SemVer に従います。バージョンが `0.x` の間は、公開 API が変更される場合があります。

## ライセンス

MIT ライセンスです。詳細は [LICENSE](LICENSE) を参照してください。サードパーティの通知は [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) にあります。

C# のサイトプロジェクトのビルドと開発サーバーについては、[CLI とサイトファクトリ](docs/cli.ja.md)を参照してください。
サイト、コンポーネント、レイアウトの検証には、[LithoSharp.Testing](docs/testing.ja.md)を利用できます。
[MDXとReactの利用ガイド](docs/mdx.ja.md)には、版と言語を持つDocs、Blog、Selective Hydrationをまとめています。
