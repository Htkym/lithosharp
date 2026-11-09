# LithoSharp for VS Code

[English](https://github.com/Htkym/lithosharp/blob/main/extensions/lithosharp-vscode/README.md)

VS CodeからLithoSharpサイトをビルド・プレビューできます。Markdown/MDXの診断と、見出しによる文書内の移動にも対応しています。

このガイドは拡張機能0.4.0とCore/Tool 2.2.0を対象にしています。インストール前に、それぞれの版の公開状況を確認してください。Toolはサイトと同じバージョンを使います。CoreとMarketplaceの拡張機能は別々に公開されます。利用できる機能はバージョン名から推測せず、選択したToolのcapabilityで確認してください。

## preflightとcapability

`LithoSharp: Preflight` はtrusted workspaceで明示実行するコマンドです。選択中のCLIから互換capability reportを取得し、実際に通知された **Static inputs**（保存済みのactive Markdown/MDXファイルだけ）または **Trusted project catalog**（コンパイルと信頼したfactory/catalogの実行）を選びます。公開済みCore/Tool 1.1を含め、対応capabilityがない場合は理由を表示して実行しません。既存のBuild、Inspect、live診断は維持します。versionだけから新機能を推定せず、未保存の編集はlive診断で検査します。preflightでは描画・公開を行わず、成功したsnapshotを将来の描画許可には使いません。

OutputではLanguage Server同梱CoreとprojectのCore参照を区別します。initializeだけではproject capabilityを取得しません。MDX workerの利用可否は実際の解析後に通知し、利用不能時もMarkdown診断を維持してRestore MDX Workerを案内します。Restore・Restart後には開いているbufferを再送します。C#診断とCodeFixは既存のRoslyn hostが担当し、LSA1402/1403やFixAllに拡張機能側で対応しているとは表示しません。

## インストールと使い方

以下のMarketplace手順では、現在公開されている拡張がインストールされます。0.4.0の公開前に検証する場合は、対応するソースから作成したレビュー済みのVSIXを使ってください。

[MarketplaceのLithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp)をインストールするか、次のコマンドを実行してください。

```sh
code --install-extension htkym.lithosharp
```

必要な環境は次のとおりです。

- VS Code `^1.139.0`。
- 同梱の言語サーバーには.NET 10ランタイムが必要です。サイトのビルドと配信には、10.0.300以降の.NET 10 SDKと、サイトに対応するLithoSharp.Toolも必要です。2.2.0のサイトには2.2.0を、2.1.3を継続するサイトには2.1.3を使ってください。候補版の検証では提供された固定バージョンにそろえます。SDKには必要なASP.NET Core共有フレームワークが含まれます。
- MDXの解析にはNode.js 24.13.0が必要です。Markdownだけの診断にはNode.jsは不要です。

[Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.ja.md)でサイトを作成し、そのフォルダーをVS Codeで開いてください。コードを信頼できる場合はワークスペースを信頼し、`LithoSharp: Select Project`、`LithoSharp: Build`、`LithoSharp: Open Preview`の順に実行してください。
MDXの診断には`LithoSharp: Restore MDX Worker`で同梱ワーカーの依存関係も復元してください。

## プロジェクト選択とコマンド

- `*.csproj`の参照、ツールマニフェスト、中央パッケージ設定などの静的ファイルからプロジェクトを検出します。検出のためにMSBuildを評価しません。
- `LithoSharp: Select Project`でプロジェクトを選択します。複数のフォルダーを開いた場合も選択をフォルダーごとに保持し、同名のプロジェクトを区別します。
- `LithoSharp: Build`でサイトをビルドし、`LithoSharp: Inspect Site`でCLIの検査結果を表示します。
- `LithoSharp: Start Server`と`LithoSharp: Stop Server`で選択したプロジェクトの開発サーバーを操作します。サーバーはプロジェクトごとに管理され、ほかのプロジェクトのプロセスは停止しません。
- 選択したCLIは出力チャネルで確認できます。ステータスバーには選択したプロジェクトと解析・明示操作・配信の短い状態を表示します。

## 検査状態と診断の説明

ステータスバーをクリックするか、`LithoSharp: Show Inspection Status`を実行すると、Outputで詳細を確認し、次に実行する操作を選べます。ホバーでは次の四つを分けて表示します。

- 現在の文書の解析状態。現在のMarkdownまたはMDXの版について、未解析、待機中、解析完了、解析不能を示します。解析完了は診断がないことや公開できることを意味しません。
- プロジェクト情報。未取得、取得済み、更新が必要な状態と、文書の解決状態、適用したcontextの識別情報を示します。InspectまたはBuildでcontextを取得します。取得日時だけでは、すべての入力が最新と一致することは保証できません。
- スキーマの検査可能範囲。組み込み型へのバインド対応、独自型はBuildで確認、範囲不明のいずれかを、実際の解析経路から通知します。スキーマ名だけではバインドの対応や成功を判断しません。MDXにMarkdownのバインド範囲を引き継ぐこともありません。
- 最後の明示操作。コマンド、対象、日時、終了状態を示します。Build成功はBuildの成功であり、Qualityを有効にしたことや実行したことを意味しません。Preflightでは通知された段階、Partialの範囲、必要な後続検査の情報を保持します。

編集、文書の開き直し、改名、プロジェクトの切り替え、サーバーの再起動で、古い文書状態を失効させます。プロジェクト入力の変更を検知した場合は、contextを更新が必要な状態にし、編集時の検査から取り下げます。検知していない入力まで最新とは保証しません。表示を更新するためだけにfactoryやビルドを実行することはありません。

プロジェクトの入力が未保存の場合は、ディスク上のファイルに対するBuildやInspectが成功しても、contextを取得せず、更新が必要と表示します。取得済みのcontextも編集時の検査から取り下げます。contextを取得するには、未保存の入力を保存してから再実行してください。拡張機能が編集中の内容を自動保存することはありません。

CLIの応答は対応するschema majorと必須項目の型を確認します。プロセスの終了コード、envelopeの`success`、`exitCode`が一致した場合だけ成功と表示します。失敗、中断、不正な応答、非対応schema、結果の矛盾を区別し、取得できた診断と元の構造化された詳細をOutputに残します。遅れて完了した別プロジェクトの結果で、選択中の状態を上書きしません。

登録済みのProblemsの診断コードから、[診断リファレンス](https://github.com/Htkym/lithosharp/blob/main/docs/diagnostics.ja.md)を開けます。検出する段階、修正箇所、残る検査を確認してください。未登録のコードは通常のコード表示を保ちます。GeneratorとAnalyzerの診断は既存のhostが担当し、拡張側へ複製しません。

## 編集支援とプレビュー

Markdown/MDXの診断には元のIDを表示します。変更をまとめて送る待ち時間は既定で150 msで、古い診断結果は破棄します。見出しはアウトラインに表示されます。入力時にサイト全体のビルド、SSR、利用者のモジュールの実行は行いません。

Markdownのアウトラインは同じ解析snapshotの見出しと`MarkdownSections`を使います。`selectionRange`はジャンプ先の見出し、`range`はそのセクション全体の範囲を示し、階層もセクション情報に従います。セクション情報が取得できない場合は、分かっている見出しの範囲だけを使います。これはMarkdownの経路の説明です。MDXの見出しにも同じセクション範囲を保証するものではありません。

解析の利用可否は診断とは別の状態として通知します。`LSM013`は入力や解析資源の上限などで解析を完了できなかったことを示します。エディター入力の上限は8 Mi UTF-16コード単位です。診断が空でも、状態が`unavailable`なら検証済みとは扱えません。`NoContext`ではプロジェクト、独自C#スキーマ、サイトのルートは検査できず、`Partial`では取得できた情報だけが使えます。プロジェクトのsnapshotがある場合も、編集時に検証するのは組み込みスキーマだけです。独自C#スキーマはコンパイラーとサイトビルドで検証します。診断位置への移動やアウトラインは、サイト全体のビルドによる検証を置き換えません。

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
- サイトのビルドや配信を使えない場合は、.NET 10 SDKとサイトに対応するToolをインストールし、選択されているプロジェクトとCLIのパスを出力チャネルで確認してください。
- プレビューを開けない場合は文書をビルドし、経路、下書き設定、サーバーの出力を確認してください。

Markdownの対応範囲、コード実行の境界、長時間稼働の検証範囲は[既知の制約](https://github.com/Htkym/lithosharp/blob/main/docs/known-limitations.ja.md)を参照してください。
