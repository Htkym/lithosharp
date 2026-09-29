# Golden Path

[English](golden-path.md)

導入からserveまでの対応経路は5つである。各手順で依存restoreとbuildの実行
境界を明示する。versionは[1.1の機能](release-1.1.ja.md)に従う。packageは
配布（V110-23）まで1.0.0で入れる。

## 1. Markdown-onlyのsite

```sh
dotnet new install LithoSharp.ProjectTemplates::1.0.0
dotnet tool install LithoSharp.Tool --version 1.0.0 --tool-path .tools
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

## 4. VSIX導入（local）

0.1.0の拡張はMarketplaceにない。

```sh
npm --prefix extensions/lithosharp-vscode ci
npm --prefix extensions/lithosharp-vscode run vsix:pack
code --install-extension extensions/lithosharp-vscode/lithosharp-0.1.0.vsix
```

`npm ci`と`vsce`取得にはnetworkが要る。いずれもここで明示実行し、拡張の
内部では走らない。`vsix:pack`はcompile、MDX worker sourceのlockfile hash付き
staging、送信なしpackまで行う。

その後`LithoSharp: Select Project`、`LithoSharp: Build`、
`LithoSharp: Open Preview`を使う。拡張はCLIを解決し（project local tool、
次にPATH）、`lithosharp.languageServerPath`から言語serverを起こす。MDX診断
にはrestore済みworkerも要る。未信頼workspaceでは検出のみ動き、processを
起こすcommandは理由を説明する。

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
  移行時のJavaScript設定実行、telemetry（0.1.0の拡張は収集しない）。
