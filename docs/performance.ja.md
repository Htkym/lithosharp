# 性能

[English](performance.md)

1.0.0候補の測定、2026-09-08に行った0.3.0の測定、2026-09-13の競合比較を分けて掲載しています。
各結果は記載した入力と環境での値であり、サービス水準を保証するものではありません。

## 測定対象と環境

Markdown harnessは生成時間、managed allocation、常駐メモリを測定します。MDX harnessは
workerのcompile、render、bundleも記録します。ブラウザーでは同じfixtureのpage hydrationと
selective hydrationを比較します。非公開の測定生データはrepositoryやpackageに含めません。

環境はWindows build 26200 x64、Intel Core Ultra 7 258V、8 logical processors、32 GB RAM、
.NET SDK 10.0.300/runtime 10.0.8、Node.js 24.13.0、Chromium 153.0.8010.12です。
worker lockfileはMDX 3.1.1、React 19.2.4、esbuild 0.25.12を固定しています。

## 1.0.0のMarkdown生成

2026-09-15に、`5cc8581`の最終コンパイラーソースを上記のWindows・.NET環境で測定しました。
100ページと1,000ページは独立した5プロセス、10,000ページは2プロセスの中央値です。
MBは10進表記です。割当量はmanaged memoryが対象であり、常駐メモリの最大値ではありません。

| ページ数 | 全件生成 | メモリ割当量 | 本文1件変更 |
| --- | ---: | ---: | ---: |
| 100 | 426.14 ms | 40.67 MB | 211.61 ms |
| 1,000 | 5,083.53 ms | 359.58 MB | 3,410.56 ms |
| 10,000 | 71,195.96 ms | 3,564.19 MB | 75,680.04 ms |

変更のないビルドでは、無効化された文書は0件でした。初期の測定にはリリース基準を超えた実行もあり、
時間の変動が大きかったため、最終測定は他のローカル検証と重ならない状態で実施しました。
10,000ページの測定回数は少なく、所要時間を保証する結果ではありません。
以下の大規模MDXやブラウザーの測定をやり直した結果でもありません。

## 過去のMarkdown互換性測定

100ページと1,000ページで、基準版と0.3.0を交互に5回測定しました。時間、割当量、常駐メモリの
増加はいずれも中央値で10%以内でした。単発では時間差が10%を超えるため、すべての実行で性能回帰が
ないとは主張しません。これはMarkdown benchmarkの基準版との比較であり、公開NuGet 0.2.0の
upgrade consumerを使った時間比較ではありません。

## MDX生成と大規模サイト

2026-09-08に測定した0.3.0の結果です。最終1.0.0候補での全ケースの再測定は行っていません。

| 10,000ページcorpus | 経過時間 |
| --- | ---: |
| Cold | 274.30秒 |
| No-op | 105.13秒 |
| 本文1件変更 | 213.39秒 |

本文1件変更ではcompileとrenderが各1件でも、interactive entryを2,000件bundleしました。
10ケースを通したprocess treeの同時working-set観測最大値は11.807 GiBです。
単一cold buildのpeakでも、最低限必要なRAMでもありません。managed allocationにはNodeのメモリを
含まず、worker heap snapshotはpeak RSSではありません。worker timeoutは既定2分から15分へ
延長しました。増分compileができても、生成全体が即座に終わるとは限りません。

## Selective Hydration

2026-09-08に0.3.0で行ったブラウザー測定の結果です。

静的ページfixtureをmodeごとに5回、順序を交互にして測定しました。各回は新しいbrowser contextを
使い、service workerを無効化しています。viewportは1280 × 720で、遅延起動前のnetwork idle時点です。

| 指標 | Page Hydration | Selective Hydration |
| --- | ---: | ---: |
| JS bytes | 449,585 | 15,817 |
| gzip相当bytes | 95,085 | 4,998 |
| Hydration root | 1 | 0 |
| Hydration | 12.0 ms | 0 ms |
| Script中央値 | 17.679 ms | 3.089 ms |

この削減は測定した静的fixtureの結果です。island fixtureではscript中央値が21.514 → 15.976 ms、
hydration時間の合計が13.2 → 18.2 msでした。hydration区間は重複する場合があり、dynamic importの
待ち時間を含みません。fallback fixtureのJSは451,676 → 452,377 bytesで、静的ページと同じ削減はありません。
640 × 600から1200 × 800へ変更して全遅延islandを起動した場合、scriptは16.101 → 18.056 ms、
hydrationは13.1 → 14.6 ms、CLSは両modeとも0.157でした。初期viewportと起動後のlayoutは別条件です。

## 競合比較

2026-09-13に同一マシンと固定corpusで、製品buildの比較を行った。対象は
Docusaurus 3.10.2とReact 19.2.4、Astro 7.3.2とStarlight 0.42.0、Hugo 0.166.0
extended、MkDocs 1.6.1とMaterial 9.7.7であり、Plain MarkdownとDocumentationと
MDX・Interactiveのprofileで測った。下表はprofile別のbuild時間の中央値である。
検索、highlight、navigation、JS機能の差があり、非対応の組合せは0msにしない。

| 1,000 pages | LithoSharp | Hugo | MkDocs | Astro | Docusaurus |
| --- | ---: | ---: | ---: | ---: | ---: |
| Plain | 6.7 s | 2.8 s | 21.3 s | 6.6 s | 34.6 s (blog) |
| Docs | 12.4 s | 0.76 s | 23.9 s | 14.3 s (Starlight) | 23.9 s |

10,000 pagesではHugoとAstroが完走し、Docusaurusはメモリ不足、MkDocsは10分制限を
超えた。HugoとMkDocsにMDX対応はない。raw結果はローカルに置き、手順は
リポジトリの harness から再現できる。Windowsの結果からLinux/macOSのthroughputは
推定できない。

## この測定からは言えないこと

上の10,000-page MDXの数値に同等の競合測定はない。Windowsの結果からLinux/macOSのthroughputは推定できません。
fixtureの削減は、全MDXページがzero-JSになること、全islandが速くなること、任意のnpm依存が動くことを
示しません。実行とplatformの境界は[既知の制約](known-limitations.ja.md)を参照してください。

## 1.1.0候補：Editor latency（測定済み）

Editor測定ハーネスで2026-09-28に測定した（実Release言語サーバー×実`LspClient`、
Windows x64、8 CPU、Node.js 24.13.0）。warm分は除外する。50 KiB代表文書に
100編集ずつ、50 ms debounce込みend-to-endの値である。

| 文書 | burst p50/p95/max | spaced p50/p95/max |
| --- | ---: | ---: |
| Markdown | 94/96/97 ms | 94/96/116 ms |
| MDX | 94/97/101 ms | 94/96/125 ms |

解析単独は約46 ms（end-to-endからdebounceを除いた相当）である。当時の検証閾値は
解析Markdown p95≤250 ms・MDX p95≤750 ms、体感Markdown p95≤500 ms・
MDX p95≤1500 msであり、このmachineでは4つとも適合する。他machineは未測定である。

二root 1,000編集＋途中server kill→有界再起動のsoakは両rootの最新版数へ収束し、
所有process残留はなかった。このsoakは高速編集であり、30分wall-clock soakではない。

## 1.1.0の性能上の制約

生成速度の目標は未達である。2026-10-06に、Windows x64、SDK 10.0.401、runtime 10.0.12で
Markdown 1,000ページをReleaseで比較した1組では、本文1件の編集に基準版1,416.16 ms、
採用したbuffer設定を使う開発候補2,521.28 msを要した。比率は1.780で、目標0.70以下を満たさない。
1組の結果であり、中央値、全性能matrix、所要時間の保証ではない。Markdown 10,000ページや
MDX 1,000ページの結果は証明せず、以前の表も記載した範囲のままである。

本文変更でも、変更のない生成物と無関係なファイルを含む既存出力全体を、独立したstagingへ
コピーしてから変更分を置き換える。出力の照合とディレクトリ切替・復旧の規則は維持する。
この処理は小さな編集の時間でも大きな割合を占め得る。目標未達は1.1.0の既知の制約として記録し、
出力設計の大きな変更は次版以降へ回す。生成の高速化や性能目標の合格は主張しない。

1.1.0ではWatchとEditorの長時間安定性検証は実施していない。高速編集と復旧の確認は、30分の
連続稼働を証明しない。複数OSのCIとpackage済み拡張の確認は機能検証で、性能測定とは区別する。

## 再現

Release buildと明示的なworker restoreを先に行います。SDK、lockfile、時刻、corpusを揃え、
測定中は別のbuildやbrowser負荷をかけないでください。

```sh
dotnet restore LithoSharp.slnx --locked-mode
dotnet build LithoSharp.slnx -c Release --no-restore
dotnet run --project benchmarks/LithoSharp.Performance -c Release --no-build -- --size 100 --smoke --output .local/performance-smoke.json
```

実行コードは[Markdown](../benchmarks/LithoSharp.Performance)、[MDX](../benchmarks/LithoSharp.MdxPerformance)、
[process-tree計測](../eng/Measure-ProcessTree.ps1)、[browser計測](../tests/fixtures/mdx-browser/measure.mjs)にあります。
browser harnessにはpage版とselective版のoriginと出力fileを指定し、5回ずつ反復します。
`--activate`は遅延起動後の測定用です。測定出力はローカルに保持してください。
