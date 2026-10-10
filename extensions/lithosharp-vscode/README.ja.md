# LithoSharp for VS Code

## 開発候補のpreflightとcapability

`LithoSharp: Preflight` はtrusted workspaceで明示実行するコマンドです。選択中のCLIから互換capability reportを取得し、実際に通知された **Static inputs**（保存済みのactive Markdown/MDXファイルだけ）または **Trusted project catalog**（コンパイルと信頼したfactory/catalogの実行）を選びます。公開済みCore/Tool 1.1を含め、対応capabilityがない場合は理由を表示して実行しません。既存のBuild、Inspect、live診断は維持します。versionだけから新機能を推定せず、未保存の編集はlive診断で検査します。preflightでは描画・公開を行わず、成功したsnapshotを将来の描画許可には使いません。

OutputではLanguage Server同梱CoreとprojectのCore参照を区別します。initializeだけではproject capabilityを取得しません。MDX workerの利用可否は実際の解析後に通知し、利用不能時もMarkdown診断を維持してRestore MDX Workerを案内します。Restore・Restart後には開いているbufferを再送します。C#診断とCodeFixは既存のRoslyn hostが担当し、LSA1402/1403やFixAllを拡張側で可能と表示しません。

[English](https://github.com/Htkym/lithosharp/blob/main/extensions/lithosharp-vscode/README.md)

VS CodeからLithoSharpサイトをビルド・プレビューできます。Markdown/MDXの診断と、見出しによる文書内の移動にも対応しています。

## インストールと使い方

[MarketplaceのLithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp)をインストールするか、次のコマンドを実行してください。

```sh
code --install-extension htkym.lithosharp
```

必要な環境は次のとおりです。

- VS Code `^1.139.0`。
- 同梱の言語サーバーには.NET 10ランタイムが必要です。サイトのビルドと配信には、10.0.300以降の.NET 10 SDKとLithoSharp.Tool 1.1.0も必要です。SDKには必要なASP.NET Core共有フレームワークが含まれます。
- MDXの解析にはNode.js 24.13.0が必要です。Markdownだけの診断にはNode.jsは不要です。

[Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.ja.md)でサイトを作成し、そのフォルダーをVS Codeで開いてください。コードを信頼できる場合はワークスペースを信頼し、`LithoSharp: Select Project`、`LithoSharp: Build`、`LithoSharp: Open Preview`の順に実行してください。
MDXの診断には、`LithoSharp: Restore MDX Worker`で同梱ワーカーの依存関係も復元してください。

## プロジェクト選択とコマンド

- `*.csproj`の参照、ツールマニフェスト、中央パッケージ設定などの静的ファイルからプロジェクトを検出します。検出のためにMSBuildを評価しません。
- `LithoSharp: Select Project`でプロジェクトを選択します。複数のフォルダーを開いた場合も選択をフォルダーごとに保持し、同名のプロジェクトを区別します。
- `LithoSharp: Build`でサイトをビルドし、`LithoSharp: Inspect Site`でCLIの検査結果を表示します。
- `LithoSharp: Start Server`と`LithoSharp: Stop Server`で選択したプロジェクトの開発サーバーを操作します。サーバーはプロジェクトごとに管理され、ほかのプロジェクトのプロセスは停止しません。
- 選択したCLIのパスとバージョンを出力チャネルとステータスバーに表示します。

## 編集支援とプレビュー

Markdown/MDXの診断には元のIDを表示します。変更をまとめて送る待ち時間は既定で150 msで、古い診断結果は破棄します。見出しはアウトラインに表示されます。入力時にサイト全体のビルド、SSR、利用者のモジュールの実行は行いません。

`LithoSharp: Open Preview`、`LithoSharp: Refresh Preview`、`LithoSharp: Open in Browser`で配信中のサイトを表示します。プレビューの経路はサイトの検査結果から取得します。不明な文書、下書き、未ビルドの文書には理由を表示します。更新が失敗した場合は、最後の成功結果であることを明示して表示を残します。プレビューのために起動したサーバーは、最後のプレビューパネルを閉じると停止します。`LithoSharp: Start Server`で起動したサーバーは引き続き動作します。

プレビューは保存・ビルド済みの文書が対象で、未保存の内容には対応していません。MDXワーカーはTypeScriptを変換しますが、型検査は行いません。

MDX preflightでは`lithosharp.nodeExecutable`をCLIの`--node-executable`へ渡し、live解析と同じ指定Nodeを使います。待機中のcapability probeは再実行、workspace・設定の変更、拡張終了で中止し、そのprobeが所有するプロセスツリーを停止します。

## ワークスペースの信頼

未信頼のワークスペースでもプロジェクトを検出できます。ビルド、配信、言語サーバーの起動、ワーカーの依存関係のインストールには信頼が必要です。ワークスペースで指定した実行ファイルのパスは、信頼するまで使用しません。この拡張機能はテレメトリーを収集しません。

## 設定

| 設定 | 用途 |
| --- | --- |
| `lithosharp.cliPath` | CLIの実行ファイル。空の場合はプロジェクト内のdotnet tool、次にグローバルのPATHから解決します。 |
| `lithosharp.projectPath` | 選択したプロジェクトファイル。 |
| `lithosharp.languageServerPath` | 言語サーバーの実行ファイルまたはDLL。空の場合はPATH上の`dotnet`で同梱サーバーを起動します。 |
| `lithosharp.workerDirectory` | MDXワーカーのディレクトリ。空の場合は拡張機能の専用ストレージを使います。変更後は`LithoSharp: Restart Language Server`が必要です。 |
| `lithosharp.nodeExecutable` | MDX解析用のNode.js実行ファイル。空の場合は言語サーバーの環境から`node`を解決します。変更後は`LithoSharp: Restart Language Server`が必要です。 |
| `lithosharp.diagnosticDebounceMs` | 診断の待ち時間。単位はミリ秒、既定は150、範囲は50〜1000です。 |

## 問題が起きたとき

- 診断が出ない場合は、LithoSharpの出力チャネル、ワークスペースの信頼、PATH上の.NET 10ランタイムを確認してください。サーバーやワーカーの設定変更後は`LithoSharp: Restart Language Server`を実行してください。
- MDXの診断が使えない場合はNode.js 24.13.0を確認し、`LithoSharp: Restore MDX Worker`を実行してください。復元前でもMarkdownの診断は動作します。
- ビルドやサーバーを使えない場合はLithoSharp.Tool 1.1.0と.NET 10 SDKをインストールし、出力チャネルで選択したプロジェクトとCLIのパスを確認してください。
- プレビューを開けない場合は文書をビルドし、経路、下書き設定、サーバーの出力を確認してください。

Markdownの対応範囲、コード実行の境界、長時間稼働の検証範囲は[既知の制約](https://github.com/Htkym/lithosharp/blob/main/docs/known-limitations.ja.md)を参照してください。
