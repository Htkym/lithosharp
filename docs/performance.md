# Performance

[日本語](performance.ja.md)

This document separates measurements of the 1.0.0 candidate, earlier 0.3.0
measurements from 2026-09-08, and competitor comparisons from 2026-09-13.
Each result applies to its stated fixture and environment, not a service-level promise.

## What was measured

The Markdown harness measures generation time, managed allocation and resident
memory. The MDX harness also reports worker compilation, rendering and bundling.
Browser tests compare page hydration and selective hydration on the same fixtures.
Private raw measurement records are not distributed in the repository or packages.

Environment: Windows build 26200 x64, Intel Core Ultra 7 258V, 8 logical processors,
32 GB RAM, .NET SDK 10.0.300/runtime 10.0.8, Node.js 24.13.0, Chromium 153.0.8010.12.
The worker lockfile pins MDX 3.1.1, React 19.2.4 and esbuild 0.25.12.

## 1.0.0 Markdown generation

The 2026-09-15 measurements used the final compiler source at `5cc8581` on the
Windows/.NET environment above. The table reports medians from five independent
processes for 100 and 1,000 pages and two for 10,000 pages. MB means decimal MB;
allocations cover managed memory, not peak resident memory.

| Pages | Clean build | Managed allocation | One body edit |
| --- | ---: | ---: | ---: |
| 100 | 426.14 ms | 40.67 MB | 211.61 ms |
| 1,000 | 5,083.53 ms | 359.58 MB | 3,410.56 ms |
| 10,000 | 71,195.96 ms | 3,564.19 MB | 75,680.04 ms |

No-op builds invalidated no documents. Earlier runs showed substantial timing
variation, including runs above the release thresholds; the final measurements
ran without overlapping local verification work. The 10,000-page sample is small
and does not establish a latency guarantee. These results do not remeasure the
large-site MDX or browser scenarios below.

## Earlier Markdown compatibility measurements

For 100 and 1,000 pages, the baseline and 0.3.0 implementation were alternated
five times. Median time, allocation and resident-memory increases were all within
10%. Individual runs exceeded a 10% time difference; this does not establish zero
regression in every run. This is the Markdown benchmark baseline, not a timing
comparison against the published NuGet 0.2.0 upgrade consumer.

## MDX generation and large sites

These 0.3.0 measurements were taken on 2026-09-08 and have not been rerun in full
against the final 1.0.0 candidate.

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

These browser measurements were taken on 2026-09-08 with 0.3.0.

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

## Competitor comparison

On 2026-09-13 the same machine and fixed corpora compared production builds
of LithoSharp against Docusaurus 3.10.2 with React 19.2.4, Astro 7.3.2 with
Starlight 0.42.0, Hugo 0.166.0 extended and MkDocs 1.6.1 with Material 9.7.7,
using Plain Markdown, Documentation and MDX/Interactive profiles. Medians
below are per-profile build times; feature differences in search, highlight,
navigation and JavaScript behavior apply and unsupported combinations are not
reported as zero.

| 1,000 pages | LithoSharp | Hugo | MkDocs | Astro | Docusaurus |
| --- | ---: | ---: | ---: | ---: | ---: |
| Plain | 6.7 s | 2.8 s | 21.3 s | 6.6 s | 34.6 s (blog) |
| Docs | 12.4 s | 0.76 s | 23.9 s | 14.3 s (Starlight) | 23.9 s |

At 10,000 pages Hugo and Astro finished, Docusaurus ran out of memory and
MkDocs exceeded the ten-minute limit. Hugo and MkDocs do not support MDX.
Raw results stay local; the procedure is reproducible from the repository
harness. Windows results do not predict Linux/macOS throughput.

## What this does not prove

The 10,000-page MDX figures above have no equivalent competitor run. Windows results do not predict
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
