---
title: Open Knowledge Format
supported_version: "3.0"
prerequisites: [quickstart]
---

サイトファクトリは `OkfBundle.ReadAsync` で `site/knowledge/` を読みます。
`AsOf` の日時を固定し、サイトに明示したベース URL を渡します。
`RenderDocs` と取得済みの補助アセットを通常の出力トランザクションに登録します。
[英語の概念](../../knowledge/en/typed-sites.html)と[対応する日本語](../../knowledge/ja/typed-sites.html)を確認できます。

小文字で完全一致する `index.md` と `log.md` はすべての階層で予約されています。
それ以外の Markdown ファイルは概念、Markdown 以外の入力は補助ファイルです。
概念のパスは通常の型付き文書スキーマとは独立した識別情報です。
リーダーは未知のメタデータを保持し、構造上の適合性、lint、鮮度、信頼性を区別します。
既定では、下書き、古い記録、未検証の記録も閲覧できます。

このバンドルは形式のバージョンと出典を宣言しますが、検証や生成の証明記録を捏造しません。
出典の項目や脚注は、技術的な正しさを証明しません。
信頼性に応じて公開を制限するには、明示的なポリシーが必要です。

保持エクスポートは取得済みのバイト列を保持し、YAML のシリアル化とは別の操作です。
CLI の文書にある OKF の検査・エクスポートコマンドで新しい出力先を指定し、公開前に診断を確認してください。
エクスポートは信頼性の記録を更新しません。
通常の `DocumentSemanticRuleSet.Documentation` のルールはこのバンドルには適用しません。
形式とツールの制約は、リポジトリの[既知の制限](../../reference/ja/known-limitations/)を参照してください。
