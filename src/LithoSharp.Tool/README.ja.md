# LithoSharp.Tool

[English](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Tool/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Tool/README.ja.md)

C# の型とビルドで管理するドキュメント向けの .NET 10 CLI です。サイトのコンパイルと生成、一時出力や保存済み出力の検査、静的ファイルのプレビューに使用します。各コマンドが検査する段階は異なるため、ソース検査の成功は最終 HTML の妥当性を証明しません。サイトのビルドには SDK 10.0.300 以降が必要で、配信には ASP.NET Core 共有フレームワークも使用します。サイトファクトリーは現在のユーザーの権限で実行されます。`capabilities` や `check-output` などの読み取り専用コマンドは、ファクトリーを実行しません。

このパッケージと、同じプロジェクトで使用する他の LithoSharp パッケージには、LithoSharp 3.0.0 を使用してください。

## 既存出力の検査

```sh
lithosharp check-output --output dist --base-url https://example.com/guide/ --format json
```

実際に公開しているベース URL を指定してください。このコマンドは、プロジェクトをコンパイルせず、ファクトリーを起動せず、ファイルを生成せず、キャッシュを書き込まずに、配布用の出力を読み取ります。ソースが最新であることの証明にはなりません。既存の `check` コマンドは一時出力を再生成します。エンコーディング、ファイル形式、プラットフォームの制限は[生成出力の検査](https://github.com/Htkym/lithosharp/blob/main/docs/generated-output-checks.ja.md)を参照してください。

## Preflight

信頼するプロジェクトの検証には Tool 3.0.0 と、同じバージョンの Core／MDX パッケージを使用します。

`capabilities --format json` は、この Tool の `preflight-static-inputs` と `preflight-trusted-catalog` コマンドを、スキーマ1.0の Preview 機能として公開します。このレポートはプロジェクトを評価しません。同梱された Core の識別情報を、選択したプロジェクトの Core の代わりに使用してはいけません。trusted preflight の応答は、ファクトリーアセンブリが参照する Core を `projectCoreVersion` として別に報告します。その Core のすべての機能や、後のレンダリングを保証するものではありません。

`lithosharp preflight --mode static --input page.md --input page.mdx --format json` は、明示的に指定した UTF-8 ソースバッファーだけを検査します。MSBuild、ファクトリー、インポートされた JavaScript モジュール、プロジェクト設定、プラグイン、SSR、バンドラーは評価しません。MDX は既存の解析専用ワーカーを使用します。固定された依存関係を明示的に復元し、必要に応じて `--worker-directory` でそのワーカーを選択してください。ワーカーの復元やライブ解析に使用する Node 実行ファイルを、`--node-executable` で明示的に指定します。省略時は PATH 上の `node` を使用します。この設定は信頼するコードのコンパイル前後に行う両方のソース検査に適用します。preflight 中にインストールや Markdown へのフォールバックを行うことはありません。

`lithosharp preflight site.csproj --mode trusted --input page.md --format json` は、プロジェクトのコンパイルと、ファクトリー／カタログのコード実行を明示的に許可します。ソースに Error があると、コンパイルやファクトリーの起動前に停止します。コンパイラーの失敗では、構造化された SARIF の規則 ID と位置を保持します。コンパイル後に選択したソースファイルを再読込し、別のホストで実際のカタログを取得して、レンダラーを呼ぶ前にルートを検証します。このコマンドが出力を公開することはありません。不明な拡張の prepare フックは Deferred のままで、実行しません。最終 HTML、テンプレートが所有する出力、アセット、動的コード、サイト全体の範囲は、このスナップショットの対象外です。結果が成功した場合に示すのは、報告した段階に Error がないことです。

コンパイラーの失敗では、取得できた規則カテゴリと関連ソース位置を、preflight JSON と SARIF の両方に保持します。ファイル URI、アーティファクトのインデックス、SARIF の URI ベースは、報告された実行作業ディレクトリに基づいて解決します。報告がない場合は、取得しておいたビルド起動時のディレクトリを使用します。プロジェクトのディレクトリと一時ログのディレクトリを、暗黙の URI ベースには使用しません。位置がなければ不明のままにし、使用できない関連位置は省略します。そのために主の Error や、ほかの有効な関連位置を破棄することはありません。コンパイラーが受理した抑制はそのまま適用し、コンパイラーの Error スナップショットが得られない場合は、引き続き LSC7002 をフォールバックとして使用します。

JSON の `preflight` と SARIF の `runs[0].properties` は、同じ段階のメタデータを持ちます。`coverage` は常に `Partial` です。`sourceCoverage` は、明示的な入力がなければ `NotRun`、選択した各入力について既存の解析 API が応答を返した場合は `Inspected`、それ以外は `Incomplete` です。`Inspected` は、スキーマ／プロジェクトコンテキストの検証が完全であることや、Error 診断がないことを示しません。coverage は `success` やプロセスの終了コードの代わりにはなりません。各 `sources` エントリーには、入力の絶対パス `file`、読み取った正確な生バイト列の SHA-256 である `sourceHash`、`readState`（`NotRead`、`Read`、`Utf8`）、`analysisState`（`NotRun`、`Inspected`、`Unsupported`、`Failed`）を記録します。生バイト列には UTF-8 BOM も含みます。ハッシュが null なら、バイト列は取得されていません。デコードと検査には同じバッファーを使用し、ハッシュの取得だけに別の読込を行うことはありません。trusted モードのコンパイル失敗では最初の入力観測を保持し、カタログ／ファクトリーの応答にはコンパイル後に再読込した入力を使用します。これらの観測は、その後のディスク内容、コンパイルした C# ソース、カタログが所有するコンテンツ、後のレンダリングを証明するものではありません。

`ruleVersion` は、同梱された Tool、Core、MDX ブリッジ、Syntamark の実装モジュールを、informational version と実際のモジュールバージョン ID で識別します。この識別情報は通信形式の `schemaVersion` とは異なり、バージョン名だけでは実装のフィンガープリントになりません。`unverifiedRuleSources` は、選択した外部 MDX ワーカー／依存関係と、任意のファクトリー／拡張コードを、この実装識別情報から明示的に除外します。完全な規則一式のダイジェストではなく、列挙したすべてのモジュールが実行されたと主張するものでもありません。テキスト出力は、段階、診断、Deferred の要約を保持します。終了コードは引き続き、報告した段階に Error がなければ0、検証／コンパイラー／ホストの失敗は1、使用方法の誤りは2、キャンセルは130です。JSON／SARIF モードでは、コンパイラーログとホスト出力を stderr に出します。

ライブラリーからは `SitePreflight.InspectMarkdown` または MDX の解析専用セッションを使用し、`SitePreflight.FromDiagnostics` で結果を組み立て、`SiteGenerator.PreflightFactoryAsync` を呼び出せます。`PreflightCatalogAsync` は取得済みの定義を検査します。`ISiteCatalogPreflightExtension` を実装する検査処理は、レンダリングしないカタログコードとして明示的に信頼します。preflight のスナップショットは、入力が変わった後のレンダリングを許可するものではありません。

通常の生成では、prepare フックを呼ぶ前に、取得した Core カタログと、利用できるすべての拡張カタログを検証します。組み込みの MDX、Blog、Documentation 拡張は、その処理で取得したカタログを準備処理へ渡します。MDX のルート／公開コールバックとローダーを再実行することはなく、取得済みの入力が変わると準備処理を停止します。レポート専用の preflight は、準備処理用のスナップショットを破棄します。検査やスナップショットの受け渡しがない拡張は、明示的に Deferred のままです。ビルド JSON はカタログの段階を含み、Core のビルドレポートは Deferred の情報診断を含みます。どちらもソースを完全に網羅したことや、将来のビルドを保証するものではありません。実行時のガードと最終 HTML の公開前検査は引き続き適用します。

同じバージョンの Tool とテンプレートパッケージをインストールして、サイトを作成します。

```sh
dotnet tool install LithoSharp.Tool --version 3.0.0 --tool-path .tools
dotnet new install LithoSharp.ProjectTemplates::3.0.0
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

Windows では `.tools/lithosharp.exe` を使用します。MDX は任意で、Node.js 24.13.0 と明示的なワーカーの復元が必要です。[クイックスタート](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.ja.md)を参照してください。

品質検証には `check --format text|json|sarif`、ビルドグラフとキャッシュ情報には `inspect --format json`、変更されていない所有ファイルの削除には `clean` を使用します。`build --clean` は出力全体を置き換えます。サイトは public で引数なしのコンストラクターを持つ `ISiteFactory` の実装を1つ公開します。通常のライブラリー利用側も同じ定義を使用できます。

Testing の移行は明示的なソースと、分離した旧バージョンのメタデータに対する静的レポートです。

```sh
lithosharp migrate testing --dry-run --source Tests.cs --reference /isolated/1.1/LithoSharp.Testing.dll --reference /isolated/1.1/AngleSharp.dll --format json
```

既存の JSON エンベロープに `migration` を追加し、バイト列の SHA256、元の UTF-16 位置、`ReadCandidate`／`Manual`／`Unknown`、理由、情報提供用のプレビューを出力します。ユーザーのアセンブリ、プロジェクト、ビルドターゲット、ジェネレーター、スクリプトは実行せず、ソースの編集や依存関係の削除も行いません。結び付きを確定できないものを自動変換にすることはなく、読み取りのプレビューでも確認が必要です。定数のセレクターは現在の Testing の文法で検査します。警告の抑制や生成コードの除外にかかわらず、結び付きが確認できた旧シンボルを検査します。インデックスを使う使用箇所は、外側の式全体を手動で確認する必要があります。LSA1501 は、結び付きが確認できたシンボルへの Warning と抑制ポリシーを維持します。通常のコンパイラーエラーもそのままエラーです。終了コードは、コンパイラーエラーなしで解析を終えた場合が0、入力／解析の失敗が1、使用方法の誤りが2、キャンセルが130です。対応表と網羅範囲の制限は [Testing の移行](https://github.com/Htkym/lithosharp/blob/main/docs/Testing-v2-api.ja.md#移行)を参照してください。

生成カタログのメンバーを明示的に選んで変更を確認するには、`preview-csharp-rename` に、呼び出し元の `--source` ファイル、元の `--reference` メタデータ、正確なメタデータ上の `--type`、`--member`、`--new-name` を指定します。JSON プレビューに含めるのは、Roslyn が証明した使用箇所と、そのバイトハッシュおよび元の期待文字列だけです。プロジェクトの評価や書き込みは行いません。対応する範囲と手動で確認する範囲は [C# 参照の rename](https://github.com/Htkym/lithosharp/blob/main/docs/csharp-reference-rename.ja.md)を参照してください。

`okf inspect`、`okf validate`、`okf export` は、明示的に選択した Open Knowledge Format バンドルを読み取ります。サイトプロジェクトの評価や、補助コードの実行は行いません。export は元のバイト列を保持します。固定された仕様、制限、別に定める公開ポリシーは [OKF](https://github.com/Htkym/lithosharp/blob/main/docs/open-knowledge-format.ja.md)を参照してください。

`proofread` は、呼び出し元が選択した Python 実行ファイルと Yomiyasu lint スクリプトを明示的に起動します。指摘はソース座標の精度を維持し、ガード付きのプレビューが文書を書き換えることはありません。[校正](https://github.com/Htkym/lithosharp/blob/main/docs/proofreading.ja.md)を参照してください。他のコマンドに Python は不要です。

`check-output --baseline ... --require-publication-compatibility` は、明示的に選択した公開済みマニフェストと URL およびアンカーを比較します。必須のベースラインがないか互換性がなければ、未検査のままとして処理は失敗します。ベースラインの初期化は明示的に行い、新規作成だけを許可します。通常のビルドがベースラインを昇格させることはありません。[出力検査](https://github.com/Htkym/lithosharp/blob/main/docs/generated-output-checks.ja.md)を参照してください。

ファクトリーの例、出力の動作、開発サーバーの制限、デプロイの対応範囲は [CLI ガイド](https://github.com/Htkym/lithosharp/blob/main/docs/cli.ja.md)を参照してください。
