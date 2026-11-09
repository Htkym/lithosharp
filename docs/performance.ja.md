# 性能

[English](performance.md)

最新の参照測定は2026年11月7日に実施した。正式版2.1.3と、未commitの2.2.0開発snapshotを比較し、新しい型付きDocs＋Qualityは単独のbaselineとして測定した。公開済みの2.2.0パッケージを対象にした測定ではなく、リリース合格を示すものでも速度の保証でもない。後続のナビゲーションcacheと正当性の修正は、このsnapshotに含まれない。高速化は主張しない。

[raw値、集計値、ソースと実行バイナリのhash、測定条件](evidence/performance/2026-11-07-typed-docs-reference.json)を、この文書とともに保存している。同じ製品バージョンのまま後続の変更が入っても、測定対象を区別できる。完全なログや生成サイトはローカルに残す。[harnessガイド](../benchmarks/LithoSharp.Performance/README.md)に、再現手順と区間の説明がある。

## 対象と条件

- baselineは正式版2.1.3の`25d92d4605533260b22abffd2618971dd6f60c8a`。製品ソースを変更せず、共通の計測コードだけを重ねた独立した作業ツリーでビルドした。
- candidateは同commitを基にした2.2.0のsource build。参照記録にはリポジトリ相対pathごとの製品ソースhashと、実際にロードしたバイナリのhashが含まれる。基準commitだけでは未commitのsnapshotを識別できない。
- Windows 10.0.26300 x64、Intel Core Ultra 7 258V、論理CPU 8、SDK 10.0.403、runtime 10.0.14。MDXとeditorはNode.js 24.13.0と、記録したworker lockfileを使った。
- Coreが実際に使ったSyntamarkのruntimeは1.2.0。parser fingerprintはGeneratorに埋め込まれたSourceの実測値と一致した。値は`1/17494090defc826957640640ddcdbca4268a9a1011cfe24a0e534247910a7faa`、profileは`lithosharp-markdown/1`。runtime assemblyとSource hostを別々に記録し、パッケージ宣言値では代用していない。
- サイトの規模は100ページ、固定のbuild timestampは`2026-01-01T00:00:00Z`。ビルドを同時実行せず、順番に実行した。新しいprocessで起動しても、OSのファイルキャッシュが空とは限らない。corpus作成、restore、compileは計測区間外。

## Blogの継続比較

従来のBlog生成区間を維持した。原稿読み込みは区間外で、Qualityの明示設定はない。両版で同じBlog harnessソースと設定を使い、独立したprocessを各3回実行した。対象の実行順を交互に変え、各processでは初回、無変更、本文変更、レイアウト変更の順に測定した。

単位はms。中央値と、括弧内に観測した最小値〜最大値を示す。

| 区間 | 正式版2.1.3 | 2.2.0 snapshot |
| --- | ---: | ---: |
| 初回生成 | 1,869.63（1,584.33〜1,902.90） | 1,840.49（1,623.12〜3,183.85） |
| 無変更 | 169.91（161.51〜194.21） | 171.72（143.53〜182.80） |
| 本文1件変更 | 848.01（797.38〜973.90） | 920.58（822.84〜1,416.21） |
| レイアウト変更 | 832.57（745.13〜942.26） | 1,006.84（727.91〜1,331.50） |

本文変更とレイアウト変更はcandidateの中央値が高かった。測定回数が少なく、範囲も重なっているため、安定した差や性能改善を確認したとは言えない。レイアウト変更後の入力と出力のbytesは、内部の出力manifestだけを除き、6回すべてで一致した。記録した入出力のhashは最後の状態を表し、各区間直後のsnapshotではない。

## 型付きDocs＋Qualityの単独baseline

candidate専用の経路で`MarkdownContentCollectionLoader`、`RenderDocs`、共通のDocs表示部品を使い、Qualityを明示的に有効にした。失敗閾値はError、孤立ページ検査は有効、外部HTTP検査は無効。実行時に作る固定corpusを組み込みのreflection binderで読み込む。生成Binder、テンプレートやC#のcompileは別の工程であり、ここには含まれない。

100ページを、新しいprocessで3回測定した。

| 区間 | 生成の中央値ms（範囲） | 読み込み＋生成の中央値ms |
| --- | ---: | ---: |
| 初回生成 | 3,120.93（2,655.32〜3,389.91） | 3,520.91 |
| 無変更 | 1,206.14（889.23〜2,240.43） | 1,386.23 |
| 本文1件変更 | 1,480.47（1,272.97〜3,177.47） | 1,600.48 |
| レイアウト変更 | 3,089.29（3,041.90〜3,517.67） | 3,380.00 |

生成時間には入力の読み込みを含めない。別項目の読み込み＋生成時間、allocation、working setには読み込みと生成の両方を含める。無変更ではcache可能なnodeをすべて再利用したが、有効にした出力Qualityは実行した。型付きDocsのadapterは2.1.3には存在しないため、Blogやlegacy Docsなど異なる経路との版間比率は出していない。3回の最終出力bytesは一致した。

## MDX、Generator、editor

MDXは既存の静的80％、page hydration 10％、load island 10％のcorpusと、10区間の測定順序を使った。各版1processで、下表以外の区間もraw値に残している。1組だけなので、改善や悪化を確定する測定ではない。

| MDX区間 | 2.1.3の秒数 | 2.2.0 snapshotの秒数 |
| --- | ---: | ---: |
| 初回生成 | 23.20 | 19.37 |
| 無変更 | 12.14 | 10.43 |
| 本文1件変更 | 21.33 | 15.39 |
| レイアウト変更 | 13.80 | 17.25 |

Generatorは実際の埋め込みSourceの`ParseForCompiler`によるheader scanを測った。reflection呼び出しのコストも含む。本文4,096文字の入力100件について、各版3processを使い、各processでwarmup 1回を除いて5回測定した。processごとの中央値をさらに集計した中央値は、2.1.3が0.8157ms、snapshotが0.6947msだった。スキーマのbind、コード出力、Roslyn、C# compileは含まず、Generator全体の高速化は示していない。

candidateのeditorは、実際の`LspClient`とRelease language serverを使った。文書は50KiB。表の4条件（Markdown／MDXとBurst／Spacedの組み合わせ）を別々のLSP processで測り、各条件でwarmup 3回を除外して20編集ずつ、計80編集を記録した。50msのdebounce、transport、schedulingを含む値であり、parser単独の時間ではない。Burstは追加の待ち時間を入れない逐次編集、Spacedは100msの間隔を追加した逐次編集を表す。

| 文書 | Burstのp50／p95／最大ms | Spacedのp50／p95／最大ms |
| --- | ---: | ---: |
| Markdown | 66.37／84.59／90.28 | 67.69／80.24／92.68 |
| MDX | 74.08／83.98／97.53 | 73.44／78.03／78.37 |

この表とは別に、1つのLSP clientで2つのroot URIの20KiB文書を交互に20回ずつ、計40回編集する短い継続試験を行い、途中で1回再起動した。この試験では再起動前後の2つのLSP processを使った。表の4つと合わせ、harnessが所有する6つのLSP processはすべて終了した。停止前のworking-set snapshotは58,114,048〜66,035,712bytesで、peakでもprocess treeの合計でもない。harness自身のworking setは別に46,026,752→49,119,232bytesだった。worker NodeのRSSは未取得。editorのprotocolからはロード済みparser／Sourceの識別情報を取得できないため、その欄は不明のまま残し、サイト測定の値を転用していない。代わりにserver／clientバイナリとclientソースのhashを記録した。

## 解釈と制約

段階別時間には既存の`SiteBuildTimings`を使う。verificationのworker区間はexecutionと重なり、合計が全体時間を上回ることもある。各値を足して総時間にはしない。サイトのworking setは10ms間隔の標本で、process実行期間全体のpeakではない。managed allocationはNodeを含まず、workerのheap snapshotもNodeのRSSではない。

最初のBlog campaignは測定中にソースの文書メタデータが変わり、最初のDocs fixtureは最小スキーマ外のfront matterを含んでいたため、いずれも参照値から除外し、理由を記録している。これらを有効な参照値に読み替えることはしていない。未取得、未対応、失敗、timeoutをゼロと区別する。

editorでは全標本の保存とLSPの終了後も、clientのtimerによってharnessホストが残り、所有元を照合したうえで別途停止する必要があった。この実行上の制約は、取得済みの遅延値とは分けて記録した。後から入れた終了処理の修正を、新たな性能測定には数えていない。参照記録には測定時のharness hashを残している。

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
