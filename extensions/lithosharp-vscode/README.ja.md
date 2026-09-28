# LithoSharp for VS Code (0.1.0)

[English](README.md)

日常利用の起点です。対象projectを特定し、CLIの利用を管理します。
projectの推測や、信頼前のコード実行はしません。

## 機能

- 静的ファイルからLithoSharpのprojectを探します（LithoSharp参照のある
  `*.csproj`、tool manifest、中央package設定）。検出のためにMSBuild評価はしません。
- workspace folderとproject pathの組で保持し、multi-rootでも混同しません。
  同名projectは区別して表示します。
- `LithoSharp: Select Project` で明示的に選択します。曖昧な場合は尋ねます。
- 採用したCLIのpathとversionをOutputとStatusBarに表示します。

## 信頼

Workspace Trustはlimited宣言です。project検出は未信頼でも動きます。
processを起こし得るcommandは最初に信頼を要求し、未信頼では理由を説明します。
workspace由来の実行path（`lithosharp.cliPath` はrestricted設定）は
信頼まで無視します。0.1.0にtelemetryはありません。

## 設定

- `lithosharp.cliPath`：明示のCLI実行ファイル。空なら自動解決します
  （project localのdotnet tool、次にglobal PATH）。解決自体は実行しません。
  versionは明示選択時に1回だけ問い合わせます。
- `lithosharp.projectPath`：選択したprojectファイル。

## 範囲

build、serve、診断、symbols、previewは後のタスク（V110-16以降）です。
このshellはprocessを持たないため、停止時はchannel・status・listenerの破棄だけです。

## Command（V110-16）

- `LithoSharp: Build`、`LithoSharp: Start Server`、`LithoSharp: Stop Server`、
  `LithoSharp: Inspect Site` は実CLIを機械出力で実行して結果を表示します。
  Buildは検証のみで、公開承認ではありません。
- projectごとにserve状態機械（Stopped/Starting/Running/Rebuilding/Failed/Stopping）を持ち、
  実process eventで遷移します。通常停止は構造化stdin shutdownで、process tree回収は
  timeout後の最終手段です。他のprocessには触りません。

## 編集支援（V110-17）

- Markdown/MDX文書は単一の編集用LSP sessionに繋ぎ、変更はdebounce
  （150ms、50〜1000ms可変）して送ります。診断は元のID付きで版数管理して表示し、
  古い結果は再表示しません。
- symbolsは保有範囲の見出し階層をOutlineへ出します。キー入力で全体buildや
  SSR、利用者moduleの実行は起きません。

## Preview（V110-18）

- `LithoSharp: Open Preview`、`Refresh Preview`、`Open in Browser` は
  実際にserveしている出力を薄いWebView shellで表示します。shell自体は変換せず、
  routeは検査結果のみ使い、推測しません。
- 不明・draft・未buildの文書は説明を出します。失敗時は成功結果を明示付きで残します。
  preview所有のserverは最後のpanelと共に止まり、利用者起動のserverは止めません。
