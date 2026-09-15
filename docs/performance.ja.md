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
