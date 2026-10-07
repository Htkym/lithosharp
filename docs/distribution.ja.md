# 配布とインストール

[English](distribution.md)

LithoSharp 1.1.0はNuGetで配布しています。[LithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp)はVS Code Marketplaceからインストールできます。拡張機能の識別子は`htkym.lithosharp`です。

## NuGetパッケージ

プロジェクトで使うパッケージの版を1.1.0に揃えてください。対象は`LithoSharp`、`LithoSharp.Generators`、`LithoSharp.Images`、`LithoSharp.Tool`、`LithoSharp.Testing`、`LithoSharp.Mdx`、`LithoSharp.ProjectTemplates`です。ツールとテンプレートは別のパッケージです。

```sh
dotnet new install LithoSharp.ProjectTemplates::1.1.0
dotnet tool install LithoSharp.Tool --version 1.1.0 --tool-path .tools
```

サイトの作成と配信は[Quick Start](quickstart.ja.md)を、既存プロジェクトの更新は[更新手順](release-1.1.ja.md)を参照してください。

## VS Code拡張機能

```sh
code --install-extension htkym.lithosharp
```

同じ拡張機能をWindows x64、Linux x64、macOS arm64で利用できます。拡張機能本体、各環境のネイティブ資産を含む言語サーバー、MDXワーカーのソースとlockfileを同梱しています。同梱サーバーには.NET 10ランタイムが必要です。サイトのビルドと配信には、10.0.300以降の.NET 10 SDKも必要です。

`LithoSharp: Select Project`、`LithoSharp: Build`、`LithoSharp: Open Preview`の順に実行してください。設定と問題への対処は[拡張機能のガイド](../extensions/lithosharp-vscode/README.ja.md)を参照してください。取得したVSIXは`code --install-extension <path-to-vsix>`でもインストールできます。

## MDXワーカーの復元

Markdownだけのサイトや診断にはNode.jsは不要です。MDXにはNode.js 24.13.0と、明示的なワーカーの依存関係の復元が必要です。

編集時の診断には`LithoSharp: Restore MDX Worker`を実行してください。拡張機能は、同梱ソースをハッシュ名の専用ストレージにコピーし、そこで`npm ci --ignore-scripts --no-audit --no-fund`を実行してlockfileに従って依存関係を復元します。復元済みの`node_modules`は拡張機能に含めません。`lithosharp.workerDirectory`で保存先を変更した場合は、`LithoSharp: Restart Language Server`の実行が必要です。

サイトのビルドにはCLIの`restore-mdx`を使います。詳しくは[MDXガイド](mdx.ja.md)を参照してください。ビルド、配信、キー入力でnpmの依存関係を自動復元することはありません。コード実行や依存関係のインストールを伴うコマンドには、ワークスペースの信頼が必要です。

## 言語サーバーを個別に使う場合

同梱サーバーは`dotnet LithoSharp.LanguageServer.dll`で起動します。`deps.json`、`runtimeconfig.json`、管理された依存関係、ネイティブ資産、ワーカーのソースを一緒に配置してください。サーバーは独立したNuGetパッケージではありません。trimming、単一ファイル配布、Native AOTには対応していません。独自クライアントについては[言語サーバーのプロトコル](language-server.md)を、環境やコード実行の境界については[既知の制約](known-limitations.ja.md)を参照してください。
