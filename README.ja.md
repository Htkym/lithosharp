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
var result = await new SiteGenerator().GenerateAsync(site, posts, "_site", clean: true, customization);

Console.WriteLine($"Generated {result.PostCount} post(s) into {result.OutputDirectory}.");
```

実行可能な Docs サンプルは [`samples/LithoSharp.DocsSample`](samples/LithoSharp.DocsSample) にあります。従来の Blog レイアウトは [`samples/LithoSharp.Sample`](samples/LithoSharp.Sample) で確認できます。

```powershell
dotnet run --project samples/LithoSharp.DocsSample -- --output _site
```

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

## 公開 API

- `new SiteGenerator().GenerateAsync(SiteSettings site, IReadOnlyList<MarkdownPost> posts, string outputDirectory, bool clean, SiteCustomization? customization = null, CancellationToken ct = default)`
- `static SiteGenerator.Validate(SiteSettings site, string contentDirectory, IReadOnlyList<MarkdownPost> posts, SiteCustomization? customization = null)`
- `new MarkdownPostReader().ReadAllAsync(string contentDirectory)`
- `SiteCustomization`, `SiteThemeOptions`, `SiteText`, `SiteExtraPage`
- `ISiteTemplate`, `SiteTemplateContext`, `SiteTemplateResult`, `SiteTemplateFile`, `SiteTemplatePage`, `SiteTemplatePageLink`, `SiteTemplateHeading`, `SiteTemplateNavigationNode`, `SiteTemplateDocument`
- `DocsSiteTemplate`, `BlogSiteTemplate`
- `IContentValidator`, `ContentValidationContext`, `RequiredSummaryValidator`

名前空間は `LithoSharp`、`LithoSharp.Configuration`、`LithoSharp.Content`、`LithoSharp.Validation`、`LithoSharp.Search` です。

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
