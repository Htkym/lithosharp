---
title: Open Knowledge Format
supported_version: "4.0"
prerequisites: [quickstart]
---

Open Knowledge Format（OKF）は、YAML メタデータを付けた Markdown 文書と関連ファイルをまとめる形式です。
LithoSharp 4.0.0 では、ローカルにある OKF 0.2 と 0.3 のバンドルの調査、検証、エクスポート、サイト生成をプレビュー機能として利用できます。

## バンドルのバージョンを選ぶ

バンドルのルートに `index.md` を置き、YAML フロントマターに
`okf_version: "0.2"` または `okf_version: "0.3"` を記述してください。
引用符は必須です。これは LithoSharp で OKF を検証してサイトを生成するときの要件です。
通常の Markdown には必要ありません。既存の規則を保つには 0.2 を選びます。0.3 には次の違いがあります。

- 信頼性は現在有効な検証記録から判定します。過去の検証記録は履歴として残ります。日付だけの値は UTC の 0 時として扱います。
- 相対パスは、メタデータを記述した文書から解決します。バンドルのルートを再検索しません。
  先頭にスラッシュがある場合はルートから解決します。概念同士の参照はグラフの辺となり、補助ファイルや出典の範囲を示す説明とは区別します。
- 型を指定していない Markdown は概念として扱いません。名前がドットで始まるファイルとディレクトリはバンドルの対象外です。
  予約ファイルの `log.md` にフロントマターは書けません。

## ローカルのバンドルを確認する

```sh
lithosharp okf inspect --input knowledge --format json
lithosharp okf validate --input knowledge --format sarif
```

入力例、厳格な検査、元のバイト列を保持したエクスポートは[OKF ガイド](../../reference/ja/open-knowledge-format/)を参照してください。
未対応のバージョンや YAML 機能は、仕様に適合したとは報告しません。エディター上の診断や参照編集には 0.3 の規則を適用しません。
計算の実行、実行結果の証明、参照先 URL からの取得は行いません。

## Docs にバンドルを表示する

公式サイトの[ナレッジの例](../../knowledge/index.html)では 0.2 の規則を使います。
サイトファクトリは `OkfBundle.ReadAsync` で読み込み、`RenderDocs` で文書と取得済みの補助ファイルを公開します。
検証記録を読めても、記述内容が事実であるとは限りません。

Agent Ready は、公開済みのページの Markdown と発見用のリンクを配信する、別のオプションです。
公開する内容は[Agent Ready ガイド](../../reference/ja/agent-ready/)で確認してください。
