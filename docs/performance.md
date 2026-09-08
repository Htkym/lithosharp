# Performance

[日本語](performance.ja.md)

These are measurements of the implemented 0.3.0 runtime, taken on 2026-09-08.
They are not comparisons with Docusaurus or Astro and are not service-level promises.

## What was measured

The Markdown harness measures generation time, managed allocation and resident
memory. The MDX harness also reports worker compilation, rendering and bundling.
Browser tests compare page hydration and selective hydration on the same fixtures.
Private raw measurement records are not distributed in the repository or packages.

Environment: Windows build 26200 x64, Intel Core Ultra 7 258V, 8 logical processors,
32 GB RAM, .NET SDK 10.0.300/runtime 10.0.8, Node.js 24.13.0, Chromium 153.0.8010.12.
The worker lockfile pins MDX 3.1.1, React 19.2.4 and esbuild 0.25.12.

## Markdown compatibility

For 100 and 1,000 pages, the baseline and current implementation were alternated
five times. Median time, allocation and resident-memory increases were all within
10%. Individual runs exceeded a 10% time difference; this does not establish zero
regression in every run. This is the Markdown benchmark baseline, not a timing
comparison against the published NuGet 0.2.0 upgrade consumer.

## MDX generation and large sites

| 10,000-page corpus | Elapsed |
| --- | ---: |
| Cold | 274.30 s |
| No-op | 105.13 s |
| One body edit | 213.39 s |

The one-body-edit run compiled and rendered one module/page, but bundled 2,000
interactive entries. Across ten scenarios, the observed simultaneous process-tree
working-set maximum was 11.807 GiB. It is neither a cold-build peak nor a minimum
RAM requirement. Managed allocations exclude Node memory; worker heap snapshots
are not peak RSS. The worker timeout was fifteen minutes, above the two-minute
default. Incremental compilation does not imply an instant end-to-end rebuild.

## Selective hydration

Static-page fixture, five navigations per mode, alternating mode order, fresh
browser contexts, service workers blocked, 1280 × 720 viewport, measured at
network idle before deferred activation:

| Metric | Page hydration | Selective hydration |
| --- | ---: | ---: |
| JS bytes | 449,585 | 15,817 |
| gzip equivalent bytes | 95,085 | 4,998 |
| Hydration roots | 1 | 0 |
| Hydration | 12.0 ms | 0 ms |
| Median script duration | 17.679 ms | 3.089 ms |

These reductions apply to this static fixture. The island fixture's script median
was 21.514 → 15.976 ms, while its summed hydration duration was 13.2 → 18.2 ms.
Hydration marks can overlap and exclude dynamic-import waiting. The fallback
fixture loaded 451,676 → 452,377 JS bytes; it did not show the static-page reduction.
With all deferred islands activated at 640 × 600 then 1200 × 800, script duration
was 16.101 → 18.056 ms and hydration 13.1 → 14.6 ms; CLS was 0.157 in both modes.
The initial static viewport and fully activated layout are different scenarios.

## What this does not prove

No equivalent competitor benchmark was run. Windows results do not predict
Linux/macOS throughput. Fixture savings do not imply that every MDX page is
zero-JS, that all islands become faster, or that arbitrary npm dependencies work.
See [Known limitations](known-limitations.md) for execution and platform boundaries.

## Reproduce

Build Release and restore the worker explicitly before measuring. Use the same
SDK, lockfile, timestamp and corpus; avoid concurrent builds or browser workloads.

```sh
dotnet restore LithoSharp.slnx --locked-mode
dotnet build LithoSharp.slnx -c Release --no-restore
dotnet run --project benchmarks/LithoSharp.Performance -c Release --no-build -- --size 100 --smoke --output .local/performance-smoke.json
```

The full harnesses live in [Markdown](../benchmarks/LithoSharp.Performance),
[MDX](../benchmarks/LithoSharp.MdxPerformance),
[process-tree measurement](../eng/Measure-ProcessTree.ps1), and
[browser measurement](../tests/fixtures/mdx-browser/measure.mjs).
The browser harness accepts page and selective origins and an output file, and
runs five repetitions; `--activate` measures deferred activation. Keep measurement output local.
