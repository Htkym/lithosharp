# 配布とインストール

[English](distribution.md)

拡張の識別子は`htkym.lithosharp`で、本体とは別のバージョンを使います。

## NuGetパッケージ

プロジェクトで使うパッケージのバージョンをそろえてください。
対象は`LithoSharp`、`LithoSharp.Generators`、`LithoSharp.Images`、`LithoSharp.Tool`、
`LithoSharp.Testing`、`LithoSharp.Mdx`、`LithoSharp.ProjectTemplates`です。
任意で導入する`LithoSharp.Analyzers`と`LithoSharp.CodeFixes`も同じバージョンにそろえてください。
ツールとテンプレートは別のパッケージです。

サイトで使うものだけをインストールしてください。Coreはランタイムで、Generatorsは型付きの静的コンテンツを追加します。
ImagesとMDXは任意の生成拡張、Testingはテスト用、AnalyzersとCodeFixesはコンパイラーやIDEへの追加機能です。
テンプレートはプロジェクトのひな形を作成するもので、実行時の依存関係にはなりません。

Coreはパッケージ一式に対応するSyntamarkのruntimeを参照し、GeneratorsとAnalyzersは対応するSourceの成果物を組み込みます。
利用者が`Syntamark.Source`を直接参照する必要はありません。
既定のプロファイルは`lithosharp-markdown/1`のままです。
CommonMarkや文書用プロファイルを明示的に選ぶ場合は[Markdownプロファイル](markdown-profiles.ja.md)を参照してください。

CoreはSkiaSharpやネイティブ画像ライブラリをインストールしなくなりました。
既存の画像ファイルはデコードせずに参照・コピーします。`LithoSharp.Images`は、リソースに上限を設けた任意のPNG処理を提供します。
JPEG、WebP、AVIFは既存ファイルを登録するか、別の手段で用意してください。
詳しくは[アセットと画像](assets-and-images.ja.md)を参照してください。

```sh
dotnet new install LithoSharp.ProjectTemplates::3.1.0
dotnet tool install LithoSharp.Tool --version 3.1.0 --tool-path .tools
```

サイトの作成と配信は[Quick Start](quickstart.ja.md)を、既存プロジェクトの更新は[3.0への移行手順](migration-3.0.ja.md)を参照してください。
Core APIの互換性比較に記録している基準は2.2.0です。[2.0への移行手順](migration-2.0.ja.md)は過去のバージョン向けです。

## VS Code拡張機能

```sh
code --install-extension htkym.lithosharp@0.5.0
```

同じ拡張機能をWindows x64、Linux x64、macOS arm64で利用できます。
コンパイル済みの拡張機能、フレームワークに依存する言語サーバーとそのマネージド依存関係、MDXワーカーのソースとlockfileを同梱しています。
同梱サーバーには.NET 10ランタイムが必要です。
サイトのビルドと配信には10.0.300以降の.NET 10 SDKと、サイトに対応するバージョンのToolも必要です。

`LithoSharp: Select Project`、`LithoSharp: Build`、`LithoSharp: Open Preview`を使ってください。
設定と問題への対処は[拡張機能のガイド](../extensions/lithosharp-vscode/README.ja.md)を参照してください。

## MDXワーカーの復元

Markdownだけのサイトや診断にはNode.jsは不要です。MDXにはNode.js 24.13.0と、ワーカー依存関係の明示的な復元が必要です。

編集時の診断には`LithoSharp: Restore MDX Worker`を実行してください。
拡張機能は同梱ソースをハッシュ名の専用ストレージにコピーし、そこで`npm ci --ignore-scripts --no-audit --no-fund`を実行してlockfileに従って依存関係を復元します。
復元済みの`node_modules`は拡張機能に含めません。
`lithosharp.workerDirectory`で保存先を変更した場合は、`LithoSharp: Restart Language Server`の実行が必要です。

サイトのビルドにはCLIの`restore-mdx`を使います。詳しくは[MDXガイド](mdx.ja.md)を参照してください。
ビルド、配信、キー入力でnpmの依存関係を自動復元することはありません。
コード実行や依存関係のインストールを伴うコマンドには、ワークスペースの信頼が必要です。

## 言語サーバーを個別に使う場合

同梱サーバーは`dotnet LithoSharp.LanguageServer.dll`で起動します。
`deps.json`、`runtimeconfig.json`、マネージド依存関係、ワーカーのソースを一緒に配置してください。
サーバーは独立したNuGetパッケージではありません。trimming、単一ファイル配布、Native AOTには対応していません。
独自クライアントについては[言語サーバーのプロトコル](language-server.ja.md)を、
環境やコード実行の境界については[既知の制約](known-limitations.ja.md)を参照してください。
