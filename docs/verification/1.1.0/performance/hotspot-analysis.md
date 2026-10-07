# V110-04 hotspot analysis — 1頁変更が遅い理由の分解

対象：2026-09-24、LithoSharp B0（公開1.0.0, `11f7e74`）と B1r（`feature/1.1.0` の V110-02 修復済み作業ツリー）
生データ：`.local/verification/1.1.0/v110-04-20260923/benchmark-baseline.json`（統合）、
`v110-04-20260923/metrics/`（engine・micro-costs）、各 `v110-04*/ledger.jsonl` と `runs/*/result.json`
コンパクト版：`docs/verification/1.1.0/performance/baseline-20260924.json`

## 方法

- Markdown harness（`benchmarks/LithoSharp.Performance`）に `--scenario clean|no-op|single-page-change|layout-change` を追加し、指定時は成功baseline buildを`prepare`として別計測する。
- MDX harness（`benchmarks/LithoSharp.MdxPerformance`）に `--scenario`（10件）を追加し、同様にcold buildを`prepare`として分離する。
- `eng/Measure-IncrementalScenarios.ps1` が scenario×size を実行枠とし、run専用のcorpus/cache/output、timeout時のprocess tree回収、resume、raw保持を行う。
- 支配要因は (1) scenario間の差分、(2) harnessのカウンタ（cache hit/miss、invalidated nodes、`MdxMetrics`のworker/bundle/render内訳）、(3) 製品外で測ったmicro-cost（列挙・SHA-256・read・copy）、(4) engine micro-benchmark（文書あたりparse/render/semantics）で分解した。製品内にphase計測点は無いため、差分による帰属である（仮定は各節に明記）。

## 測定条件とドリフト事故

- 機械：HP OmniBook X Flip、Core Ultra 7 258V（8 core）、31.6 GiB、Windows 11 26200、AC電源・高パフォーマンス計画、SDK 10.0.300、Node 24.13.0。他作業とは並行していない。
- process-coldのみ保証し、OS page cacheは統制していない（process-coldと記録）。
- **2026-09-23 23:00〜2026-09-24 12:00 の間に機械が 1.5〜8倍遅くなる劣化が発生した**。同コードの B0 が同日14時台は clean 110s、翌朝は 289〜868s と変動し、隣接run間でも矛盾する値（10,000頁 B0 の no-op 341s > 本文1件 248s）が出た。`.local` の再生成可能ツリー39万ファイルを削除しても回復しなかったため、原因は未特定（サーマル／OS側の持続要因を疑う）。この時間帯の値は raw として保持し、A/Bの主張には使わない。

## Markdown の結果（B0、安定していた5 run、中央値 ms）

| size | clean | no-op | 本文1件 | layout | 本文1件−no-op | clean−no-op |
| --- | --- | --- | --- | --- | --- | --- |
| 1,000頁 | 6,718 | 5,777 | 2,106 | 4,025 | −3,671 | 941 |
| 10,000頁 | 110,613 | 79,718 | 83,438 | 162,472 | 3,720 | 30,895 |

- 1,000頁の本文1件が no-op より速いのは、同一プロセスで no-op の後に走るため（JIT・page cache・staging状態）。**順序効果**であり、scenario間の差をそのまま編集コストと読んではいけない。独立プロセスの実行では B1r 1,000頁で no-op 7.5s → 本文1件 3.1s と、こちらも同じ順序効果の向きを示す（prepare直後の1回目が最も重い）。
- 10,000頁では順序効果より規模の効果が支配的で、**本文1件 83.4s のうち 79.7s（95.6%）が編集に依存しない固定費**である。編集した1頁の追加コストは 3.7s（本文1件−no-op）。
- 全corpusの parse/render/派生物は clean−no-op = 30.9s。layout（全頁再render、再parseなし）は no-op に対し 82.8s 増（1頁あたり約8.3ms）。
- 参考（B1r 1,000頁、5 run、9/24計測）：clean 9,550 / no-op 7,470 / 本文1件 3,084 / layout 4,999。同日のB0同時刻データが無く、環境劣化の影響を含むため B0 との比は主張しない。

## MDX の結果（B1r 1,000頁、各1 run、9/24計測）

| scenario | elapsed | prepare | worker | server bundle | browser bundle | render | compiled | rendered |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| cold | 183,327 | — | 15,869 | 6,505 | 814 | 2,760 | 1,000 | 1,000 |
| no-op | 17,756 | 108,140 | 0 | 0 | 0 | 0 | 0 | 0 |
| 本文1件 | 38,195 | 72,326 | （1 module分） | | | | 1 | 1 |

- cold のMDX帰属分（worker＋bundle＋render）は約25.9sで、183.3sの14%。残り86%は .NET 側の入力・計画・cache・成果物・stagingで、Markdownと同じ固定費構造である。
- Node workerのcompileは約16ms/module（1,000 modulesで15.9s）、bundleは6.5s/1,000頁。
- 本文1件（38.2s − no-op 17.8s = +20.4s）のうち、1 moduleの再compile＋bundleは数秒で、残りは頁・成果物パイプラインと固定費である。
- B0 側のMDXは未計測（下記BLOCKED）。

## 固定費の中身（micro-costとの比較、B0 1,000頁の corpus/site で測定）

| 操作 | 中央値 | 備考 |
| --- | --- | --- |
| corpus 1,000ファイルの列挙 | 47ms | 入力発見の下限 |
| 出力1,012ファイルの列挙 | 41ms | 出力検証の下限 |
| corpus全byteのSHA-256（273 KB） | 113ms | fingerprintの下限 |
| corpus全byteのread | 63ms | 入力readの下限 |
| 出力treeのcopy（1,012ファイル・6.05 MB） | 2,254ms | staging/copyの下限（単純copy） |
| 合計 | 約2.5s | no-op 7.5s の約33% |

製品外の測定なので桁の参考値に留まるが、**固定費の3分の2は素朴なファイル操作では説明できない**。cache読み込み・fingerprint生成・manifest・所有権検査・attribute/ACL処理・検索index・検証といった製品側のper-file処理が候補になる。

## engine（文書あたり、10種のcorpus形状、1,000頁規模）

parse-only 0.04〜0.83ms、render-only 0.01〜0.65ms、parse+render 0.06〜1.21ms、analyze 0.10〜1.82ms。
1,000頁換算で parse+render は約0.1〜1.2s。一方 clean−no-op は 0.9s（1,000頁）〜30.9s（10,000頁）で、**compilerそのものは頁あたりコストの主因ではない**。site側のper-page処理（page model、検索、成果物生成、staging、検証）が支配する。

## 改善見込み（実測値からの算術射影。達成予測ではない）

- 10,000頁・本文1件 83.4s のうち固定費 79.7s。固定費を仮に30%削減できれば約59.5s、50%なら約43.6s。計画書の必須改善目標（B0比0.70以下＝58.4s以下）は、頁あたりコストを変えずに達成するには固定費を約27%以上削減する必要がある。
- 1,000頁・本文1件 2.1s（順序効果込み）は既に小さく、10,000頁の比率目標と同列に扱わない。
- layoutは no-op＋82.8sで、全頁再renderとしては clean のcorpus仕事（30.9s）より大きい。テーマ変更が検索・派生物・stagingまで波及している可能性があり、V110-06の対象とする。
- MDXのcold 183.3sは、worker帰属25.9sを除いた .NET 側の削減余地が大きい。V110-07はbundle対象の絞り込みとworker payload削減を担う。

## BLOCKED・未解決

- **Markdown 10,000頁のB1r**: 環境劣化のためA/B不能（rawは保持）。
- **MDX 1,000頁のB0**: 未計測（サーバ再起動でrunが中断）。MDX 10,000頁も未計測。計画書の「10,000頁MDXの未計測解消」は実行手順（scenario選択・prepare分離・resume）としては整備済みで、数値は環境が安定した時点で再取得する。
- 製品内のphase計測点は無い。V110-05/06で差分で説明できない固定費を追う際は、明示的なcounter（入力走査・hash・cache読込・stagingの件数と時間）を加算的に追加する。
- 1,000頁の順序効果（no-op > 本文1件）は、独立プロセス計測でも prepare 直後の1回目が重い形で現れる。V110-05は「同一process内のwarm条件」と「新processのcache-hit」を分けて比較する必要がある。
