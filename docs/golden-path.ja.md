# Golden Path

[English](golden-path.md)

以下はLithoSharp 2.2.0と拡張0.4.0の手順です。依存関係の復元とプロジェクトの実行が必要な段階を説明します。既存サイトの更新は[更新手順](migration-2.0.ja.md)を参照してください。

## Markdown-onlyのsite

```sh
dotnet new install LithoSharp.ProjectTemplates::2.2.0
dotnet tool install LithoSharp.Tool --version 2.2.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release --port 4317
```

新規DocsはC#スキーマ、生成Binder・Catalog、型付き参照、`RenderDocs`を使います。QualityはError閾値で明示的に有効にし、外部HTTP検査は無効です。入力の編集、検査、CIは[Quick Start](quickstart.ja.md)にまとめています。

projectのbuildではNuGetの依存関係が復元される場合があります。Node.jsは不要です。`serve`は子processでbuildし、project directoryを監視してloopbackで配信します。

## MDXのsite

```sh
.tools/lithosharp new mdx MyMdx -o MyMdx
dotnet build MyMdx -c Release
.tools/lithosharp restore-mdx MyMdx/bin/Release/net10.0/worker
.tools/lithosharp build MyMdx -c Release
.tools/lithosharp serve MyMdx -c Release --port 4317
```

npm依存を入れるのは`restore-mdx`だけです（package済みlockfileからの`npm ci --ignore-scripts --no-audit --no-fund`）。buildがnpm packageを入れることはありません。信頼できるMDXだけをbuildしてください。TypeScriptは変換のみで、型検査は行いません。

## 既存projectの更新

```sh
dotnet restore MySite
.tools/lithosharp build MySite -c Release
.tools/lithosharp check-output --output MySite/dist --base-url https://example.com/guide/ --format json
```

`build`はprojectをcompileしてから設定された出力先へ増分生成します。`check-output`はその配布対象を読み取り専用で検査します。実際の出力先と公開URLを指定してください。現在の原稿との一致は保証しません。`check`は一時出力へ再生成して検証し、公開しません。詳しくは[生成済み成果物の検査](generated-output-checks.ja.md)を参照してください。`clean`はLithoSharpが所有する未変更の成果物だけを削除し、cacheは削除しません。`cache clean -o <directory>`を使うと、1つの出力先の区分だけを明示的に削除できます。

## VS Code拡張機能

[LithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp)をインストールします。

```sh
code --install-extension htkym.lithosharp
```

同梱の言語サーバーには.NET 10ランタイム、ビルドには.NET 10 SDKとLithoSharp.Tool 2.2.0を用意します。`LithoSharp: Select Project`、`LithoSharp: Build`、`LithoSharp: Open Preview`の順に実行してください。MDX診断にはNode.js 24.13.0と`LithoSharp: Restore MDX Worker`も必要です。未信頼のワークスペースでは検出だけが動作します。設定と対処方法は[拡張機能のガイド](../extensions/lithosharp-vscode/README.ja.md)を参照してください。

## multi-rootのworkspace

ワークスペースへ各フォルダーを一度ずつ追加します。`LithoSharp: Select Project`はworkspace folderとproject pathの組でprojectを保持し、同名のprojectを区別します。projectごとにserveの状態管理とpreview panelを持ち、1つを停止しても他のprocessには影響しません。言語sessionはfolderごとにMarkdown/MDX文書を結び付け、診断がprojectを跨ぐことはありません。

## 実行境界

- `build`、`serve`、`check`は選択したprojectを`dotnet build`でコンパイルします。この処理で設定済みfeedからNuGetパッケージを復元する場合があります。project codeとMSBuild taskは信頼するコードとして実行します。
- npm依存のインストールはMDX workerの`restore-mdx`と、利用者が移行元サイトなどの依存関係を明示的に復元するときに行います。
- `build`、`serve`、`check`、エディター入力によるnpmの自動restoreはありません。エディターは入力のたびにMSBuildやsite factoryを実行しません。移行時にJavaScript設定を実行せず、拡張機能はテレメトリーを収集しません。

C#スキーマ、生成した参照、エディター、生成物検査を順に試すには[Docsサンプル](../samples/LithoSharp.DocsSample/README.ja.md#cの型定義から生成物の検査まで)を使います。コンパイル時に確定できる範囲、実行時の検査、文脈不足で検証できない状態を区別し、不正な例は通常のコンテンツから分離しています。
