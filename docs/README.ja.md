# LithoSharpのドキュメント

[English](README.md) | [日本語](README.ja.md)

このガイドはLithoSharp 2.2.0とVS Code拡張0.4.0を対象にしています。
利用するLithoSharpのパッケージは2.2.0にそろえてください。拡張機能は別のバージョンで公開します。
必要な環境と導入手順はQuick Start、用途に合うかの判断は[導入選択ガイド](adoption-guide.ja.md)を参照してください。

## 最初のサイトを作る

1. [Quick Start](quickstart.ja.md)でSDK、Tool、テンプレートを用意し、Docsサイトを作成します。コンテンツを編集して、ビルド、配信、生成物の検査へ進みます。
2. [型付きコンテンツの体験手順](../samples/LithoSharp.DocsSample/README.ja.md#cの型定義から生成物の検査まで)で、C#モデル、生成した参照、Markdown、コンパイル診断、エディター、HTMLの検査をつなぎます。不正な入力と確定できない値の例は通常のコンテンツから分離しています。
3. [Golden Path](golden-path.ja.md)で、用途に応じた導入、生成、編集、診断、修正、検査の手順を確認します。
4. 既存サイトは[更新手順](migration-2.0.ja.md)を参照してください。1.xからの更新ではTesting APIの`Document`から`Snapshot`への移行も必要です。

## コンテンツと表示を選ぶ

- [Docsテンプレート](docs-template.ja.md)はサイドバー、ナビゲーション、検索、metadata、テーマの設定を説明します。
- [型付きMarkdown collection](typed-markdown-collections.md)と[型付きYAML collection](typed-yaml-collections.md)はモデル、binder、ルート、厳密な入力規則の英語リファレンスです。
- [Source Generator](source-generators.md)は日英併記、[Analyzer](../src/LithoSharp.Analyzers/README.md)は英語です。宣言したカタログとコンパイル時の検証範囲を説明します。動的な値には実行時の検証が必要です。
- [MDX](mdx.ja.md)はReact、island、文書collection、バージョン、locale、API文書の作成を扱います。
- [資産と画像](assets-and-images.ja.md)と[HTML/CSSの契約](layout-css-contract.md)は資産、変換、responsive image、表示の変更を説明します。HTML/CSSの詳細には英語の説明を含みます。
- [Docusaurus移行](docusaurus-migration.ja.md)で対応入力を検査・変換できます。任意のpluginやsite codeを自動移行するものではありません。
- 実行例は[Docs](../samples/LithoSharp.DocsSample/README.ja.md)、[Blog](../samples/LithoSharp.Sample/README.ja.md)、[MDX](../samples/LithoSharp.MdxSample/README.ja.md)にあります。

## 診断・テスト・運用

- [診断リファレンス](diagnostics.ja.md)では原因、検出工程、修正する場所、検査範囲を確認できます。
- [CLI](cli.ja.md)はfactory、生成、preview、inspection、preflight、cacheのコマンドを説明します。
- [VS Code](../extensions/lithosharp-vscode/README.ja.md)と[言語サーバー](language-server.md)は診断、セクションのアウトライン、project context、信頼が必要な操作を扱います。言語サーバーのプロトコルは英語です。
- [Testing](testing.ja.md)と[Testing API・移行表](Testing-v2-api.md)はサイトのテスト、snapshotの所有期間、対応selector、移行例を説明します。APIの詳細は英語です。
- [生成済み出力の検査](generated-output-checks.ja.md)と[サイト品質](site-quality.md)は成果物、HTML、リンク、anchor、任意の外部HTTP検査を扱います。サイト品質の詳細には英語の説明を含みます。
- [差分ビルド](incremental-builds.ja.md)と[ビルドグラフ](build-graph.ja.md)は依存関係、キャッシュの再利用、無効化理由、出力の所有者を説明します。
- [機能情報](tooling-capabilities.ja.md)、[互換性契約](compatibility-contract.ja.md)、[既知の制約](known-limitations.ja.md)で、解析ごとに確定できる範囲とコード実行の条件を確認できます。
- [性能](performance.ja.md)には100ページのDocsの参考時間を記載しています。

## パッケージを選ぶ

| パッケージ・ツール | 用途 |
| --- | --- |
| LithoSharp | サイト生成、Markdown、ルート、コンテンツのCore API |
| LithoSharp.Generators | コンパイル時のbinder、schema、型付き参照、明示的な静的カタログ |
| LithoSharp.Analyzers | 対応する静的API、HTML、参照パターンのコンパイル診断 |
| LithoSharp.CodeFixes | 文書化したRoslynの修正機能。診断とは別に導入 |
| LithoSharp.Images | 画像変換とresponsive image |
| LithoSharp.Mdx | 任意で導入するMDX/Reactと文書サイトの機能 |
| LithoSharp.Testing | サイト、出力、DOM snapshot、部品、レイアウトの検証 |
| LithoSharp.Tool | コマンドラインのhost |
| LithoSharp.ProjectTemplates | 新しいサイトを作るテンプレート |
| LithoSharp for VS Code | エディターのコマンドと言語サーバー。Marketplaceで別途公開 |

[配布とインストール](distribution.ja.md)では、導入方法、パッケージの選択、NuGetとMarketplaceの別々のバージョンを説明します。

## 言語と過去の資料

多くの利用ガイドには英語版と日本語版があります。この索引にある技術リファレンスには英語の説明も含むため、日本語だけですべての詳細を読めるわけではありません。各リンク先の説明と対応範囲を確認してください。

[更新履歴の資料](history/README.md)には以前の移行手順を残しています。

脆弱性の非公開報告は[Security](../SECURITY.md)、再配布する部品は[third-party notices](../THIRD-PARTY-NOTICES.md)を参照してください。これらは英語の資料です。
