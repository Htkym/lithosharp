# LithoSharp のサイトソース

[English](README.md)

このパッケージ利用プロジェクトは3.0 向けの英日ハンドブック、正式な詳細ガイド、
パッケージとサンプルの文書、実行時ライブラリの API リファレンスを生成します。
`LithoSharpVersion` の既定値は対象とするパッケージの契約 `3.0.0` です。
パッケージやサイトの正式公開を示すものではありません。手動の GitHub Pages ワークフローは
https://htkym.github.io/lithosharp/ を公開先としています。ワークフローを用意しただけでは
公開の成功を確認したことにはなりません。公開先の URL と
[公開の前提条件](../eng/github-pages.md)を確認してください。

`global.json` の SDK、軽量な宣言検査に使う Node、PowerShell 7 を用意し、
リポジトリのルートから実行してください。

```powershell
node site/validate-coverage.mjs
./site/Build.ps1 -LithoSharpVersion <available-version> -PackageFeed <candidate-feed> -Output <new-directory>
```

選択した Syntamark の組が別のフィードにある場合、`PackageFeed` には配列を渡せます。
正式なパッケージの公開後は、省略して NuGet.org を使います。
スクリプトは NuGet の配布物と、その Generator のメタデータを使い、実装プロジェクトは参照しません。
候補のフィードとパッケージのキャッシュは、サイトのソースの外へ置いてください。
Windows では短い `-WorkDirectory` を使います。復元の前に、展開後のパスの長さを検査します。

スクリプトは利用プロジェクトと対応付けたテストをビルドし、生成出力の品質を検査します。
さらに、ソースを取り込む二つのフェンスを、実行したテストで検証します。
デプロイや、公開・翻訳レビューの基準の更新は行いません。
ホストやサブパスを明示して検査するには、`-BaseUrl` を渡します。
`example.invalid` は、プレビュー用に予約した既定値です。
生成器は出力をトランザクションで更新し、ソースツリーの再帰的な削除は行いません。

`feature-coverage.json` は、機能 ID を公開 API、英日ページ、サンプル、制約に対応付けます。
既存サンプルや参照だけのワークフローには、個別の検証が必要です。
`documentation-inventory.json` には、正式な英日原稿、公開 API の契約ファイル、
CLI の使い方、エディターのコマンドと設定、テンプレート、診断 ID を記録します。
`PublicDocumentation` は同じ原稿をサイトへ投影し、別に管理するガイドの複製は作りません。

API リファレンスは選択した Core、MDX、Images、Testing のパッケージにある、
公開する型の public と protected の宣言をすべて読みます。
対応するコンパイラーの XML を英語の説明に使い、内容のハッシュで識別したレビュー済みの翻訳を日本語に使います。
XML ID とインライン要素は維持します。
メタデータは選択したライブラリを実行せずに読みます。
宣言固有のコメントがない場合は、両言語で明示します。
外部依存先の参照カードは実際に参照する宣言に限ります。
共有する実行用サンプルも、コンパイラーの XML と明示した日本語の表示用入力を使います。
このサイトは Markdown を使い、任意の MDX worker は起動しません。

ビルドでは最初に、最新の `english-units.json` と `public-symbols.json` を作業ディレクトリへ出力します。
`validate-api-localization.mjs` は、翻訳がない説明を拒否します。
続いて `validate-generated-site.mjs` が実際の出力の網羅性と、API の両言語の表示を検査します。
パッケージの説明が変わった場合は、新しい説明を翻訳・レビューしてから再実行してください。
日本語ページで英語へ戻すために、この検査を弱めてはいけません。

`translation-reviews.json` は、明示的にレビューした原稿の正確なバイト列のハッシュを記録します。
両言語のファイルを確認した後だけ更新してください。ビルドは書き換えません。
`reference-translation-reviews.json` は、リポジトリの正式な原稿について同じ役割を持ちます。
ステージングでも、この記録を更新しません。
原稿のペアを明示的にレビューした後、`node site/update-documentation-inventory.mjs --write` で現在の一覧を記録します。
日本語の校正記録は公開入力ルートの外へ置きます。

対応する候補パッケージが利用できるまで、このプロジェクトはソリューションから分けています。
CI では、参照するすべてのパッケージを pack した後に `Build.ps1` を呼び、診断と出力を保持してください。
この検証工程からデプロイは行いません。
