# LithoSharpのドキュメント

[English](README.md) | [日本語](README.ja.md)

このガイドの対象はLithoSharp 3.0.0、Syntamark 2、VS Code拡張0.5.0です。
開発上の対象を説明しており、これらのリリースが公開済みであることを示すものではありません。
[バージョン一覧](../eng/version-matrix.json)には選定したruntime/Sourceの組み合わせと、パッケージ・拡張機能の別々のバージョンを記録しています。
3.0.0を採用する場合は、LithoSharpのパッケージを同じバージョンにそろえてください。
必要な環境と導入手順はQuick Start、用途に合うかの判断は[導入選択ガイド](adoption-guide.ja.md)を参照してください。

## 最初のサイトを作る

1. [Quick Start](quickstart.ja.md)でSDK、Tool、テンプレートを用意し、Docsサイトを作成します。コンテンツを編集して、ビルド、配信、生成物の検査へ進みます。
2. [型付きコンテンツの体験手順](../samples/LithoSharp.DocsSample/README.ja.md#cの型定義から生成物の検査まで)で、C#モデル、生成した参照、Markdown、コンパイル診断、エディター、HTMLの検査をつなぎます。不正な入力と確定できない値の例は通常のコンテンツから分離しています。
3. [Golden Path](golden-path.ja.md)で、用途に応じた導入、生成、編集、診断、修正、検査の手順を確認します。
4. [3.0への更新手順](migration-3.0.ja.md)はネイティブYAML、画像参照、任意のPNG処理、Markdownプロファイル、取得したエディターの根拠を説明します。

## コンテンツと表示を選ぶ

- [Docsテンプレート](docs-template.ja.md)はサイドバー、ナビゲーション、検索、metadata、テーマの設定を説明します。
- [型付きMarkdown collection](typed-markdown-collections.ja.md)と[型付きYAML collection](typed-yaml-collections.ja.md)はモデル、binder、ルート、厳密な入力規則を説明します。
- [Source Generator](source-generators.ja.md)と[Analyzer](../src/LithoSharp.Analyzers/README.ja.md)は、宣言したカタログとコンパイル時の検証範囲を説明します。動的な値には実行時の検証が必要です。
- [MDX](mdx.ja.md)はReact、island、文書collection、バージョン、locale、API文書の作成を扱います。
- [資産と画像](assets-and-images.ja.md)と[HTML/CSSの契約](layout-css-contract.ja.md)は資産、変換、responsive image、表示の変更を説明します。
- [Docusaurus移行](docusaurus-migration.ja.md)で対応入力を検査・変換できます。任意のpluginやsite codeを自動移行するものではありません。
- 実行例は[Docs](../samples/LithoSharp.DocsSample/README.ja.md)、[Blog](../samples/LithoSharp.Sample/README.ja.md)、[MDX](../samples/LithoSharp.MdxSample/README.ja.md)にあります。

## Markdownと編集を設定する

- [Markdownプロファイル](markdown-profiles.ja.md)は、明示的なプロファイル選択、脚注、登録した型付きコンポーネントを説明します。front matterと公開ポリシーは別の設定です。
- [意味に関する制約](semantic-constraints.ja.md)は共通の静的規則と、参照検査に必要なカタログの根拠を説明します。独自コードやビルドが必要な判断を、エディターの変更ごとに実行しません。
- [参照編集](reference-editing.ja.md)と[C#の名前変更プレビュー](csharp-reference-rename.ja.md)は、取得した対象範囲、バージョン・ハッシュ・期待文字列による保護、未解決の手作業を説明します。
- [コード例の検証](code-example-verification.ja.md)と[校正](proofreading.ja.md)は明示的に要求した実行とソース位置への対応付けを説明します。根拠がない状態を成功と扱いません。
- [Open Knowledge Format](open-knowledge-format.ja.md)は、バンドルの入力規則、保持、エクスポートを説明します。通常のドキュメントには独自のメタデータと公開ポリシーがあります。

## 診断・テスト・運用

- [診断リファレンス](diagnostics.ja.md)では原因、検出工程、修正する場所、検査範囲を確認できます。
- [CLI](cli.ja.md)はfactory、生成、preview、inspection、preflight、cacheのコマンドを説明します。
- [VS Code](../extensions/lithosharp-vscode/README.ja.md)と[言語サーバー](language-server.ja.md)は診断、セクションのアウトライン、project context、信頼が必要な操作を扱います。
- [Testing](testing.ja.md)と[Testing API・移行表](Testing-v2-api.ja.md)はサイトのテスト、snapshotの所有期間、対応selector、移行例を説明します。
- [生成済み出力の検査](generated-output-checks.ja.md)と[サイト品質](site-quality.ja.md)は成果物、HTML、リンク、anchor、任意の外部HTTP検査を扱います。
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
| LithoSharp.Images | 任意のマネージドPNG変換。ほかの形式は既存のアセットとして登録 |
| LithoSharp.Mdx | 任意で導入するMDX/Reactと文書サイトの機能 |
| LithoSharp.Testing | サイト、出力、DOM snapshot、部品、レイアウトの検証 |
| LithoSharp.Tool | コマンドラインのhost |
| LithoSharp.ProjectTemplates | 新しいサイトを作るテンプレート |
| LithoSharp for VS Code | エディターのコマンドと言語サーバー。Marketplaceで別途公開 |

[配布とインストール](distribution.ja.md)では、導入方法、パッケージの選択、NuGetとMarketplaceの別々のバージョンを説明します。

## 言語と過去の資料

多くの利用ガイドには、先頭で相互にリンクした英語版と日本語版があります。
この日本語索引はコレクション、コンパイラー、言語サーバー、Testing API、品質、レイアウトの日本語ガイドへリンクしています。
コード、API識別子、実行可能な検証例の表記は原文のままです。

[更新履歴の資料](history/README.md)と、履歴資料の[2.xへの移行手順](migration-2.0.ja.md)には、1.xのTesting APIでの`Document`から`Snapshot`への移行など、以前の手順を残しています。
現在の更新には3.0のガイドを使用してください。

脆弱性の非公開報告は[Security](../SECURITY.md)、再配布する部品は[third-party notices](../THIRD-PARTY-NOTICES.md)を参照してください。これらは英語の資料です。
