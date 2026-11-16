# LithoSharp.Testing

[English](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Testing/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Testing/README.ja.md)

.NET ビルドで実際に生成するドキュメントサイトを検査できます。開発サーバーを起動せず、通常の .NET テスト内でサイトを生成します。任意のテストフレームワークで、コンテンツとスキーマのコンパイルに、出力、ルート、DOM のアサーションを組み合わせてください。アサーションが失敗すると `SiteTestException` をスローします。

このパッケージと、プロジェクト内のほかの LithoSharp パッケージには、LithoSharp 3.0.0 を指定してください。

```sh
dotnet add package LithoSharp.Testing --version 3.0.0
```

選択した LithoSharp パッケージのバージョンをそろえてください。HTML 解析自体は外部プロセスを起動しません。任意で MDX を生成する場合は、Node ワーカーを起動することがあります。

```csharp
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Testing;

await using var host = await SiteTestHost.CreateAsync(
    new SiteDefinition(new SiteSettings { BaseUrl = "https://example.com/" }, []));
host.AssertSucceeded();
host.AssertRoute("/index.html", "index.html");
host.AssertArtifact("index.html");
using var page = await host.OpenPageAsync("/index.html");
page.AssertElement("main");
```

出力、ファイルの所有情報、有効にしたキャッシュは、一時ディレクトリ内に隔離します。破棄時にはそのディレクトリツリーを削除します。ソースファイルは元の場所から読み取ります。タイムスタンプや品質設定を明示した場合は、その指定に従います。指定しない場合、ホストは Unix epoch を使用してローカルの品質検証を有効にします。ネットワークを使う検証には、外部リンクのオプションを明示する必要があります。ホストはサイト定義の出力先やキャッシュ先のパスには書き込みません。カスタムファクトリー、レンダラー、変換処理は信頼して実行するアプリケーションコードであり、それぞれが独自の I/O を実行できます。ホストはサンドボックスではありません。

ルート、ビルド計画、品質の検証に失敗した場合は、`Succeeded == false`、`Result == null` と、`AssertDiagnostic` で検査できる診断を返します。キャンセルや I/O 失敗など、ほかの例外は呼び出し元へ伝播します。診断のアサーションが成功したことをビルドの成功と解釈する前に、`AssertSucceeded` を呼び出してください。

`SiteTestDocument.RenderComponent` と `RenderLayout` は既存のレンダリングコンテキストを受け取ります。`Parse` は空の出力を含む単独の HTML を扱い、ツリー全体を解析できない場合は拒否します。`Snapshot.Query` と `QueryAll` は範囲と上限が定められたセレクター構文で、読み取り専用の要素ビューを返します。スクリプトやネットワーク通信は実行しません。`AssertText` と `AssertAttribute` は最初に一致する要素のテキストや属性値を厳密に比較します。`AssertMeta`、`AssertLink`、`AssertImage` はデコード後の属性値を厳密に照合します。URL の解決とリンク切れの検証は、ホストの品質検査が担当します。無効な引数には標準の引数例外をスローします。危険なファイルシステムパスは拒否し、破棄済みのホストや文書に対するアサーションには `ObjectDisposedException` をスローします。

破棄すると、保持していたスナップショットと要素ビューも無効になります。形式が不正なセレクターには `HtmlSelectorSyntaxException` をスローし、未対応の機能やクエリ上限には `HtmlSelectorUnsupportedException` をスローします。メジャーバージョン v2 で `Document` を削除しています。[スナップショットとクエリの仕様および移行例](https://github.com/Htkym/lithosharp/blob/main/docs/Testing-v2-api.md)を参照してください。

[テストガイド](https://github.com/Htkym/lithosharp/blob/main/docs/testing.md)と[日本語ガイド](https://github.com/Htkym/lithosharp/blob/main/docs/testing.ja.md)も参照してください。
