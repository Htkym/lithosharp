---
title: このサイトをビルドする
supported_version: "3.0"
prerequisites: [introduction]
sidebar_position: 2
---

`site/global.json` が指定する SDK と PowerShell 7 をインストールしてください。
このサイトは独立した NuGet 利用プロジェクトであり、実装へのプロジェクト参照を持ちません。
`LithoSharpVersion` の既定値は `3.0.0` です。これは予定するバージョンを示し、
パッケージが公開済みであることを意味しません。

リポジトリのルートから、承認されたフィードで利用できるパッケージのバージョンと、
新しい出力先を指定します。

```powershell
./site/Build.ps1 -LithoSharpVersion <available-version> -PackageFeed <feed> -Output <new-directory>
```

スクリプトは短い作業パスに専用の NuGet 設定とパッケージキャッシュを作ります。
サイトと明示的に対応付けたテストの依存関係を復元し、Release ビルド、サイト生成、スニペット検証を行います。
出力はローカルでのプレビュー用です。既定のベース URL は `https://example.invalid/` です。
ホスティング先の URL やサブパスを確認する場合は、明示的に選んだ値を `-BaseUrl` で渡してください。
デプロイや公開時点の比較基準の更新は行いません。

パッケージの正式公開後は、`-PackageFeed` を省略すると NuGet.org を使います。
LithoSharp の各パッケージは、同じバージョンにそろえてください。
ソース、ルート宣言、テスト、ジェネレーター用メタデータは `site/` にあります。
サンプルの入力集合が変わった場合は、生成カタログの再ビルドが必要です。

失敗の見方は[運用と検証](../operations/)を参照してください。
復元できない状態、テストのスキップ、実行記録の欠落は、サイト検証の成功ではありません。
