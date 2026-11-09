# 配布とインストール

[English](distribution.md)

現在のソースは、未公開候補のLithoSharp 2.2.0とVS Code拡張0.4.0を対象にしています。以下の安定版の導入例には2.1.3を残しています。拡張は別の公開系列を使い、Marketplaceでの識別子は`htkym.lithosharp`です。NuGetパッケージの公開では拡張は公開されません。

## 候補版の導入と互換性

レビュー済み候補とともに渡された正確なバージョンと専用feedを使ってください。生成したプロジェクトの`LithoSharpVersion`をその版に合わせ、同じ版のツールとテンプレートを使います。普段使うfeedやテンプレートhiveは変更せず、候補専用の設定を使ってください。ソースのバージョンやローカルパッケージだけでは、NuGetやMarketplaceに公開済みとは判断できません。

| 利用箇所 | 従来の安定版 | 2.2.0／0.4.0候補 |
| --- | --- | --- |
| 既存のCore呼び出し | 対応を維持 | 型付きDocsのAPIを追加。Qualityの既定値は変更しない |
| Docsテンプレート | 安定版パッケージの動作 | 生成Binder／Catalog／参照を使い、QualityのErrorで明示的に失敗する |
| CLI JSON | schema major 1 | 同じmajorを維持。拡張は必須項目と終了コードの整合を検査する |
| 編集プロトコル | 従来の項目 | プロジェクト情報を任意項目として追加。根拠がない状態は未確認とする |
| 独自C# frontmatter | コンパイラーとbuildで検査 | プロジェクトのbuildが必要。編集時にすべての項目を検証するわけではない |

候補のコンパイラーパッケージと異なる版のCoreや生成テンプレートを混在させないでください。Docsへ接続した後は出力を生成し直して確認し、公開前にサイト全体をbuildして出力を検査します。

## NuGetパッケージ

プロジェクトで使うパッケージのバージョンを2.1.3に揃えてください。対象は`LithoSharp`、`LithoSharp.Generators`、`LithoSharp.Images`、`LithoSharp.Tool`、`LithoSharp.Testing`、`LithoSharp.Mdx`、`LithoSharp.ProjectTemplates`です。任意で導入する`LithoSharp.Analyzers`と`LithoSharp.CodeFixes`も2.1.3を使います。ツールとテンプレートは別のパッケージです。

```sh
dotnet new install LithoSharp.ProjectTemplates::2.1.3
dotnet tool install LithoSharp.Tool --version 2.1.3 --tool-path .tools
```

サイトの作成と配信は[Quick Start](quickstart.ja.md)を、既存プロジェクトの更新は[更新手順](migration-2.0.ja.md)を参照してください。

候補feedを使う場合は、提供された`<candidate-version>`を正確に指定し、生成projectの`LithoSharpVersion`もrestore前に同じ値へ設定します。正式版と候補版のLithoSharpパッケージを混在させないでください。CoreはSyntamark 1.2.0 runtimeを参照し、GeneratorsとAnalyzersは対応するSourceを組み込みます。利用者がSyntamark.Sourceを直接参照する必要はありません。候補feedでもSyntamark自身の1.2.0というバージョンを使い、LithoSharpの候補suffixへ付け替えません。既定のprofileは`lithosharp-markdown/1`です。

公開する9パッケージは[配布対象の一覧](../eng/release-packages.json)で管理します。

## VS Code拡張機能

```sh
code --install-extension htkym.lithosharp
```

同じ拡張機能をWindows x64、Linux x64、macOS arm64で利用できます。拡張機能本体、各環境のネイティブ資産を含む言語サーバー、MDXワーカーのソースとlockfileを同梱しています。同梱サーバーには.NET 10ランタイムが必要です。サイトのビルドと配信には10.0.300以降の.NET 10 SDKも必要です。

`LithoSharp: Select Project`、`LithoSharp: Build`、`LithoSharp: Open Preview`の順に実行してください。設定と問題への対処は[拡張機能のガイド](../extensions/lithosharp-vscode/README.ja.md)を参照してください。取得したVSIXは`code --install-extension <path-to-vsix>`でもインストールできます。

## MDXワーカーの復元

Markdownだけのサイトや診断にはNode.jsは不要です。MDXにはNode.js 24.13.0と、ワーカー依存関係の明示的な復元が必要です。

編集時の診断には`LithoSharp: Restore MDX Worker`を実行してください。拡張機能は同梱ソースをハッシュ名の専用ストレージにコピーし、そこで`npm ci --ignore-scripts --no-audit --no-fund`を実行してlockfileに従って依存関係を復元します。復元済みの`node_modules`は拡張機能に含めません。`lithosharp.workerDirectory`で保存先を変更した場合は、`LithoSharp: Restart Language Server`の実行が必要です。

サイトのビルドにはCLIの`restore-mdx`を使います。詳しくは[MDXガイド](mdx.ja.md)を参照してください。ビルド、配信、キー入力でnpmの依存関係を自動復元することはありません。コード実行や依存関係のインストールを伴うコマンドには、ワークスペースの信頼が必要です。

## 言語サーバーを個別に使う場合

同梱サーバーは`dotnet LithoSharp.LanguageServer.dll`で起動します。`deps.json`、`runtimeconfig.json`、マネージド依存関係、ネイティブ資産、ワーカーのソースを一緒に配置してください。サーバーは独立したNuGetパッケージではありません。trimming、単一ファイル配布、Native AOTには対応していません。独自クライアントについては[言語サーバーのプロトコル](language-server.md)を、環境やコード実行の境界については[既知の制約](known-limitations.ja.md)を参照してください。
