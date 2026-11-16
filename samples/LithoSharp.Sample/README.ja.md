# LithoSharp Blog サンプル

[English](README.md)

![生成したサンプルサイト](../../docs/images/blog-dark.png)

テーマ切り替えは既定で有効です。`SiteThemeOptions.EnableThemeSwitching = false` で無効にできます。
[固定ダーク配色を含む設定例](../../docs/layout-css-contract.ja.md#テーマ切り替え)を参照してください。

[LithoSharp](../../src/LithoSharp) でブログを生成する最小限のコンソールアプリです。
依存するのは LithoSharp ライブラリだけなので、日次更新のコードなしでジェネレーターが動作することの確認にも使えます。

このリポジトリのサンプルは LithoSharp 3.0.0 を対象にしています。選定した Syntamark 2 の
runtime と Source の組み合わせと、候補が未公開であることは[バージョン一覧](../../eng/version-matrix.json)に記録しています。

## 実行

.NET 10 SDK（10.0.300 以降の対応する feature band）を使い、リポジトリのルートで実行します。
Node.js は不要です。

```powershell
dotnet run --project samples/LithoSharp.Sample -- --output _site
```

- `--output <dir>`：出力先です。既定値は `./_site` です。
- `--content <dir>`：Markdown の入力先です。既定値はアプリと同梱する `content/` です。
- `--check`：公開前にステージング内の HTML、ローカルリンク、アンカーを検査します。
- `--redirect-demo`：検証する `old-home.html` リダイレクトの例を追加します。

`_site/index.html` をブラウザーで開いて確認してください。このサンプルは `BlogSiteTemplate` を明示的に使い、
記事一覧・本文・アーカイブ・タグ・検索を備えています。見出しと余白を重視したレイアウトです。
ライト・ダークの配色は、ヘッダーから読者が選んだ設定を保存するまでは OS 設定に従います。

## サンプルで示すこと

- `MarkdownPostReader` による Markdown 記事の読み込み。
- `SiteGenerator.Validate` による既定の検証処理。
- `SiteGenerator.GenerateWithOptionsAsync` と独自の `SiteCustomization` による生成。`llms.txt` を明示的に有効にしています。
- `BlogSiteTemplate` の明示的な選択。

標準 Docs のレイアウトや、独自の C# スキーマ、生成した参照、エディター、出力検査をつなぐ手順は
[Docs サンプル](../LithoSharp.DocsSample/README.ja.md)を参照してください。

favicon とソーシャル画像のアセットは任意です。favicon のディレクトリを指定しなくても、それらのバイナリーを省いて HTML サイトを生成できます。

このサンプルは `BlogSampleFactory : ISiteFactory` を公開しています。
`lithosharp build samples/LithoSharp.Sample -o artifacts/blog-cli` と通常の `dotnet run` は同じサイト定義を使います。
`--content`、`--output`、`--check`、`--redirect-demo` は引き続きサンプルのプログラムで利用できます。

既存の出力を検査するには、リポジトリのルートで実行します。

```powershell
dotnet run --project src/LithoSharp.Tool -- check-output --output _site --base-url https://example.com/ --format json
```

このコマンドは現在のファイルを読み取り、サイトの再ビルドやソースが最新であることの確認はしません。
ファクトリのベース URL を変更した場合は、その値を使ってください。
ネイティブの Markdown 経路では、選定した Syntamark 2 の組み合わせを使い、`lithosharp-markdown/1` を維持します。
[解析上の制約](../../docs/known-limitations.ja.md#markdown-の互換性)を参照してください。

## ファクトリのテスト

`SiteTestHost` と `SiteTestDocument` を使い、テストプロセス内でファクトリを検査できます。

```csharp
using LithoSharp;
using LithoSharp.Testing;

await using var host = await SiteTestHost.CreateAsync(
    new BlogSampleFactory(),
    new SiteFactoryContext(Path.GetFullPath("samples/LithoSharp.Sample")));
host.AssertSucceeded();
host.AssertRoute("/index.html", "index.html");
using var page = await host.OpenPageAsync("/index.html");
page.AssertElement("main");
```

テストプロジェクトからこのサンプルと `LithoSharp.Testing` を参照し、コード例をリポジトリのルートで実行します。
出力先とキャッシュは隔離され、外部リンクの検証は既定で無効です。
成果物と診断の検証、失敗時の扱いは[テストガイド](../../docs/testing.ja.md)を参照してください。
