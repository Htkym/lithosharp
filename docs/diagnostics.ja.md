# 診断リファレンス

[English](diagnostics.md) | [日本語](diagnostics.ja.md)

診断コードごとに、検出される段階と修正箇所を確認できます。この索引は代表的な診断の対処を説明します。詳しい条件は各項目のリンク先を参照してください。エディターに問題がないことやビルドの成功だけでは、すべての検査を実行したとは限りません。`NoContext`、`Partial`、`Unknown`、`Open`、古い結果には未確認の範囲が残ります。

VS Code拡張機能は、自身が所有するProblemsの診断コードに説明へのリンクを付けます。GeneratorとAnalyzerの診断はコンパイラーホストが管理し、拡張機能は複製や置き換えを行いません。この索引からも各診断の説明を参照できます。未登録のコードは通常の表示を保ちます。位置情報がない診断から行番号を推測しないでください。

## Front matter

`LSM001`〜`LSM008`は、Markdownコンテンツの読み込み時にfront matterの問題を検出します。エディターの検査では現在の文書から得られる構文の情報を扱います。コンパイル対象の入力には、Generatorの`LSG`コードが使われます。

| コード | 原因と修正箇所 |
| --- | --- |
| `LSM001` | 必須のfront matterがありません。Markdownの先頭にYAMLのヘッダーを追加します。 |
| `LSM002` | front matterの終了マーカーがありません。元のMarkdownでヘッダーを閉じます。 |
| `LSM003` | ヘッダーが空です。必要なフィールドを記入します。 |
| `LSM004` | YAMLの構文や文書の構造が受け付けられません。報告されたヘッダーの記述を修正します。 |
| `LSM005` | mappingのキーが重複しています。重複したキーを削除するか、名前を直します。 |
| `LSM006` | YAMLのanchorやaliasが使われています。値を直接記述します。 |
| `LSM007` | mappingのキーがnullでない文字列になっていません。キーを修正します。 |
| `LSM008` | YAMLのルートがmappingではありません。ヘッダーの最上位を名前付きのフィールドにします。 |

ヘッダーを直しただけでは、独自のC#スキーマ、動的なルート、最終HTMLの検査は終わりません。保存後に`lithosharp build <project>`を実行し、実行時の読み込みを確認してください。ソースの限定的な検査には`lithosharp preflight --mode static --input <file>`を明示的に実行できます。

詳しくは[front matterの区切り](typed-markdown-collections.md#front-matter-delimiters)と[preflightの段階と検査範囲](../src/LithoSharp.Tool/README.md#preflight)を参照してください。いずれも英語の説明です。

## Front matter binding

`LSC101`〜`LSC104`は、読み込み時にfront matterをスキーマへ割り当てる際の問題です。エディターでは、現在のproject contextが得られる場合に、対応する組み込みスキーマを検査できます。入力のたびに独自のconstructor、setter、validatorを実行することはありません。

| コード | 原因と修正箇所 |
| --- | --- |
| `LSC101` | スキーマにないフィールドです。YAML名を修正するか、意図した変更であればC#スキーマを更新します。 |
| `LSC102` | nullを許可しないフィールドにnullが渡されています。値を設定するか、意図した変更であればスキーマのnull許容を変更します。 |
| `LSC103` | 宣言した型へ値を割り当てられません。C#のメンバーに合わせてYAMLの値や構造を直します。 |
| `LSC104` | 必須のフィールドがありません。スキーマのYAML名を使ってヘッダーに追加します。 |

スキーマを変更する前に、aliasとsnake_caseの名前を確認してください。生成されたbinderではなく、元のコンテンツやC#モデルを直します。その後、`lithosharp build <project>`で確認します。`dotnet build <project>`が検査するのは、コンパイラーへ明示的に登録した入力です。どちらの成功も動的な参照や最終HTMLの正しさを保証しません。

[割り当ての規則](typed-markdown-collections.md#yaml-rules)と[エディターと実行時の検査範囲](typed-markdown-collections.md#diagnostics-and-failure-behavior)に英語の詳細があります。

## LSM013

Markdownの解析に必要な情報を確定できませんでした。不正な入力や、パーサーの資源上限への到達などが原因です。コンテンツの読み込みや本文のコンパイルではErrorとなり、inspectionでも報告されることがあります。実際の理由は診断メッセージを確認してください。front matterの境界が不明なことを、ヘッダーの欠如と読み替えないでください。入力が上限を超えている場合は、対象のソースや取り込むコードを修正します。

取得できなかった構文の情報、それに依存するスキーマの検査、最終出力には未確認の範囲が残ります。修正後に同じ検査か`lithosharp build <project>`を実行してください。言語サーバーの再起動では入力の上限は解消しません。

[本文解析と上限](typed-markdown-collections.md#code-inclusions-and-body-analysis)は英語、[既知の制約](known-limitations.ja.md)は日本語で確認できます。

## LSG005

Source Generatorが、登録された`AdditionalFiles`でYAMLの不正や未知のフィールドをコンパイル時に検出しました。元のヘッダーの構造、キー、スキーマに対するフィールド名を直してください。生成物は編集しません。

修正後に`dotnet build <project>`を実行します。対象は宣言されたコンパイラー入力です。動的に読み込むコンテンツ、constructorが設定する既定値、最終HTMLはこの結果では確定しません。[Generatorの診断仕様](source-generators.md#diagnostics-and-dynamic-content--診断と動的コンテンツ)は日英併記です。

## LSG006

Source Generatorがfront matterの型と互換性のない値を検出しました。必須値の欠如、許可されないnull、値の構造の不一致も含みます。登録された`AdditionalFiles`をコンパイル時に検査します。C#モデルに合わせて元のYAMLを直すか、意図した変更であればモデルを変更してください。生成されたbinderや参照は修正しません。

`dotnet build <project>`で再確認します。コンパイルが成功しても、動的に発見する入力、任意の実行時の既定値やvalidator、動的なルート、最終HTMLの検査は終わりません。必要に応じて`lithosharp build <project>`と生成物の検査へ進んでください。[Generatorの診断仕様](source-generators.md#diagnostics-and-dynamic-content--診断と動的コンテンツ)は日英併記です。

## LSA1402

Analyzerが型付きの`ContentRef<T>`について、既知のentry IDと公開ルートの矛盾を証明しました。同じカタログに属する、最新のClosedな生成カタログを使ったコンパイル時の検査です。元のルート引数を主な位置として報告し、IDの位置も関連情報として示します。生成された参照を使うか、手書きのIDとルートの組み合わせを直してください。宣言が誤っていた場合は、その入力メタデータを修正します。

修正後に`dotnet build <project>`を実行します。未知のID、動的ルート、Openや古いカタログ、対応外の参照形式はこの証明の対象外です。この診断がないことは、それらや最終出力の正しさを保証しません。[Analyzerの証明条件](../src/LithoSharp.Analyzers/README.md#lsa1402)は英語です。

## LSA9001

明示的に有効にした`RequireStaticCoverage`のコンパイル時ポリシーで、要求された範囲を証明できませんでした。診断のscope、state、reasonを確認してください。Open、Stale、Unavailable、Unknown、Deferredは、リンク切れや動的なproducerの存在を意味しません。

要求したC#の呼び出しやproducerの選択と、登録したコンパイラー入力を見直します。古い入力を更新し、可能であれば対応する静的な形式へ直して、`dotnet build <project>`を再実行してください。実行時の`lithosharp build <project>`や生成物の検査は別の段階です。それらが成功しても、静的な結果がCompleteへ変わるわけではありません。このポリシーはサイト全体の公開、anchor、assetを保証しません。[有効化と対応範囲](../src/LithoSharp.Analyzers/README.md#lsa9001)は英語です。

## LSQ001

生成物の検査で、不正なURL、対応しないURL、値が必要な箇所の空URL、存在しない内部の参照先を検出しました。Qualityを有効にした生成時か、明示的な出力検査で報告されます。URLを出力したコンテンツのリンク、レイアウト、assetの登録、ルートの宣言を直して再生成してください。生成後に出力ファイルを編集した場合は、その変更も確認します。

`lithosharp check <project>`は一時サイトを生成して検査します。既存のファイルには`lithosharp check-output --output <directory> --base-url <url>`を使います。通常のBuild結果だけではQualityを実施したとは限りません。既存の出力を検査してもソースの新しさは証明できず、外部HTTP検査は明示的な有効化が必要です。[Qualityの規則](site-quality.md#english)は日英併記、[出力検査の範囲](generated-output-checks.ja.md)は日本語で確認できます。

## LSQ002

生成物の検査で、参照先のページに存在しないanchorを指すfragmentを検出しました。検出する段階は`LSQ001`と同じです。元のリンクのfragmentか、参照先の見出しやレイアウトが出力するHTMLのIDを修正し、関係するページを再生成してください。C#の参照が正しくても、最終HTMLにanchorが存在するとは限りません。

`lithosharp check <project>`か、既存のファイルを対象にする`lithosharp check-output --output <directory> --base-url <url>`を実行します。結果が対象にするのは検査したHTMLです。未ビルドのソース変更、ブラウザー内で動的に生成する内容、検査していない外部ページは含みません。[Qualityの規則](site-quality.md#english)と[出力検査の範囲](generated-output-checks.ja.md)を参照してください。
