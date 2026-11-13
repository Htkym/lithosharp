---
title: API 文書
supported_version: "3.0"
prerequisites: [quickstart]
---

リファレンスには選択した NuGet パッケージの実行時ライブラリ 4 件を使います。
シグネチャ、公開する型とメンバー、可視性、引数、制約、継承は、実際のアセンブリのメタデータから取得します。
説明は対応するコンパイラーの XML から取得します。
非公開の補助機能と、公開しない型のメンバーは除外し、公開する型の public と protected のメンバーを含めます。

| ライブラリ | 英語 | 日本語 |
|---|---|---|
| Core | [LithoSharp](../../api/en/lithosharp/) | [LithoSharp](../../api/ja/lithosharp/) |
| MDX と文書 | [LithoSharp.Mdx](../../api/en/lithosharp.mdx/) | [LithoSharp.Mdx](../../api/ja/lithosharp.mdx/) |
| 任意の PNG 処理 | [LithoSharp.Images](../../api/en/lithosharp.images/) | [LithoSharp.Images](../../api/ja/lithosharp.images/) |
| テスト | [LithoSharp.Testing](../../api/en/lithosharp.testing/) | [LithoSharp.Testing](../../api/ja/lithosharp.testing/) |
| 共有する実行用サンプル | [サンプル](../../api/en/) | [サンプル](../../api/ja/) |

両言語で、同じ正確な API ID と引数名を維持します。
日本語では選択した XML の概要、補足、引数、戻り値などの説明をすべて翻訳します。
インラインの参照とコードは、識別情報を維持します。
新しい説明に翻訳がない場合は、英語を日本語の表示へコピーせず、ビルドを失敗させます。
パッケージに宣言固有の XML コメントがない場合は、両言語でそのことを明示し、取得できる構造を表示します。
このカードは動作を推測せず、関連する機能ガイドの代わりにもなりません。

`GenerateDocumentationFile` は、型付きサンプルに含める
`WebsiteExamples.HasPublishedVersion` の XML も出力します。
サイトはコンパイラーの XML と明示的な日本語の表示用入力を、
`ApiAssemblySource` と `ApiSymbol` とともに `ApiReferenceSite.AddXml` へ渡します。
同じメソッドを、[検証](../operations/)で説明する対応付けたテストでもコンパイルします。
[実装](https://github.com/Htkym/lithosharp/blob/main/site/PublicApiDocumentation.cs)と
[翻訳の検査](https://github.com/Htkym/lithosharp/blob/main/site/validate-api-localization.mjs)で、
メタデータと翻訳を再現可能な形で確認する手順を示します。

XML ID はオーバーロードとジェネリックの識別情報を保持します。
アセンブリ間の参照は明示した参照マップで接続します。
参照する外部宣言には専用の参照カードを用意しますが、各依存ライブラリ全体のリファレンスではありません。
文書だけの XML から、シグネチャや可視性は証明できません。
OpenAPI は明示的に渡す JSON を使い、外部参照はダウンロードせず診断します。
Tool、Generator、Analyzer、テンプレート、エディターの利用方法は、
この 4 件の実行時アセンブリの API とは区別し、
[詳細ガイド](../../reference/ja/README/)と各パッケージのガイドで説明します。

`ApiReferenceSite.Compare` は、文書、シグネチャ、可視性、非推奨、追加・削除の変化を分けます。
分類だけでは破壊的変更とは断定できません。
比較するスナップショットを明示的に選び、入力を保持してください。
根拠と公開の区別は、[運用](../operations/)を参照してください。
