# 配布とインストール

[English](distribution.md)

このガイドの対象はLithoSharp 3.0.0、言語サーバー3.0.0、VS Code拡張0.5.0、Syntamark 2.0.0です。
これらは開発対象のバージョンであり、公開の告知ではありません。
拡張の識別子は`htkym.lithosharp`で、本体とは別のバージョンを使います。

[バージョン一覧](../eng/version-matrix.json)では現在、Syntamark `2.0.0-preview.2026111303`を選定しています。
選定した成果物は[候補の来歴情報](../eng/markdown/candidate-provenance.json)と
[runtimeとSourceの組み合わせを記録したマニフェスト](../eng/markdown/component-pair.json)で識別します。
この候補は正式には公開されていません。正式採用には、レビューとマージを経たruntimeとSourceの組み合わせが、正式なリリース手順で公開される必要があります。

## NuGetパッケージ

プロジェクトで使うパッケージのバージョンを3.0.0にそろえてください。
対象は`LithoSharp`、`LithoSharp.Generators`、`LithoSharp.Images`、`LithoSharp.Tool`、
`LithoSharp.Testing`、`LithoSharp.Mdx`、`LithoSharp.ProjectTemplates`です。
任意で導入する`LithoSharp.Analyzers`と`LithoSharp.CodeFixes`も3.0.0を対象にしています。
ツールとテンプレートは別のパッケージです。

サイトで使うものだけをインストールしてください。Coreはランタイムで、Generatorsは型付きの静的コンテンツを追加します。
ImagesとMDXは任意の生成拡張、Testingはテスト用、AnalyzersとCodeFixesはコンパイラーやIDEへの追加機能です。
テンプレートはプロジェクトのひな形を作成するもので、実行時の依存関係にはなりません。

Coreはパッケージ一式に対応するSyntamarkのruntimeを参照し、GeneratorsとAnalyzersは対応するSourceの成果物を組み込みます。
利用者が`Syntamark.Source`を直接参照する必要はありません。
候補を使う場合は、runtimeとSourceのバージョン、および正規ソースの識別情報を、選定した組み合わせと一致させてください。
既定のプロファイルは`lithosharp-markdown/1`のままです。
CommonMarkや文書用プロファイルを明示的に選ぶ場合は[Markdownプロファイル](markdown-profiles.ja.md)を参照してください。

CoreはSkiaSharpやネイティブ画像ライブラリをインストールしなくなりました。
既存の画像ファイルはデコードせずに参照・コピーします。`LithoSharp.Images`は、リソースに上限を設けた任意のPNG処理を提供します。
JPEG、WebP、AVIFは既存ファイルを登録するか、別の手段で用意してください。
詳しくは[アセットと画像](assets-and-images.ja.md)を参照してください。

次のコマンドは設定済みのNuGetソースから対応する3.0.0のパッケージ一式を取得できる場合だけ実行してください。
正式公開されたパッケージを使うか、利用者が明示的に指定したソースから候補パッケージを使います。
候補を使う場合は、選定したSyntamarkのruntimeとSourceの組み合わせも必要です。
このガイドにバージョン番号が記載されているだけでは、パッケージを入手できることにはなりません。

```sh
dotnet new install LithoSharp.ProjectTemplates::3.0.0
dotnet tool install LithoSharp.Tool --version 3.0.0 --tool-path .tools
```

サイトの作成と配信は[Quick Start](quickstart.ja.md)を、既存プロジェクトの更新は[3.0への移行手順](migration-3.0.ja.md)を参照してください。
Core APIの互換性比較に記録している基準は2.2.0です。[2.0への移行手順](migration-2.0.ja.md)は過去のバージョン向けです。

## VS Code拡張機能

次のMarketplace向けコマンドは、拡張0.5.0がMarketplaceで入手可能になってから実行してください。

```sh
code --install-extension htkym.lithosharp@0.5.0
```

候補を使う場合は、明示的に提供された対応するVSIXを`code --install-extension <path-to-vsix>`でインストールしてください。

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
