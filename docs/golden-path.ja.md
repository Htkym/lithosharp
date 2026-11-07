# Golden Path

[English](golden-path.md)

導入からserveまでの対応経路は5つである。各手順で依存関係のrestoreとbuildの実行
境界を明示する。以下は2.1.3を対象にした手順である。候補feedを使う場合は、実際の固定候補版を指定する。既存サイトを更新する場合は[更新手順](migration-2.0.ja.md)を参照する。

## Markdown-onlyのsite

```sh
dotnet new install LithoSharp.ProjectTemplates::2.1.3
dotnet tool install LithoSharp.Tool --version 2.1.3 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release --port 4317
```

projectのbuildではNuGetの依存関係が復元される場合がある。Node.jsは不要である。`serve`は子processでbuildし、project directoryを監視してloopback
で配信する。[Quick Start](quickstart.ja.md)参照。

## MDXのsite

```sh
.tools/lithosharp new mdx MyMdx -o MyMdx
dotnet build MyMdx -c Release
.tools/lithosharp restore-mdx MyMdx/bin/Release/net10.0/worker
.tools/lithosharp build MyMdx -c Release
.tools/lithosharp serve MyMdx -c Release --port 4317
```

npm依存を入れるのは`restore-mdx`だけである（package済みlockfileからの
`npm ci --ignore-scripts --no-audit --no-fund`）。buildがnpm packageを
入れることはない。信頼できるMDXだけをbuildする。TypeScriptは変換のみで、
型検査はしない。

## 既存projectの更新

```sh
dotnet restore MySite
.tools/lithosharp build MySite -c Release
.tools/lithosharp check-output --output MySite/dist --base-url https://example.com/guide/ --format json
```

`build`はprojectをcompileしてから設定出力へ増分生成する。`check-output`は
その配布対象を読み取り専用で検査する。実際の出力先と公開URLを指定すること。
現在の原稿との一致は保証しない。`check`は一時出力へ再生成して検証し、公開しない。
詳しくは[生成済み成果物の検査](generated-output-checks.ja.md)を参照する。`clean`は所有の未変更成果物だけを消し、
cacheは消さない。`cache clean -o <directory>`で1出力の区分だけを明示的に回収する。

## VS Code拡張機能

[LithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp)をインストールする。

```sh
code --install-extension htkym.lithosharp
```

同梱の言語サーバーには.NET 10ランタイム、ビルドには.NET 10 SDKとLithoSharp.Tool 2.1.3を用意する。`LithoSharp: Select Project`、`LithoSharp: Build`、`LithoSharp: Open Preview`の順に実行する。MDX診断にはNode.js 24.13.0と`LithoSharp: Restore MDX Worker`も必要である。未信頼のワークスペースでは検出だけが動作する。設定と対処方法は[拡張機能のガイド](../extensions/lithosharp-vscode/README.ja.md)を参照。

## multi-rootのworkspace

folderごとに1回ずつ追加する。`LithoSharp: Select Project`はworkspace folder
とproject pathの組でprojectを保持し、同名projectは区別する。projectごとにserve状態
機械とpreview panelを持ち、1つの停止が他のprocessに触れることはない。
言語sessionはfolderごとにMarkdown/MDX文書を繋ぎ、診断がprojectを跨ぐ
ことはない。

## 実行境界

- `build`、`serve`、`check`は選択したprojectを`dotnet build`でコンパイルする。この処理で設定済みfeedからNuGetパッケージを復元する場合がある。project codeとMSBuild taskは信頼するコードとして実行する。
- npm依存のインストールはMDX workerの`restore-mdx`と第三者siteの明示的なrestoreで行う。
- `build`、`serve`、`check`、エディター入力によるnpmの自動restoreはない。エディターは入力のたびにMSBuildやsite factoryを実行しない。移行時にJavaScript設定を実行せず、拡張機能はテレメトリーを収集しない。

C#スキーマ、生成した参照、エディター、生成物検査を順に試すには[Docsサンプル](../samples/LithoSharp.DocsSample/README.ja.md#cの型定義から生成物の検査まで)を使う。コンパイル時に確定できる範囲、実行時の検査、文脈不足で検証できない状態を区別し、不正な例は通常のコンテンツから分離している。
