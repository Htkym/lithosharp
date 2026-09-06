# LithoSharp Blog サンプル

`BlogSampleFactory` は通常実行とテストで同じサイト定義を返します。
テストプロジェクトからこのサンプルと `LithoSharp.Testing` を参照し、
次の例をリポジトリのルートで実行します。既存のサンプル用コマンドライン
オプションも利用できます。

## ファクトリのテスト

```csharp
using LithoSharp;
using LithoSharp.Testing;

await using var host = await SiteTestHost.CreateAsync(
    new BlogSampleFactory(),
    new SiteFactoryContext(Path.GetFullPath("samples/LithoSharp.Sample")));
host.AssertSucceeded();
host.AssertRoute("/index.html", "index.html");
using var page = await host.OpenPageAsync("/index.html");
page.AssertElement("main");
```

出力先とキャッシュを隔離し、外部リンクの検証は既定で無効にします。
成果物と診断の検証、失敗時の扱いについては、
[テストガイド](../../docs/testing.ja.md)を参照してください。
