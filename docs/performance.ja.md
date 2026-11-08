# 性能

[English](performance.md)

最新の参照測定は2026年11月8日01:36:57〜01:39:26 UTCに実施した。正式版2.1.3と、2.2.0開発commit `bedc85cc63a5c1ef955e6fa34d1ce3118a983b90`を比較し、新しい型付きDocs＋Qualityは単独のbaselineとして測定した。1台のPCで100ページを生成したsource buildの観測であり、公開済みの2.2.0パッケージの測定、リリース合格の判定、速度の保証ではない。高速化は主張しない。

[raw値、集計値、ソースと実行バイナリのhash、測定条件](evidence/performance/2026-11-08-committed-bedc85c-reference.json)を、この文書とともに保存している。同じ製品バージョンのまま後続の変更が入っても、測定対象を区別できる。完全なログや生成サイトはローカルに残す。[harnessガイド](../benchmarks/LithoSharp.Performance/README.md)に、再現手順と区間の説明がある。

## 対象と条件

- baselineは正式版2.1.3の`25d92d4605533260b22abffd2618971dd6f60c8a`。製品ソースを変更せず、共通の計測コードだけを重ねた独立した作業ツリーでビルドした。
- candidateは2.2.0のcommit `bedc85cc63a5c1ef955e6fa34d1ce3118a983b90`。測定時の作業ツリーはcleanで、参照記録には製品ソースと実際にロードしたバイナリのhashを残した。文書と集計コードの後続編集は測定後に行った。
- Windows 10.0.26300 x64、Intel Core Ultra 7 258V、論理CPU 8、SDK 10.0.403、runtime 10.0.14。MDXとeditorはNode.js 24.13.0と、記録したworker lockfileを使った。
- Coreが実際に使ったSyntamarkのruntimeは1.2.0。parser fingerprintはGeneratorに埋め込まれたSourceの実測値と一致した。値は`1/17494090defc826957640640ddcdbca4268a9a1011cfe24a0e534247910a7faa`、profileは`lithosharp-markdown/1`。runtime assemblyとSource hostを別々に記録し、パッケージ宣言値では代用していない。
- サイトの規模は100ページ、固定のbuild timestampは`2026-01-01T00:00:00Z`。製品とharnessを事前にReleaseビルドし、ビルドやテストを並列実行せずに測定した。計測中はソースもビルド出力も変更していない。新しいprocessで起動しても、OSのファイルキャッシュが空とは限らない。corpus作成、restore、compileは計測区間外。

## Blogの継続比較

従来のBlog生成区間を維持した。原稿読み込みは区間外で、Qualityの明示設定はない。両版で同じBlog harnessソースと設定を使い、独立したprocessを各3回実行した。対象の実行順を交互に変え、各processでは初回、無変更、本文変更、レイアウト変更の順に測定した。

単位はms。中央値と、括弧内に観測した最小値〜最大値を示す。

| 区間 | 正式版2.1.3 | 2.2.0 `bedc85c` |
| --- | ---: | ---: |
| 初回生成 | 474.54（473.99〜528.32） | 480.49（475.41〜495.29） |
| 無変更 | 68.64（62.18〜74.35） | 61.17（60.51〜61.84） |
| 本文1件変更 | 202.13（196.48〜239.01） | 187.48（186.94〜208.07） |
| レイアウト変更 | 227.72（226.60〜261.12） | 244.00（210.99〜256.07） |

初回生成とレイアウト変更はcandidateの中央値が高く、無変更と本文変更は低かった。無変更以外の範囲は重なり、測定回数も少ないため、安定した性能改善を確認したとは言えない。レイアウト変更後の入力と出力のbytesは、内部の出力manifestだけを除き、6回すべてで一致した。記録した入出力のhashは最後の状態を表し、各区間直後のsnapshotではない。

## 型付きDocs＋Qualityの単独baseline

candidate専用の経路で`MarkdownContentCollectionLoader`、`RenderDocs`、共通のDocs表示部品を使い、Qualityを明示的に有効にした。失敗閾値はError、孤立ページ検査は有効、外部HTTP検査は無効。実行時に作る固定corpusを組み込みのreflection binderで読み込む。生成Binder、テンプレートやC#のcompileは別の工程であり、ここには含まれない。

100ページを新しいprocessで3回測定した。

| 区間 | 生成の中央値ms（範囲） | 読み込み＋生成の中央値ms |
| --- | ---: | ---: |
| 初回生成 | 1,065.33（1,062.74〜1,136.42） | 1,229.03 |
| 無変更 | 406.79（395.88〜542.11） | 448.59 |
| 本文1件変更 | 536.90（509.35〜571.00） | 576.48 |
| レイアウト変更 | 1,026.94（764.62〜1,034.52） | 1,067.03 |

生成時間には入力の読み込みを含めない。別項目の読み込み＋生成時間、allocation、working setには読み込みと生成の両方を含める。無変更ではcache可能なnodeをすべて再利用したが、有効にした出力Qualityは実行した。型付きDocsのadapterは2.1.3には存在しないため、Blogやlegacy Docsなど異なる経路との版間比率は出していない。3回の最終出力bytesは一致した。

## MDX、Generator、editor

MDXは既存の静的80％、page hydration 10％、load island 10％のcorpusと、10区間の測定順序を使った。各版1processで10区間すべてが成功し、下表以外の区間もraw値に残している。baselineの初回ではplan区間が8,035msを占めた。1組だけなので、改善や悪化を確定する測定ではない。

| MDX区間 | 2.1.3の秒数 | 2.2.0 `bedc85c`の秒数 |
| --- | ---: | ---: |
| 初回生成 | 8.68 | 6.87 |
| 無変更 | 2.75 | 2.71 |
| 本文1件変更 | 5.63 | 5.30 |
| レイアウト変更 | 2.98 | 3.22 |

Generatorは実際の埋め込みSourceの`ParseForCompiler`によるheader scanを測った。reflection呼び出しのコストも含む。本文4,096文字の入力100件について、各版3processを使い、各processでwarmup 1回を除いて5回測定した。processごとの中央値をさらに集計した中央値は、2.1.3が0.6487ms、candidateが0.6390msだった。スキーマのbind、コード出力、Roslyn、C# compileは含まず、Generator全体の高速化は示していない。

candidateのeditorは、実際の`LspClient`とRelease language serverを使った。文書は50KiB。表の4条件（Markdown／MDXとBurst／Spacedの組み合わせ）を別々のLSP processで測り、各条件でwarmup 3回を除外して20編集ずつ、計80編集を記録した。50msのdebounce、transport、schedulingを含む値であり、parser単独の時間ではない。Burstは追加の待ち時間を入れない逐次編集、Spacedは100msの間隔を追加した逐次編集を表す。

| 文書 | Burstのp50／p95／最大ms | Spacedのp50／p95／最大ms |
| --- | ---: | ---: |
| Markdown | 64.42／81.72／84.68 | 67.49／78.97／81.05 |
| MDX | 63.27／76.58／79.72 | 74.00／77.25／80.64 |

この表とは別に、1つのLSP clientで2つのroot URIの20KiB文書を交互に20回ずつ、計40回編集する短い継続試験を行い、途中で1回再起動した。この試験では再起動前後の2つのLSP processを使った。表の4プロセスと合わせ、harnessが所有する6つのLSP processはすべてexit code 0で終了し、harnessもexit code 0で正常終了した。外部からの停止は不要だった。停止前のworking-set snapshotは55,533,568〜65,732,608bytesで、peakでもprocess treeの合計でもない。harness自身のworking setは別に41,783,296→45,559,808bytesだった。worker NodeのRSSは未取得。editorのprotocolからはロード済みparser／Sourceの識別情報を取得できないため、その欄は不明のまま残し、サイト測定の値を転用していない。代わりにserver／clientバイナリとclientソースのhashを記録した。

## 解釈と制約

段階別時間には既存の`SiteBuildTimings`を使う。verificationのworker区間はexecutionと重なり、合計が全体時間を上回ることもある。各値を足して総時間にはしない。サイトのworking setは10ms間隔の標本で、process実行期間全体のpeakではない。managed allocationはNodeを含まず、workerのheap snapshotもNodeのRSSではない。

[以前の未commit snapshotの参照記録](evidence/performance/2026-11-07-typed-docs-reference.json)は別の測定として保存している。測定途中にソースが変わったBlog campaignと、スキーマ外のfront matterを含んだDocs fixtureの除外理由は、この旧記録に属する。editorのclient timerが残ってharnessホストを別途停止した制約も旧測定固有であり、今回の正常終了とは区別する。今回の絶対値が旧測定より小さいことを高速化の根拠にはせず、両方の標本を混ぜて集計しない。未取得、未対応、失敗、timeoutはゼロと区別する。

1,000／10,000ページ、他OS、長時間のwatch／editor soak、Extension Hostのコスト、競合との再比較は今回の対象外。CIの軽量な比較は任意実行のままで、既定baselineを正式版2.1.3に固定した。14日間のCI artifactだけを、この参照値の根拠にはしていない。

## 再現

Releaseビルド、profileの選択、設定ファイルを使うmatrix、任意のCI比較は[harnessガイド](../benchmarks/LithoSharp.Performance/README.md)を参照。通常の実行記録は`.local/`に残し、レビューした参照値だけを公開用のevidenceディレクトリへ保存する。

## 過去の測定

0.3.0／1.0.0／1.1.0の表、未達の増分性能目標、当時の制約は[性能測定の履歴](history/performance-through-2.1.3.ja.md)へ移した。現行版の結果としては扱わない。

<a id="100のmarkdown生成"></a>
<a id="過去のmarkdown互換性測定"></a>
<a id="mdx生成と大規模サイト"></a>
<a id="selective-hydration"></a>
<a id="競合比較"></a>
<a id="110候補editor-latency測定済み"></a>
<a id="110の性能上の制約"></a>
<a id="現行版と測定の範囲"></a>
<a id="測定対象と環境"></a>
<a id="この測定からは言えないこと"></a>

旧sectionのリンク先はここに残している。該当する表は履歴を参照。
