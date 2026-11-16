---
title: 機能ガイド
supported_version: "3.1"
prerequisites: [introduction]
sidebar_position: 3
---

使いたい機能の例を試し、詳しいガイドへ進んでください。

## C# で要件を定義し、Markdown で文書を書く

Docs テンプレートではフロントマターを C# で定義します。

```csharp
public sealed class GuideFrontMatter
{
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public int Order { get; init; }
}
```

ページにはこの定義に対応する値を記述します。

```markdown
---
title: インストール
description: 最初のサイトを準備します。
order: 1
---

# インストール

クイックスタートに沿ってドキュメントサイトを作成します。
```

必須のタイトルを省略すると、テンプレートのジェネレーターが `LSG006` を報告します。
コンテンツのファイルを追加、移動、削除した後は再ビルドしてください。

[この構成のサイトを作成する](../quickstart/) · [型付き Markdown と YAML を詳しく見る](../typed-content/)

## 読者が探しやすいドキュメントにする

サイドバー、ページ間のナビゲーション、目次、バージョン、言語で文書を整理できます。
ローカル検索ではコレクション、バージョン、言語の範囲を指定できます。

このページの言語を切り替えてみてください。対応する文書に移動します。

[ドキュメントと多言語対応を設定する](../documentation/)

## Markdown にコンポーネントを追加する

コンテンツに合わせて Markdown プロファイルを選べます。
既定は `lithosharp-markdown/1` で、`syntamark-docs/2` では文書向けの構文を使えます。
登録した C# コンポーネントには型付きのリテラル属性を渡せます。
このサイトのバージョンバッジもその一例です。

```markdown
:::component name="version-badge" version="3.1"
:::
```

[バッジの表示例と設定](../profiles/)を確認してください。
コンポーネントの属性では C# の式を実行しません。
React で対話的な表示を行うには、オプションの MDX 機能を使えます。これには Node.js と明示的なワーカーのセットアップが必要です。

[テンプレート、MDX、テストを詳しく見る](../extended/)

## ガイドとともに API リファレンスを公開する

アセンブリのメタデータとコンパイラー XML から .NET API リファレンスを、OpenAPI 3 JSON から HTTP API リファレンスを生成できます。

実際の生成結果は [Core API](../../api/ja/lithosharp/)、[MDX API](../../api/ja/lithosharp.mdx/)、[Testing API](../../api/ja/lithosharp.testing/) で確認できます。
XML の説明がない場合は、説明文を補わずにその旨を表示します。

[API ドキュメントの生成方法を見る](../api/)

## ソースだけでなく出力も検証する

ビルド診断と出力の検証では確認する内容が異なります。
クイックスタートを終えたら、保存されたファイルを次のコマンドで検証できます。

```sh
.tools/lithosharp check-output --output MyDocs/dist --base-url https://example.com/ --format json
```

サイトに設定したベース URL を指定してください。Windows では `.tools/lithosharp.exe` を使います。

存在しない内部リンク先（`LSQ001`）や見出し（`LSQ002`）を検出できます。
現在のソースではなく保存済みの出力を読むため、最新の結果が必要な場合は先にビルドしてください。

[運用と検証を詳しく見る](../operations/) · [CLI とエディターを使う](../tooling/)

## サイトを拡張する

- **画像:** 既存の画像やレスポンシブ画像を使えます。オプションの画像処理機能は制限付きの PNG 処理に対応します。[画像ガイド](../images/)
- **知識バンドル:** 適合性、鮮度、信頼性のポリシーを分けて OKF バンドルを読み込み、保持できます。[知識ガイド](../knowledge/)
- **翻訳レビュー:** レビュー済みのコンテンツのハッシュを記録し、明示的に対応付けた信頼済みテストでスニペットを検証できます。[検証ガイド](../operations/)
- **既存サイト:** パッケージの更新を計画し、公開済みの URL を保持します。[移行ガイド](../migration/)

各ガイドでは設定方法と制限事項を説明しています。
条件付きの参照チェックには十分なカタログ情報が必要です。根拠が不明または不完全な場合は、検証の成功とは扱いません。
パッケージとガイドの全体像は、[詳細ドキュメントの索引](../../reference/ja/README/)で確認できます。