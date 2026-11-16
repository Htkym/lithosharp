# Golden Path

[English](golden-path.md)

次の手順はLithoSharp 3.0.0と拡張0.5.0を対象にしています。
インストール前に[バージョン一覧](../eng/version-matrix.json)を確認してください。候補の検証はパッケージの正式公開を証明しません。
各手順で、依存関係を復元する時点とプロジェクトのコードを実行する時点を説明します。
既存サイトでは[移行案内](migration-3.0.ja.md)に従ってください。

## Markdownだけのサイト

```sh
dotnet new install LithoSharp.ProjectTemplates::3.0.0
dotnet tool install LithoSharp.Tool --version 3.0.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release --port 4317
```

新規のDocsはC#スキーマ、生成されたバインダーとカタログ、型付き参照、`RenderDocs`を使います。
QualityはErrorを閾値として明示的に有効化し、外部HTTP検査は行いません。
編集方法、検査コマンドの対象範囲の表、Docs用の1つのCI手順は[Quick Start](quickstart.ja.md)を参照してください。
プロジェクトのビルドでNuGetの依存関係を復元する場合があります。Node.jsは不要です。
`serve`は子プロセスでビルドし、プロジェクトディレクトリを監視してループバックで配信します。
詳しくは[Quick Start](quickstart.ja.md)を参照してください。

## MDXサイト

```sh
.tools/lithosharp new mdx MyMdx -o MyMdx
dotnet build MyMdx -c Release
.tools/lithosharp restore-mdx MyMdx/bin/Release/net10.0/worker
.tools/lithosharp build MyMdx -c Release
.tools/lithosharp serve MyMdx -c Release --port 4317
```

npmの依存関係をインストールする手順は`restore-mdx`だけです。
パッケージに含めたlockfileから`npm ci --ignore-scripts --no-audit --no-fund`を実行します。
ビルドがnpmパッケージをインストールすることはありません。信頼して実行できるMDXだけをビルドしてください。
MDXやコンポーネントのコードは、信頼して実行するビルドコードとして動作します。
TypeScriptはトランスパイルしますが、型検査は行いません。

## 既存プロジェクトの更新

```sh
dotnet restore MySite
.tools/lithosharp build MySite -c Release
.tools/lithosharp check-output --output MySite/dist --base-url https://example.com/guide/ --format json
```

`build`は最初にプロジェクトをコンパイルし、設定した出力先に増分生成します。
`check-output`はその既存の配布物を検査します。実際の出力パスと公開時のベースURLを指定してください。
ソースの最新性を証明するものではありません。`check`は一時出力へ生成して検証し、公開はしません。
詳しくは[既存出力の検査](generated-output-checks.ja.md)を参照してください。
`clean`は所有している未変更の成果物だけを削除し、キャッシュは削除しません。
`cache clean -o <directory>`で、1つの出力パーティションを明示的に回収します。

## VS Code拡張機能

[LithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp)をインストールしてください。

```sh
code --install-extension htkym.lithosharp
```

同梱の言語サーバーには.NET 10ランタイムを、ビルドには.NET 10 SDKとLithoSharp.Tool 3.0.0をインストールしてください。
`LithoSharp: Select Project`、`LithoSharp: Build`、`LithoSharp: Open Preview`を実行します。
MDXの診断にはNode.js 24.13.0と`LithoSharp: Restore MDX Worker`も必要です。
未信頼のワークスペースで利用できるのは検出だけです。
設定と問題への対処は[拡張機能のガイド](../extensions/lithosharp-vscode/README.ja.md)を参照してください。

## マルチルートのワークスペース

各フォルダーを一度ずつ追加してください。
`LithoSharp: Select Project`はワークスペースフォルダーとプロジェクトパスの組ごとに1つのプロジェクトを保存し、同名のプロジェクトも区別します。
各プロジェクトは配信の状態管理とプレビューパネルを自身で持ち、1つを停止しても別のプロジェクトのプロセスには触れません。
言語セッションはフォルダーごとにMarkdown/MDX文書を接続し、診断がプロジェクトをまたぐことはありません。

## 実行の境界

- .NETの復元とビルド: `build`、`serve`、`check`は選択したプロジェクトを`dotnet build`でコンパイルします。この処理で設定したフィードからNuGetパッケージを復元する場合があります。プロジェクトのコードとMSBuildタスクは、信頼して実行する入力です。
- npmのインストール: MDX workerの`restore-mdx`と、サードパーティーのサイトの復元だけです。
- `build`、`serve`、`check`、エディターのキー入力でnpmを自動復元することはありません。エディターはキー入力のたびにMSBuildやサイトファクトリを実行しません。移行でJavaScript設定を実行することはなく、拡張機能はテレメトリーを収集しません。

型付きC#スキーマ、生成された参照、エディター、出力検証の流れは[Docsサンプルの手順](../samples/LithoSharp.DocsSample/README.ja.md#cの型定義から生成物の検査まで)に従ってください。
コンパイル時の証明、実行時の検査、利用できないコンテキストを区別し、意図的に不正にした例は通常のサイト入力の外に置いています。
