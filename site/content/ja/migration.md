---
title: LithoSharp 4.0 への移行
supported_version: "4.0"
prerequisites: [introduction]
---

## 3.x から更新する

パッケージ、CLI、プロジェクトテンプレートをそろえて 4.0.0 に更新します。
OKF バンドルを従来の規則で使うには、ルートの `index.md` に引用符付きで `okf_version: "0.2"` と書きます。
`"0.3"` を選ぶ前に、相対パス、検証記録、日時の扱いの違いを確認してください。
サイトを生成する前に `lithosharp okf validate --input knowledge` を実行します。
通常の Markdown にはこの宣言は不要です。詳しくは[OKF ガイド](../knowledge/)を参照してください。

## 2.x 以前から更新する

既存のサイトを更新する前に、[3.0 移行ガイドの全文](../../reference/ja/migration-3.0/)を読んでください。
固定した Syntamark の組み合わせ、ネイティブ YAML とコンテンツ属性、厳密な DTO バインド、
任意の PNG 処理、明示的なソーシャル画像、Markdown プロファイル、型付きコンポーネント、
取得したエディター用スキーマ、キャッシュの互換性、検証の根拠を説明しています。

移行では YamlDotNet の属性を `ContentName` と `ContentIgnore` に置き換えます。
Core の画像登録は任意の画像処理と分かれます。
従来の Markdown プロファイルが既定値のままなので、ディレクティブを導入する場合は文書用プロファイルを明示します。
新しい出力先を使い、公開に成功した時点の基準と URL やアンカーを比較してください。
