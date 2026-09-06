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

## Phase 4: links and site quality

Added opt-in DOM inspection with source locations, internal artifact/fragment and
canonical checks, redirect ownership and graph dependencies, orphan/title/SEO
diagnostics, deterministic Text/JSON/SARIF reports, and a failure threshold before
transaction commit. External checks remain disabled unless explicitly configured.
Their versioned cache, HEAD/GET fallback, redirects, address restrictions, timeout,
in-flight cancellation, and process-wide request spacing have focused tests with
an in-memory HTTP handler; validation does not require a live network service.

Release compilation has zero warnings/errors and all 376 TUnit tests pass. Core
NuGet API/content validation and generator package-content validation pass. Both
samples run with `--check` and with `--check --redirect-demo`, with zero diagnostics.
Tests also cover base paths, encoded anchors and HTML entities, srcset, CSS assets,
empty resource URLs, broken references, duplicate titles, orphans, redirect cycles,
chains, missing targets, collisions, stale redirect deletion, cancellation, and
preservation of previously committed output on quality failure.

The first sample check exposed an existing defect: automatic social-image URLs
were emitted even without the source image needed to generate those files. The
shared built-in rendering now omits the four image metadata tags and uses the
`summary` Twitter card when there is no image. Explicit custom image URLs still
work, and image-backed compatibility fixtures remain unchanged. The existing
no-image SEO assertion was corrected to check that behavior; no fixture files
were replaced. Fixed Docs (15 files) and Blog (14 files) outputs were compared
against phase 2A: exactly 10 and 6 HTML files, respectively, differ only by those
four omitted lines and the card type. All other bytes and paths match.

The initial performance run is retained under `artifacts/ssg-4/`. It predates the
social-image fix; its smaller workloads also overlapped an agent's verification,
so it is not used as the final comparison. Final measurements under
`artifacts/ssg-4/final/` run sequentially without other build/test/sample commands,
on the same Windows/.NET 10.0.8 environment as phase 3. Quality is disabled in the
compatibility performance workloads; the checked samples exercise the DOM path.

| Pages | Workload | Time (ms) | Time delta | Allocation delta | Peak working-set delta |
| --- | --- | ---: | ---: | ---: | ---: |
| 100 | clean | 483.2 | +1.5% | -3.5% | +0.7% |
| 100 | no-op | 340.6 | -14.5% | -2.5% | +0.7% |
| 100 | single-page-change | 303.5 | -2.5% | -3.0% | +6.2% |
| 100 | layout-change | 315.7 | -21.2% | -2.6% | +1.6% |
| 1000 | clean | 2016.1 | +1.0% | -3.4% | +3.6% |
| 1000 | no-op | 5396.0 | +41.3% | -3.3% | +2.5% |
| 1000 | single-page-change | 3219.8 | -2.5% | -3.3% | -1.8% |
| 1000 | layout-change | 3050.1 | -29.7% | -3.0% | -3.6% |
| 10000 | clean | 36813.3 | -9.7% | -3.9% | +1.3% |
| 10000 | no-op | 63720.1 | -1.2% | -2.9% | +2.3% |
| 10000 | single-page-change | 59084.7 | -10.5% | -2.5% | -3.3% |
| 10000 | layout-change | 64224.5 | -17.9% | -2.2% | -2.7% |

Adopt phase 4 with the remaining 1,000-page no-op timing regression recorded:
5396.0 ms versus 3819.0 ms (+41.3%). Its cause is not established by these runs;
the workload still renders all pages and reuses no render cache before phase 6.
The generated-node and artifact counts are unchanged, allocation is 3.3% lower,
and neither the smaller nor the larger no-op run shows the same regression.
No profiler attribution is claimed. All other final time regressions and all
peak working-set regressions remain below 10%. The smaller HTML after the
social-image correction reduces allocated bytes by 2.2–3.9% in these workloads.

日本語の検証要約: Releaseビルドは警告・エラー0件、TUnitは376件成功した。
Docs／Blogの品質検査とリダイレクト例は診断0件で、NuGet互換性・内容検査も成功した。
サンプル検査で未生成のOGP画像への参照を発見し、共通描画を修正した。
画像を生成する互換fixtureは維持した。固定サンプルとの全ファイル比較では、画像がない場合の
メタデータ4行の削除とカード種別以外に差がないことを確認した。
性能は全規模で割当量が減り、ピークメモリの悪化は10%以内だった。1,000ページのno-opだけは
時間が41.3%増え、その原因は未特定である。ほかの規模のno-opでは同じ悪化がなく、
この制限を記録してフェーズ4を採用する。実際の描画省略はフェーズ6で実装する。

## Asset and image verification

Phase 5 adds declared asset transforms, original-path public copies, integrity
hashes, and the separate `LithoSharp.Images` package. The added tests execute
PNG/JPEG/WebP resizing, proportional dimensions, responsive markup and cache reuse.
They also cover source/settings invalidation, malformed cache recovery, preflight
on hits, declaration identity, missing/double writes, directory links and unsafe
input/output overlap. The existing compatibility fixtures are unchanged.

The Release build and locked restore succeed without warnings. TUnit has 385
passing tests. Core package compatibility against published 0.2.0 and contents of
Core, Generators and Images packages pass. Fixed Docs (15 files) and Blog (14 files)
match phase 4 byte for byte. Both quality checks return no diagnostics. The opt-in
Docs `--asset-demo --check` produces 18 artifacts with no quality diagnostics.
The new public APIs have XML documentation and English/Japanese usage and error
contracts in [assets and images](assets-and-images.md).

A real `avifenc` executable is not installed in this verification environment.
AVIF process invocation has compilation and code-review coverage, but no real
encode/decode execution test. PNG/JPEG/WebP run through the installed Skia runtime.
AVIF remains explicitly configured; Core never starts an external encoder.

An unchanged asset skips its final source/cache-to-staging write after the staged
bytes are verified. Transaction setup still copies the previous output into
isolated staging. This retains the existing rollback guarantees; these measurements
do not claim zero filesystem copy I/O. Rendering itself is still eager until phase 6.

The first sequential measurements are retained in `artifacts/ssg-5/baseline-*.json`.
Compared with phase 4's historical final runs, allocated-byte changes stay between
+0.1% and +1.6%, and working-set regressions stay below 10%, while timings vary
substantially. The largest historical timing regression is the 10,000-page layout
workload (+105.0%). No build, test or sample command overlaps the performance runs.

To investigate, the unchanged benchmark runner and dependencies were copied into
two isolated directories. One uses Core from the verified phase 4 NuGet package;
the other uses the current Core assembly. Each pair uses the same corpus/output
location and Windows/.NET 10.0.8, x64, 8 logical processors. Results are retained in
`artifacts/ssg-5/ab/phase{4,5}-{100,1000,10000}.json`. These fresh pairs are the main
comparison below. The compatibility corpus declares no public/image/transform
inputs, so it measures the effect on existing generation; tests and the sample
exercise the new asset behavior.

The old phase 4 binary itself changes from 2016.1 to 4993.4 ms for the 1,000-page
clean workload, and from 63720.1 to 126147.6 ms for the 10,000-page no-op workload.
Thus the historical differences include substantial run-to-run environment
variation. The exact split between filesystem latency, GC and other system activity
has not been profiled; no such attribution is claimed.

| Pages | Workload | Phase 4 time (ms) | Phase 5 time (ms) | Time delta | Allocation delta | Peak working-set delta |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 100 | clean | 388.1 | 438.3 | +12.9% | +1.5% | -0.8% |
| 100 | no-op | 386.9 | 311.3 | -19.5% | -0.1% | -1.4% |
| 100 | single-page-change | 329.1 | 305.1 | -7.3% | -0.1% | -2.3% |
| 100 | layout-change | 312.3 | 340.6 | +9.1% | -0.5% | -0.7% |
| 1000 | clean | 4993.4 | 4842.9 | -3.0% | +1.9% | +0.1% |
| 1000 | no-op | 7802.1 | 6338.8 | -18.8% | -0.1% | -3.3% |
| 1000 | single-page-change | 6502.4 | 4192.2 | -35.5% | +0.6% | +0.1% |
| 1000 | layout-change | 5957.1 | 4101.0 | -31.2% | -0.2% | +4.2% |
| 10000 | clean | 52348.8 | 52126.8 | -0.4% | +1.7% | +1.5% |
| 10000 | no-op | 126147.6 | 103472.3 | -18.0% | -0.7% | +8.6% |
| 10000 | single-page-change | 99198.3 | 63827.7 | -35.7% | -0.2% | +3.1% |
| 10000 | layout-change | 120502.7 | 78500.6 | -34.9% | -0.4% | +9.9% |

Adopt phase 5 with the residual 100-page clean timing difference recorded:
+12.9%, or 50.2 ms, in the fresh pair. The initial 100-page run was 9.6% faster
than the historical phase 4 result, and the larger fresh clean pairs are 3.0%
and 0.4% faster. The specific cause of the small-run difference is not isolated.
The observed environment variation, stable artifact/node counts, allocation
regressions of at most 1.9% and working-set regressions below 10% support adopting
the implementation. The paired timings do not establish a general speedup.
Further repeat measurement is deferred until phase 6 changes actual execution.

日本語の検証要約: Releaseビルドは警告・エラー0件、TUnitは385件成功した。
Docs／Blogの全成果物はフェーズ4と一致し、画像デモを含む品質診断は0件だった。
CoreのNuGet互換性と、Core／Generators／Imagesの内容検査も成功した。
PNG／JPEG／WebPは実際に変換した。AVIFの外部実行経路はビルドとレビューで確認したが、
avifencが未導入のため実際のエンコードは未検証である。

最初の性能比較では時間が最大105.0%増えたため、保存済みの旧Coreと現在のCoreを
同じ場所で続けて測定した。旧Core自体も以前の記録から大幅に遅くなり、
実行環境による変動を確認した。連続比較では100ページのcleanが12.9%増えたが、
差は50.2msで、1,000・10,000ページのcleanでは同じ悪化はなかった。
割当量の悪化は最大1.9%、ピークメモリの悪化は10%未満だった。
小規模測定の差の原因は特定できていない。この制限と初回の測定値を残して採用する。
変更のない資産の最終書込みと変換は省略するが、出力を安全に確定するための
ステージングへの複製は残る。ページの実際の描画省略はフェーズ6で実装する。

The final CI-style smoke also passes (`artifacts/ssg-5/final-smoke.json`, 607.5 ms
clean). It follows build/pack verification and is retained separately from the
isolated paired measurements.
