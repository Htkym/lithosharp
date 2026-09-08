# MDX verification record

This record covers follow-up phases 11A–19. The complete Windows, Linux and
macOS jobs passed in [CI run 34208355089](https://github.com/Htkym/lithosharp/actions/runs/34208355089)
at source commit `ab3b92c`. This includes worker/reference fixtures, .NET tests,
CLI/watch/API-example checks, packages/templates and production browser checks.
Performance measurements below use Windows x64. The [parity matrix](docusaurus-parity-matrix.md) maps requirements to tests;
the [English](mdx.md) and [Japanese](mdx.ja.md) guides document the supported APIs.

## Functional and distribution checks

The Release solution contains 14 projects. The local suite passes 427 .NET tests
and four worker tests. Tests include atomic output retention on cancellation,
timeout and worker failure; declared public props and secret canaries; imported
MDX/TSX/npm components; strict metadata; shared assets; source locations;
publication filtering; and no-op cache behavior.

Documentation tests exercise two collections, three versions and two languages,
subset retention, snapshots, Japanese/API search, category navigation, Git update
metadata, translation completeness, XML member IDs and OpenAPI version diffs.
The Markdown-only documentation fixture sets an unusable Node executable and
still builds. Phase-10 deterministic sample evidence remains in
[the baseline record](ssg-followup-baseline.md).

Production Chromium checks exercise Counter, tabs and keyboard interaction,
static text without JavaScript, five island strategies, page fallback, history,
head/scroll/focus and root cleanup. Additional checks cover missing browser APIs,
failed chunk recovery, sandboxed live code, explicit analytics consent and
revocation without duplicate page views, offline revision updates and service
worker retirement while retaining unrelated caches.

`eng/Test-MdxWatch.ps1` starts the real CLI server, changes an imported component,
checks worker reuse, retains the previous site after invalid MDX and C#, then
checks recovery. `eng/Test-Templates.ps1` installs the packed tool and all four
templates into an isolated local hive. It consumes separately packed C# and npm
extensions and compares CLI/API output. Seven shipping package IDs are checked
by `eng/Validate-Package.ps1`, including worker files and third-party notices.

`eng/Test-DocumentationVerification.ps1` builds API XML from a real solution and
runs an existing-framework example test project through the public verification
APIs. It then introduces an incorrect implementation and requires test failure.

## Measurement conditions

The cross-platform runs exposed and drove fixes for Windows administrator file
ownership, case-sensitive license discovery, Unix test path separators and
read-only backup expectations. macOS handle verification uses the native
`F_GETPATH` operation, including its ARM64 calling convention; see
[Apple's fcntl reference](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/fcntl.2.html)
and [Apple's ARM64 ABI](https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms).
The test host and CLI check resolve their own temporary directories to physical
paths before generation. Source/output link rejection is retained. The offline
retirement browser fixture also requires exactly one reload after retirement.

Large-corpus trials exposed unbounded simultaneous source reads and repeated
full-list lookups. Input reads are now capped at 32 and page/asset lookups use
dictionaries. Failed or interrupted trials are excluded from the accepted
performance tables.
Standalone stylesheet roots use stable logical input names and order; an
unrelated body edit must not reorder CSS on every static/island page. The corpus
runner rejects that regression instead of recording it as an accepted result.

Measured on Windows 10.0.26200 x64, Intel Core Ultra 7 258V with eight logical
processors and 32 GB RAM, .NET SDK 10.0.300,
runtime 10.0.8, Node 24.13.0 and the committed npm lockfile. The older phase-10
record used a different SDK and is preserved as historical evidence. The new
Markdown comparison rebuilds both `65ff20e` and the implementation with the same
SDK. Each MDX workload and the initial Markdown comparison is one observation.
The 100/1,000-page Markdown comparisons also include five alternating runs per
revision to investigate timing excesses; neither series is a universal speed claim.
Restore, package installation and corpus creation are outside generation timing.
The fixed build timestamp is 2026-01-01T00:00:00Z.

The MDX corpus has 80% static documents, 10% page-hydrated Counters and 10% load
islands, shared CSS/SVG/Counter, eight fixed paragraphs and separate 100-document
sidebars. This avoids a single 10,000-link sidebar on every page. It is a fixed
mixed workload, not a byte-identical substitute for the Markdown benchmark.
The server corpus does not enable optional browser navigation/search. Its final
asset inventory records all JS, CSS, HTML and JSON bytes separately; these are
site totals, not per-navigation transfer sizes. Browser/search behavior is
covered separately by the sample and documentation fixtures.
The benchmark sets `MdxOptions.Timeout` to fifteen minutes. The default is two
minutes; large worker requests can exceed that default on this machine.

The [JSON record](mdx-verification.json) retains raw elapsed time, .NET allocation, Node heap-used
snapshots, work counts, generated file counts and output bytes. Node snapshots
are not peaks. The Windows process-tree sampler sums the root .NET process and
discovered descendants at the same instant. It samples nominally every 50 ms and
discovers children every 500 ms; discovery overhead slows the actual interval.
Shared resident pages can be counted more than once and short-lived processes
or between-sample peaks can be missed. Its elapsed time also includes corpus
creation and teardown.

No-op and clean-with-warm-cache builds reuse worker results. A page edit compiles
and renders the affected MDX; a shared React change renders its consumers.
Browser bundling still processes all interactive entries on a worker cache miss.
Image URLs affect rendered HTML, while a C# theme change can reuse MDX output.
Route changes recreate the worker; a lockfile change invalidates dependency
fingerprints. These are explicit costs, not claims of per-entry bundling.
Zero MDX work does not mean a zero-cost site build: input validation, metadata,
the C# build graph and output integrity checks still run.

## MDX generation

The ten scenarios run sequentially with one reusable worker, except the route
scenario deliberately recreates it. "Warm-cache" means a clean output rebuild
with cached MDX results; it does not force recompilation of unchanged sources.
The lockfile edit adds whitespace: dependency fingerprinting and bundling run,
but unchanged output bytes cause zero output cache misses. Allocation is total
managed allocation during the workload, not retained memory.

| Pages / workload | Seconds | .NET allocated MiB | Compiled / rendered / bundled | Cache misses |
| --- | ---: | ---: | ---: | ---: |
| 100 / cold | 5.29 | 290.0 | 100 / 100 / 20 | 298 |
| 100 / warm-cache | 2.20 | 229.6 | 0 / 0 / 0 | 298 |
| 100 / no-op | 1.99 | 204.8 | 0 / 0 / 0 | 0 |
| 100 / one-page | 3.68 | 237.7 | 1 / 1 / 20 | 1 |
| 100 / shared-component | 4.31 | 269.8 | 0 / 20 / 20 | 42 |
| 100 / shared-css | 5.25 | 294.5 | 0 / 0 / 20 | 111 |
| 100 / shared-image | 5.17 | 263.8 | 0 / 100 / 20 | 112 |
| 100 / layout | 2.91 | 209.9 | 0 / 0 / 0 | 101 |
| 100 / route | 6.30 | 284.2 | 100 / 100 / 20 | 123 |
| 100 / lockfile | 4.99 | 207.6 | 0 / 0 / 20 | 0 |
| 1,000 / cold | 25.31 | 986.4 | 1000 / 1000 / 200 | 1468 |
| 1,000 / warm-cache | 12.51 | 929.5 | 0 / 0 / 0 | 1468 |
| 1,000 / no-op | 8.18 | 653.5 | 0 / 0 / 0 | 0 |
| 1,000 / one-page | 17.19 | 758.3 | 1 / 1 / 200 | 1 |
| 1,000 / shared-component | 20.73 | 887.6 | 0 / 200 / 200 | 402 |
| 1,000 / shared-css | 29.94 | 1,207.0 | 0 / 0 / 200 | 1101 |
| 1,000 / shared-image | 31.11 | 1,179.6 | 0 / 1000 / 200 | 1102 |
| 1,000 / layout | 19.80 | 1,015.2 | 0 / 0 / 0 | 1001 |
| 1,000 / route | 29.38 | 1,229.5 | 1000 / 1000 / 200 | 1203 |
| 1,000 / lockfile | 16.99 | 769.6 | 0 / 0 / 200 | 0 |
| 10,000 / cold | 274.30 | 8,441.4 | 10000 / 10000 / 2000 | 13168 |
| 10,000 / warm-cache | 129.55 | 8,273.9 | 0 / 0 / 0 | 13168 |
| 10,000 / no-op | 105.13 | 5,814.7 | 0 / 0 / 0 | 0 |
| 10,000 / one-page | 213.39 | 6,355.5 | 1 / 1 / 2000 | 1 |
| 10,000 / shared-component | 236.98 | 7,890.9 | 0 / 2000 / 2000 | 4002 |
| 10,000 / shared-css | 303.32 | 10,562.7 | 0 / 0 / 2000 | 11001 |
| 10,000 / shared-image | 321.65 | 10,637.4 | 0 / 10000 / 2000 | 11002 |
| 10,000 / layout | 192.88 | 9,498.1 | 0 / 0 / 0 | 10001 |
| 10,000 / route | 342.22 | 11,161.4 | 10000 / 10000 / 2000 | 12003 |
| 10,000 / lockfile | 205.64 | 6,544.0 | 0 / 0 / 2000 | 0 |

| Pages | Process-tree peak GiB | Final JS bytes | Final CSS bytes | Final HTML bytes |
| --- | ---: | ---: | ---: | ---: |
| 100 | 1.050 | 4,690,163 | 317,766 | 1,216,633 |
| 1,000 | 1.917 | 5,248,073 | 2,723,646 | 12,257,797 |
| 10,000 | 11.807 | 10,831,673 | 26,782,446 | 123,659,257 |

The RSS peak spans all ten scenarios, not just the cold build. Final generated
file counts are 297 / 1,467 / 13,167. The JSON retains exact byte counts and
sampler metadata. Site-wide CSS grows with esbuild's per-entry CSS bundles;
static/island pages also use standalone stylesheet outputs. Shared JavaScript
chunks do not imply optimal cross-page CSS deduplication.

The single-page edit has exactly one compiled module, one rendered page and
one output cache miss at all three sizes. It still bundles every interactive
entry. At 10,000 pages, the no-op takes 105.13 seconds and the single edit takes
213.39 seconds. The current whole-site validation and bundle costs are material
limits for large sites, even when MDX cache work is small. The two-minute default
worker timeout is insufficient for some requests in this corpus; fifteen minutes
was explicitly configured. These results do not establish a large-site speed
advantage over other generators.
## Existing Markdown path

| Pages / workload | Baseline ms | Current ms | Time change | Allocation change | Peak working-set change |
| --- | ---: | ---: | ---: | ---: | ---: |
| 100 / clean | 415.1 | 511.4 | +23.2% | 0.0% | +3.2% |
| 100 / no-op | 191.8 | 213.7 | +11.4% | -20.4% | +1.8% |
| 100 / single-page-change | 159.0 | 197.3 | +24.1% | -21.9% | +2.4% |
| 100 / layout-change | 248.7 | 232.1 | -6.7% | +0.9% | +5.1% |
| 1,000 / clean | 4,075.0 | 3,078.4 | -24.5% | +2.6% | +3.6% |
| 1,000 / no-op | 5,811.8 | 6,759.4 | +16.3% | -19.9% | +4.8% |
| 1,000 / single-page-change | 1,300.4 | 1,627.9 | +25.2% | -13.9% | +5.2% |
| 1,000 / layout-change | 1,933.6 | 2,098.0 | +8.5% | +1.1% | +5.7% |
| 10,000 / clean | 39,287.0 | 40,716.5 | +3.6% | +0.9% | +0.6% |
| 10,000 / no-op | 41,507.8 | 42,855.1 | +3.2% | -19.3% | +1.8% |
| 10,000 / single-page-change | 49,341.7 | 40,392.2 | -18.1% | -22.4% | +2.0% |
| 10,000 / layout-change | 79,937.0 | 76,477.9 | -4.3% | +1.2% | -3.6% |

The single observations exceed 10% in the 100-page clean/no-op/edit cases and
1,000-page no-op/edit cases. All allocation and RSS increases remain below 10%.
To investigate the timing excesses, both revisions were then run five times at
100 and 1,000 pages, alternating baseline/current order and using fresh output
roots. The same SDK, machine and fixed timestamp were retained. Medians follow;
all raw runs, including the slower first observations, remain in the JSON.

| Pages / workload | Baseline median ms | Current median ms | Time change | Allocation change | Peak working-set change |
| --- | ---: | ---: | ---: | ---: | ---: |
| 100 / clean | 430.4 | 428.0 | -0.6% | +1.6% | +3.4% |
| 100 / no-op | 194.1 | 198.2 | +2.1% | -22.2% | +0.2% |
| 100 / page edit | 176.0 | 172.3 | -2.1% | -20.9% | 0.0% |
| 100 / layout | 222.4 | 205.2 | -7.7% | -3.0% | +1.1% |
| 1,000 / clean | 2,198.3 | 2,246.8 | +2.2% | +5.1% | +2.8% |
| 1,000 / no-op | 3,697.6 | 3,803.6 | +2.9% | -18.6% | +4.6% |
| 1,000 / page edit | 1,639.2 | 1,601.9 | -2.3% | -20.0% | +2.4% |
| 1,000 / layout | 2,323.9 | 2,216.7 | -4.6% | +0.5% | +7.9% |

The timing excesses did not reproduce in these medians. The broad ranges show
why a single short run is insufficient: 1,000-page clean times range from
2,034–2,995 ms for the baseline and 2,105–3,505 ms for the implementation.
File-system caching/scheduling, process startup and JIT are plausible sources
of this variation, but this test does not isolate their individual contribution.
Windows ownership checks and the extension integration remain enabled for
correctness; they are not disabled to improve timings. The repeated evidence
supports retaining the change, not a universal speedup or a guarantee that every
run stays within 10%. The 10,000-page results remain single observations.
## Browser comparison

Chromium 153.0.8010.12 ran five navigations per route and mode, alternating mode
order, at 1280×720. Each navigation used a fresh context with service workers
blocked and waited for network idle. No scrolling or manual activation was
performed. The local server sent uncompressed responses. Gzip sizes below are
the sum of loaded JS bodies compressed separately at level 6, not observed
compressed transfers. Browser results are medians; raw rows include parse,
compile and evaluation trace durations, heap usage, long tasks and layout shift.

| Route / mode | Loaded JS bytes | Gzip bytes | JS requests | Script ms | Hydration ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Static / page | 449,585 | 95,085 | 4 | 17.679 | 12.0 |
| Static / selective | 15,817 | 4,998 | 1 | 3.089 | 0 |
| Islands / page | 451,572 | 95,743 | 5 | 21.514 | 13.2 |
| Islands / selective | 438,296 | 93,609 | 13 | 15.976 | 18.2 |
| Page fallback / page | 451,676 | 95,773 | 5 | 19.794 | 9.5 |
| Page fallback / selective | 452,377 | 97,876 | 10 | 17.115 | 10.6 |

Static selective output requests only the documentation UI script, without a
React hydration root. The islands route has three started island roots at the
measurement boundary; deferred/manual work is not counted as completed. Its
smaller loaded JS and ScriptDuration do not mean every activation is cheaper.
The fallback page adds descriptor/shared-chunk overhead and does not improve JS
size. All six groups had median zero long tasks and zero cumulative layout shift.

Hydration spans `hydrateRoot` to the first passive-effect commit, excludes
dynamic imports and sums roots that may overlap. Trace durations may overlap
too. Neither sum is an exclusive wall-clock measurement. These distinctions
explain why island hydration sums can grow while overall script time decreases.

The `--activate` run repeats the islands route five times per mode at 640×600,
then scrolls to the visible island, changes the viewport to 1200×800 and
dispatches the manual activation event. Both modes receive the same actions.
The selective run requires all five hydration commits before taking its final
snapshot. Values after activation are cumulative, not activation-only durations.

| Mode / checkpoint | React roots | Loaded JS bytes | Script ms | Hydration ms |
| --- | ---: | ---: | ---: | ---: |
| Page / initial | 1 | 451,572 | 21.034 | 9.6 |
| Page / after actions | 1 | 451,572 | 21.174 | 9.6 |
| Selective / initial | 2 | 438,296 | 16.101 | 13.1 |
| Selective / all activated | 5 | 438,296 | 18.056 | 14.6 |

Idle activation has completed at the initial network-idle checkpoint. Visible,
media and manual activation add work afterward. They add no JS bytes in this
fixture because all five islands use the same Counter module, already loaded
by the load/idle islands. This does not demonstrate network savings for five
different deferred modules. Both modes observed median layout shift 0.157 at
the narrow viewport, already present before activation; the earlier 1280×720
measurements observed zero. Deferred activation did not remove that layout
shift, and the report does not claim a site-wide CLS guarantee.

## Reproduction

Run `eng/Test-Tool.ps1`, `eng/Test-MdxWatch.ps1` and the package/template checks
after the Release build and worker restore. The browser fixture's `npm test`
expects the production MDX sample at `http://127.0.0.1:4317`.
`offline-update.mjs` additionally needs the next and retired sample revisions;
the exact commands are in `.github/workflows/build.yml`.

Run `LithoSharp.MdxPerformance` with page count, restored worker directory and
a new result directory. On Windows, wrap it with `eng/Measure-ProcessTree.ps1`.
`tests/fixtures/mdx-browser/measure.mjs` takes page-hydration origin, selective
origin and destination JSON. Use the same sample with `--page-hydration` for the
baseline. Keep other builds and browser checks out of performance runs.
Add `--activate` to record the narrow-viewport islands route before and after
all deferred roots start. This run has different viewport conditions and is
reported separately from the default navigation measurements.

## 検証結果の読み方

Windows、Linux、macOS の CI で、ビルド、試験、配布物、ブラウザー動作を確認した。
性能値は Windows の固定した条件での観測値であり、
すべてのサイトで同じ改善を保証するものではない。静的MDXではReactの読み込みを省ける一方、
Islandの分割やページ全体へのfallbackには追加費用がある。
