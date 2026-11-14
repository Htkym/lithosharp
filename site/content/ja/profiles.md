---
title: Markdown プロファイルとコンポーネント
supported_version: "3.0"
prerequisites: [typed-content]
---

プロファイルは `SiteGenerationOptions.Markdown.ProfileId` で明示的に選びます。
`lithosharp-markdown/1` は互換性を維持する既定値、`commonmark/0.31.2` は CommonMark、
`syntamark-docs/2` は文書用の拡張構文を選択します。
フロントマターのバインドと公開ルールは、プロファイルから独立しています。
プロファイルを変えると、互換性のあるキャッシュが無効になります。生成するアンカーと HTML を比較してください。
[Syntamark の契約ガイド](../../reference/ja/syntamark-contracts/)は、固定した runtime と Source の組、
UTF-16 の位置、選択項目の取得状態、増分セッション、ネイティブ YAML、脚注、校正の境界を説明します。

このページは登録した C# コンポーネントに、型付きのリテラル属性を渡して描画します。

:::component name="version-badge" version="3.0"
:::

`WebsiteFactory` は文書用プロファイルを選び、`VersionBadge` を `GeneratedBadges.Binder` と設定の識別情報で登録します。
`VersionBadgeProps` が、バインドとエディターの静的な属性検査に共通の C# スキーマです。
このコンポーネントは HTML を作る前に値をエンコードします。
属性は C# の式を実行せず、通常の入力操作では描画処理も実行しません。

コンポーネントが読むファイル、値、アセットをすべて宣言してください。
参照型の属性には、明示した意味ルール、現在の `DocumentKey`、十分なカタログの根拠も必要です。
文脈を利用できない場合は、参照先の存在も不在も判定できません。
未知の属性と未対応の構文は、任意の HTML として扱わず、診断します。

文書用プロファイルは脚注も解析します。[^profile]

[^profile]: 選択したプロファイルで実際に出力する脚注です。
