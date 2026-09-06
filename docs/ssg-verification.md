# SSG enhancement verification

Measurements use Windows 10.0.26200 x64, .NET SDK 10.0.400 and runtime 10.0.8,
with eight logical processors. The starting commit is `6a73fc9`.

## Starting baseline

On 2026-09-06, locked restore, Release build (zero warnings), all 312 TUnit tests,
Docs and Blog sample generation, package compatibility validation and package
contents validation passed. The 100-page performance smoke also passed.

| Pages | Workload | Time (ms) | Allocated bytes | Peak working set (bytes) |
| --- | --- | ---: | ---: | ---: |
| 100 | clean | 364.3 | 35404728 | 62132224 |
| 100 | no-op | 309.4 | 46172992 | 65818624 |
| 100 | single-page-change | 271.8 | 45902384 | 65699840 |
| 100 | layout-change | 296.4 | 46134752 | 68382720 |
| 1000 | clean | 1600.2 | 315867616 | 98467840 |
| 1000 | no-op | 5246.8 | 423315248 | 119205888 |
| 1000 | single-page-change | 2349.0 | 423597808 | 130584576 |
| 1000 | layout-change | 2440.7 | 422009192 | 139489280 |
| 10000 | clean | 34990.0 | 3118341040 | 414687232 |
| 10000 | no-op | 58848.0 | 4197688328 | 503758848 |
| 10000 | single-page-change | 60359.5 | 4186519336 | 567590912 |
| 10000 | layout-change | 76594.5 | 4203504696 | 591269888 |

Results are written to `artifacts/ssg-baseline/baseline-{size}.json`.
Compare the `Workloads` entries for clean, no-op, single-page change and layout
change. The original top-level peak memory summary omitted the end sample;
the 1,000-page result exposes that defect. Workload peaks already include the
end sample and are the comparison source. Phase 2A corrects the summary formula.

Run each size sequentially using:

```powershell
dotnet run --project benchmarks/LithoSharp.Performance --configuration Release --no-build -- --size 100 --smoke --output artifacts/ssg-baseline/baseline-100.json
dotnet run --project benchmarks/LithoSharp.Performance --configuration Release --no-build -- --size 1000 --output artifacts/ssg-baseline/baseline-1000.json
dotnet run --project benchmarks/LithoSharp.Performance --configuration Release --no-build -- --size 10000 --output artifacts/ssg-baseline/baseline-10000.json
```

The existing untracked ownership/lock sidecars and `.playwright-cli/` are excluded
from these changes. Outputs and measurement files stay under ignored `artifacts/`.

## Phase 2A

Release build completed with zero warnings. All 320 TUnit tests passed, including
the existing compatibility fixtures and new HTML, URL, asset snapshot, dependency,
collision and pre-cancelled generation tests. Docs and Blog samples completed;
package compatibility and contents validation passed. The Docs sample intentionally
adds one downloadable source asset and links to it. Blog sample output differences
were limited to the generated timestamp in the search index and its dependent
content-hash URL; the remaining search data matched.

The 1,000-page run overlapped sample startup and measured 3477.1 ms for clean
generation. A sequential repeat in `artifacts/ssg-2a-isolated/` measured 1419.6 ms,
compared with 1600.2 ms before the change. Use that isolated repeat for comparison.
The no-asset path retains the original build-plan construction and rendering flow.
Assets are still snapshotted in memory; copy/transform skipping belongs to phase 5
and persistent page reuse to phase 6.

| Pages | Workload | Time (ms) | Time delta | Allocation delta | Peak working-set delta |
| --- | --- | ---: | ---: | ---: | ---: |
| 100 | clean | 357.4 | -1.9% | 0.0% | +1.1% |
| 100 | no-op | 318.5 | +3.0% | +0.3% | +0.9% |
| 100 | single-page-change | 274.9 | +1.1% | +0.2% | +5.2% |
| 100 | layout-change | 267.8 | -9.7% | +0.1% | +2.3% |
| 1000 | clean | 1419.6 | -11.3% | +0.1% | +2.5% |
| 1000 | no-op | 3322.5 | -36.7% | +0.1% | +1.4% |
| 1000 | single-page-change | 2439.2 | +3.8% | -0.3% | 0.0% |
| 1000 | layout-change | 2373.2 | -2.8% | +0.5% | +2.6% |
| 10000 | clean | 32534.8 | -7.0% | +0.2% | +1.0% |
| 10000 | no-op | 56024.4 | -4.8% | 0.0% | +2.8% |
| 10000 | single-page-change | 49089.0 | -18.7% | +0.1% | +2.5% |
| 10000 | layout-change | 54985.3 | -28.2% | -0.1% | +10.6% |

The initial 100-page no-op and single-change times increased by 49.2% and 16.6%.
Their sequential repeat reduced those differences to 3.0% and 1.1%; no timing
regression above 10% remained. Final comparison uses the isolated 100/1,000-page
files and `artifacts/ssg-2a/baseline-10000.json`.

The 10,000-page layout workload retained a 10.6% working-set increase despite
0.1% lower total allocations and 28.2% lower elapsed time. The no-asset path adds
only an empty registry and retains the original rendering/plan algorithm. Runtime
GC timing and OS working-set retention are the likely explanation, not established
by a heap profile. Accept this measured limit for 2A and retain the comparison for
later phases; do not claim a statistically established performance improvement or
hide the working-set increase. No unrelated memory tuning is included.

## Phase 2B

The built-in Blog and Docs renderers now share the public layout/component
implementations. Fixed-time sample generation produced byte-identical files to
phase 2A for both samples, including Unicode URLs, metadata, whitespace, SVGs,
CSS, scripts and search data. The Docs sample uses a typed layout class. The
legacy template adapter preserves environment, build timestamp and registered
assets. Standalone rendering uses the same components without an output folder.

The Blog theme-color attribute is now HTML-encoded, matching Docs and the other
attributes. This deliberately fixes unsafe custom theme text; normal sample
output is unchanged. Public component input validation runs outside the built-in
internal render cores. No rendering cache or execution skipping is claimed here.

Final Release build: zero warnings and errors. All 331 TUnit tests passed;
package API compatibility and package contents validation passed. The 100-page
performance smoke passed. The table retains the first sequential measurements
instead of selecting the fastest repeat. Comparison uses the phase 2A isolated
100/1,000-page baselines and its 10,000-page baseline.

| Pages | Workload | Time (ms) | Time delta | Allocation delta | Peak working-set delta |
| --- | --- | ---: | ---: | ---: | ---: |
| 100 | clean | 360.5 | +0.9% | +2.2% | -1.8% |
| 100 | no-op | 300.5 | -5.7% | +1.7% | +2.1% |
| 100 | single-page-change | 279.0 | +1.5% | +1.4% | -2.9% |
| 100 | layout-change | 282.8 | +5.6% | +1.3% | -2.6% |
| 1000 | clean | 1698.3 | +19.6% | +2.4% | -4.5% |
| 1000 | no-op | 3522.5 | +6.0% | +1.5% | -3.6% |
| 1000 | single-page-change | 2760.3 | +13.2% | +1.9% | +1.8% |
| 1000 | layout-change | 2833.9 | +19.4% | +1.1% | -1.6% |
| 10000 | clean | 31046.3 | -4.6% | +2.1% | -6.5% |
| 10000 | no-op | 60185.7 | +7.4% | +1.5% | -11.0% |
| 10000 | single-page-change | 53894.3 | +9.8% | +1.6% | -11.9% |
| 10000 | layout-change | 56442.1 | +2.6% | +2.0% | -0.9% |

The 1,000-page timing regression remains a measured limitation. One repeat
measured 2406.9 ms for clean generation. To separate earlier environment state
from the code change, commit `b2d1e4e` was extracted and rebuilt under ignored
`artifacts/ssg-2a-reference/`, then measured immediately before 2B. That paired
comparison measured 1677.9/1907.5 ms for clean, 4188.9/3836.5 ms for no-op,
2239.1/2774.0 ms for single change, and 2193.9/3897.5 ms for layout change
(2A/2B; rounded). Paired time deltas were +13.7%, -8.4%, +23.9%, and +77.7%.
Raw files remain in `artifacts/ssg-2b-repeat/`, `artifacts/ssg-2a-current/`, and
`artifacts/ssg-2b-current/`.

Component extraction introduces intermediate HTML strings, which explains the
additional allocations; measured allocation growth stays at approximately 1–2.4%.
It does not establish the cause of the much larger, inconsistent timing deltas.
Filesystem and runtime scheduling variability remain possible contributors, not
proven causes. Accept the 2B API and compatibility work with this explicit timing
limitation; do not claim performance neutrality. No custom pooling or unrelated
rendering/cache redesign is added to this phase. Preserve these results for the
execution and cache work in phase 6.

日本語の検証要約: Releaseビルドは警告・エラー0件、TUnitは331件成功した。
固定時刻のDocs／Blogはフェーズ2Aと全成果物が一致し、NuGet互換性・内容検査と
100ページsmokeも成功した。1,000ページでは10%を超える時間増加が残る。
部品化に伴う中間HTML文字列で割当量は約1〜2.4%増えたが、大きな時間差の原因は
断定できていない。この制限を明記して2Bを採用し、フェーズ6の比較にも残す。

## Phase 3: static content generation

Added the separate analyzer package, explicit static collection and AdditionalFiles
contract, generated binders/schema/references, and LSG001–LSG006 diagnostics.
Generated-code tests compile and execute binders against reflection, including
inheritance, ignored required members, init setters, nested collections, defaults,
nulls, and diagnostic locations. Static input tests cover deletion, movement,
duplicate IDs, invalid declarations, YAML, metadata, and conflicting output routes.

Release compilation has zero warnings/errors; all 355 TUnit tests pass. The final
Docs sample uses generated entry references for route resolution and a generated
binder. Its 15 files and the Blog sample's 14 files match the fixed phase 2A outputs
byte for byte. Core package API/content validation and generator package content
validation pass. A separate temporary consumer restores the actual generator
NuGet package, compiles Binder/SchemaJson/Pages references, and runs successfully.
That consumer exposed an MSBuild metadata issue: CompilerVisibleItemMetadata needs
MetadataName, which is now corrected and exercised by the sample's named references.

The following final measurements use the same Windows/.NET 10.0.8 environment as
phase 2B. The 100-page run includes the smoke checks. Raw results are under
`artifacts/ssg-3/final/`; the comparison is against `artifacts/ssg-2b/`.

| Pages | Workload | Time (ms) | Time delta | Allocation delta | Peak working-set delta |
| --- | --- | ---: | ---: | ---: | ---: |
| 100 | clean | 476.2 | +32.1% | 0.0% | +1.8% |
| 100 | no-op | 398.6 | +32.6% | -0.2% | -2.0% |
| 100 | single-page-change | 311.3 | +11.6% | +0.4% | -4.7% |
| 100 | layout-change | 400.9 | +41.7% | +0.2% | -0.7% |
| 1000 | clean | 1995.4 | +17.5% | +0.1% | +1.4% |
| 1000 | no-op | 3819.0 | +8.4% | +0.7% | +2.5% |
| 1000 | single-page-change | 3301.1 | +19.6% | -0.3% | +3.5% |
| 1000 | layout-change | 4341.3 | +53.2% | -0.2% | -2.5% |
| 10000 | clean | 40775.4 | +31.3% | +0.1% | +0.6% |
| 10000 | no-op | 64478.7 | +7.1% | +0.1% | -0.3% |
| 10000 | single-page-change | 66048.9 | +22.6% | +0.1% | +1.3% |
| 10000 | layout-change | 78266.7 | +38.7% | +0.1% | 0.0% |

Initial measurements remain under `artifacts/ssg-3/` and are not replaced by the
final run. They showed roughly 0.6–1.6% extra allocation. Inspection found that
the netstandard-compatible route implementation allocated separator arrays on
each validation. Reusing those private arrays removed that allocation increase:
the 1,000-page clean run fell from 326,644,032 to 323,868,056 allocated bytes,
compared with 323,851,496 bytes for a freshly rebuilt phase 2B reference.

For a contemporaneous comparison, commit `2b882f8` was extracted and rebuilt in
`artifacts/ssg-3/phase2b-reference/`. Its sequential 1,000-page run measured
1852.7/5055.6/2906.0/3293.3 ms (clean/no-op/single/layout). The final phase 3 run
measured 1995.4/3819.0/3301.1/4341.3 ms, respectively: +7.7%, -24.5%, +13.6%, and
+31.8%. Corresponding allocation differences are +0.01%, +0.34%, +0.24%, and -0.17%.
The earlier paired phase 3 run is retained under `phase3-paired/`.

Adopt phase 3 with the timing limitation explicitly retained. The separator
allocation cause is established and fixed, but it does not explain the remaining
wall-clock regressions above 10%. These runs do not establish whether filesystem,
runtime scheduling, or another factor causes the remaining difference; no profiler
evidence is claimed. Memory stays within 10% in all final workloads. The analyzer
is not loaded by this runtime benchmark, and actual render skipping remains phase 6.

日本語の検証要約: 生成コードのコンパイルと実行、Reflectionとの互換性、入力・型・ルートの
診断を確認した。Releaseビルドは警告・エラー0件、TUnitは355件成功した。Docs／Blogは
既存の固定出力と一致し、NuGetパッケージを参照する別プロジェクトのビルド・実行も成功した。
ルート検証の区切り文字配列による余分な割当を修正し、最終測定のメモリ差は全項目で10%以内だった。
時間には10%を超える悪化が残り、その原因は断定できていない。連続比較と初回値を残し、
この制限を明記してフェーズ3を採用する。
