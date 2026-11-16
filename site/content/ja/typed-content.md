---
title: 型付きコンテンツと参照
supported_version: "3.0"
prerequisites: [quickstart]
---

`WebsiteModels.cs` は、`ExampleFrontMatter` と、`InputRoot = "examples"` を指定した静的コレクションを宣言します。
プロジェクトは Markdown 入力を `AdditionalFiles` として渡し、各ファイルに項目 ID とルートを一つずつ指定します。
ジェネレーターは、バインダー、スキーマ、カタログ、型付き参照を生成します。
`FromCatalog` は、読み込み前に、実行時の入力集合が宣言と一致することを確認します。

[日本語の実行例](../../examples/ja/typed-content/)では、必須のタイトル、列挙型、
`SupportedVersion` の `ContentRequiredWhen("status", "Published")` を使います。
メンバーの別名には `ContentName`、無視するメンバーには `ContentIgnore` を使います。
リーダーは、重複または衝突する名前、未対応の YAML 構文、不正な型の値を拒否します。
部分的な解析結果は有効なマッピングを証明しません。

通常のハンドブックのページには `DocumentSemanticRuleSet.Documentation` を使います。
公開するページには `supported_version` が必要です。
`prerequisites` は、同じコレクション、バージョン、言語の文書を参照します。
このページの前提文書 `quickstart` も実際の参照です。
参照先の欠落や未公開の状態は、十分な根拠がある場合だけ診断します。
公開状態が不明、または対象範囲が不完全な場合は、`Deferred` を返すことがあります。
OKF メタデータには別のポリシーを使います。

エディターの補完候補を作るために、コンテンツの DTO をインスタンス化しないでください。
信頼したプロジェクトの明示的な検査で静的なスキーマ記述子を出力します。入力中はこの記述子を使います。
C# のスキーマ定義や入力集合が変わると、対応する根拠は無効になります。
スキーマとカタログの詳しい契約は、リポジトリの
[ジェネレーターガイド](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Generators/README.md)を参照してください。
