---
title: 追加のワークフロー
supported_version: "3.0"
prerequisites: [quickstart]
---

公開機能にはこのサイトの通常のビルドでは使わないワークフローもあります。
有効にする前に、それぞれの実際のサンプルプロジェクトと前提条件を確認してください。

| ワークフロー | ソースと詳細ガイド | 範囲と制限 |
|---|---|---|
| プロジェクトテンプレート | `LithoSharp.ProjectTemplates`、[配布](../../reference/ja/distribution/) | docs・blog・MDX・empty を選択。生成プロジェクトにも依存関係の復元が必要 |
| ブログと公開 | `samples/LithoSharp.Sample`、[クイックスタート](../../reference/ja/quickstart/) | 日時、環境、フィード、出力先を明示 |
| MDX・React とオフライン | `samples/LithoSharp.MdxSample`、[MDX](../../reference/ja/mdx/) | 信頼する Node・worker の復元と依存宣言が必要。Markdown だけの本ハンドブックでは起動しない |
| YAML コレクション | [型付き YAML](../../reference/ja/typed-yaml-collections/) | 型付きマッピング専用のローダー。任意の YAML をシリアル化する機能ではない |
| C# レイアウトとテーマ | [レイアウト契約](../../reference/ja/layout-css-contract/) | レイアウトと CSS の領域、宣言した描画処理の識別情報を一致させる |
| Docusaurus の取り込み | [移行ガイド](../../reference/ja/docusaurus-migration/) | 移行レポートを確認。未解決や未対応の構文はレビューが必要 |
| 出力とテスト | `LithoSharp.Testing`、[テスト](../../reference/ja/testing/) | 生成出力の検査、隔離したフィクスチャ、明示した基準 |
| 性能 | [性能](../../reference/ja/performance/) | 条件をそろえた同一マシンでの測定が必要。簡易検査から速度を主張しない |

対応表ではこれらを既存サンプルのワークフローとして記録します。
このサイトのビルドは、MDX、オフライン更新、外部検索、すべてのテンプレートを検証したことにはなりません。
専用の検査を実行し、実際の結果を保持してください。
