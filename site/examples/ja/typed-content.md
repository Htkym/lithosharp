---
title: 型付きコンテンツの例
locale: ja
status: Published
supported_version: "3.0"
---
# 型付きコンテンツの例

表示するコードは、このサイトと、明示的に対応付けたテストプロジェクトがコンパイルするソースから取り込みます。
[HasPublishedVersion](../../../api/ja/M-LithoSharp.Website.Examples.WebsiteExamples.HasPublishedVersion-c2e3a4b41e3d/) を呼び出します。

```csharp snippet="published-version" source="../source/WebsiteExamples.cs" region="PublishedVersion"
```

テストでは、公開文書のバージョンが欠落、空白、指定済みの各場合と、下書きを確認します。
対応付けたテストを実行するには、`Build.ps1` を使います。
スニペットの表示は現在の入力をテストしたことを証明しません。

:::component name="version-badge" version="3.0"
:::
