# Golden Path

[English](golden-path.md)

導入からserveまでの対応経路は5つである。各手順で依存restoreとbuildの実行
境界を明示する。バージョンは[1.1の機能](release-1.1.ja.md)に従い、NuGet 1.1.0を使う。

## 1. Markdown-onlyのsite

```sh
dotnet new install LithoSharp.ProjectTemplates::1.1.0
dotnet tool install LithoSharp.Tool --version 1.1.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release --port 4317
```

template自身のproject restore以外に`dotnet restore`は要らない。Node.jsも
要らない。`serve`は子processでbuildし、project directoryを監視してloopback
で出す。[Quick Start](quickstart.ja.md)参照。

## 2. MDXのsite

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

## 3. 既存projectの更新

```sh
dotnet restore MySite -p:NuGetAudit=false
dotnet build MySite -c Release
.tools/lithosharp check MySite -c Release --format json
.tools/lithosharp build MySite -c Release
```

`check`は一時出力へ生成して検証し、公開しない。`build`はprojectをcompile
してから設定出力へ増分生成する。`clean`は所有の未変更成果物だけを消し、
cacheは消さない。`cache clean -o <directory>`で1出力の区分だけを明示回収する。

## 4. VS Code拡張機能

[LithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp)をインストールする。

```sh
code --install-extension htkym.lithosharp
```

同梱の言語サーバーには.NET 10ランタイム、ビルドには.NET 10 SDKとLithoSharp.Tool 1.1.0を用意する。`LithoSharp: Select Project`、`LithoSharp: Build`、`LithoSharp: Open Preview`の順に実行する。MDX診断にはNode.js 24.13.0と`LithoSharp: Restore MDX Worker`も必要である。未信頼のワークスペースでは検出だけが動作する。設定と対処方法は[拡張機能のガイド](../extensions/lithosharp-vscode/README.ja.md)を参照。

## 5. multi-rootのworkspace

folderごとに1回ずつ追加する。`LithoSharp: Select Project`はworkspace folder
とproject pathの組で保持し、同名projectは区別する。projectごとにserve状態
機械とpreview panelを持ち、1つの停止が他のprocessに触れることはない。
言語sessionはfolderごとにMarkdown/MDX文書を繋ぎ、診断がprojectを跨ぐ
ことはない。

## 実行境界

- .NET restore/buildは上記の明示commandである。buildがnpm packageを入れることはない。
- npm installは`restore-mdx`（MDX worker）と第三者siteのrestoreだけである。
- 走らない隠れ手順：`build`・`serve`・`check`・Editorキー入力での依存restore、
  移行時のJavaScript設定実行、テレメトリー（拡張は収集しない）。
