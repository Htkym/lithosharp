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
