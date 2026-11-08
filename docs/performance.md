# Performance

[日本語](performance.ja.md)

The latest reference was measured on 2026-11-08, 01:36:57–01:39:26 UTC: released 2.1.3 and 2.2.0 development commit
`bedc85cc63a5c1ef955e6fa34d1ce3118a983b90`, plus a standalone baseline
for the new typed Docs + Quality path. These are 100-page source-build
observations on one PC, not measurements of published 2.2.0 packages, release
acceptance, or a speed guarantee. No speedup is claimed.

[Raw observations, aggregates, source/binary hashes and conditions](evidence/performance/2026-11-08-committed-bedc85c-reference.json)
are retained with this document. They identify the measured snapshot even when
later changes use the same product version. Full logs and generated sites stay
local. The [harness guide](../benchmarks/LithoSharp.Performance/README.md) explains
reproduction and measurement boundaries.

## Versions and conditions

- Baseline: clean 2.1.3 product at `25d92d4605533260b22abffd2618971dd6f60c8a`,
  built separately with the common measurement sources overlaid.
- Candidate: 2.2.0 commit `bedc85cc63a5c1ef955e6fa34d1ce3118a983b90`,
  with a clean working tree at measurement time. The reference retains product
  source and actual loaded binary hashes. Subsequent documentation and
  aggregation-code edits happened after measurement.
- Windows 10.0.26300 x64, Intel Core Ultra 7 258V, 8 logical CPUs, SDK 10.0.403,
  runtime 10.0.14. MDX/editor used Node.js 24.13.0 and the recorded worker lockfile.
- The observed Core runtime is Syntamark 1.2.0. Its parser fingerprint matches
  the Generator's observed embedded Source fingerprint
  `1/17494090defc826957640640ddcdbca4268a9a1011cfe24a0e534247910a7faa`.
  The profile is `lithosharp-markdown/1`. Runtime assembly and Source-host identity
  are recorded separately; package declarations are not substituted for them.
- Site corpora contain 100 pages with build timestamp `2026-01-01T00:00:00Z`.
  Products and harnesses were Release-built in advance. Runs were sequential
  without concurrent builds or tests, and neither source nor build outputs
  changed during measurement. Fresh processes do not mean a
  cold operating-system cache. Corpus creation, restore and compilation are untimed.

## Blog continuity comparison

The existing Blog generation interval excludes manuscript reading and has no
explicit Quality configuration. Both products used identical Blog harness
sources and settings, three independent processes each, with alternating target
order. Each process ran clean → no-op → body edit → layout edit.

Times below are median milliseconds, with observed minimum–maximum in parentheses.

| Scenario | Released 2.1.3 | 2.2.0 `bedc85c` |
| --- | ---: | ---: |
| Initial generation | 474.54 (473.99–528.32) | 480.49 (475.41–495.29) |
| No change | 68.64 (62.18–74.35) | 61.17 (60.51–61.84) |
| One body edit | 202.13 (196.48–239.01) | 187.48 (186.94–208.07) |
| Layout edit | 227.72 (226.60–261.12) | 244.00 (210.99–256.07) |

The candidate's initial/layout medians are higher; no-op/body medians are lower.
Ranges overlap for three of the four scenarios. These small samples do not
establish a stable performance improvement. Final
mutated input and output bytes matched across all six observations, excluding
only the internal output manifest. These hashes describe the final state after
layout edit, not separate snapshots of each scenario.

## Typed Docs + Quality standalone baseline

This candidate-only profile uses `MarkdownContentCollectionLoader`, `RenderDocs`,
the shared Docs shell and explicit Quality: Error threshold, orphan checks on,
external HTTP checks off. Its deterministic runtime corpus uses the built-in
reflection binder. Generated binders and template/C# compilation are separate
compiler work; this is not a template build-time measurement.

Three fresh processes, 100 pages each:

| Scenario | Generation median ms (range) | Read + generate median ms |
| --- | ---: | ---: |
| Initial generation | 1,065.33 (1,062.74–1,136.42) | 1,229.03 |
| No change | 406.79 (395.88–542.11) | 448.59 |
| One body edit | 536.90 (509.35–571.00) | 576.48 |
| Layout edit | 1,026.94 (764.62–1,034.52) | 1,067.03 |

Generation elapsed excludes input loading. The separately named read-and-generate
elapsed, allocation and working-set measurements include loading and generation.
No-change runs reused all cacheable nodes but still ran enabled output Quality.
The adapter did not exist in 2.1.3: no cross-version ratio against a different
Blog or legacy Docs path is reported. Final output bytes matched across these
three observations.

## MDX, Generator and editor

MDX used its existing 80% static/10% page-hydration/10% load-island corpus and
ten-scenario sequence, in one independent process per product. Selected elapsed
seconds are shown below; all ten raw observations and worker stages are retained.
All ten scenarios completed successfully. The baseline initial plan stage
accounted for 8,035 ms. One pair does not establish an improvement or regression.

| MDX scenario | 2.1.3 seconds | 2.2.0 `bedc85c` seconds |
| --- | ---: | ---: |
| Initial generation | 8.68 | 6.87 |
| No change | 2.75 | 2.71 |
| One body edit | 5.63 | 5.30 |
| Layout edit | 2.98 | 3.22 |

Generator measurements exercised the actual embedded Source `ParseForCompiler`
header-scanning substage, including reflection-call overhead. For 100 inputs
with 4,096-character bodies, the median of three process medians was 0.6487 ms
for 2.1.3 and 0.6390 ms for the candidate. Each process excluded one warmup and
recorded five iterations. This excludes schema binding, code emission, Roslyn
and C# compilation, and proves no whole-Generator speedup.

The candidate editor harness drove the real `LspClient` and Release language
server with 50 KiB documents. Each of the four table conditions (Markdown/MDX
and burst/spaced) used a separate LSP process, excluded three warmups and recorded
20 edits, for 80 measured edits in total.
Values include the 50 ms debounce, transport and scheduling; they are not pure
parser time. Burst means sequential edits without an extra gap; spaced edits
used an additional 100 ms gap.

| Document | Burst p50/p95/max ms | Spaced p50/p95/max ms |
| --- | ---: | ---: |
| Markdown | 64.42 / 81.72 / 84.68 | 67.49 / 78.97 / 81.05 |
| MDX | 63.27 / 76.58 / 79.72 | 74.00 / 77.25 / 80.64 |

Separately from the table, a short soak used one LSP client to alternate between
20 KiB documents at two root URIs: 20 edits each, 40 in total, with one bounded restart.
That scenario used two LSP processes, before and after the restart. Together with
the four table processes, all six harness-owned LSP processes exited with code 0. The harness also
returned normally with code 0; no external stop was needed. Their
pre-stop working-set snapshots were
55,533,568–65,732,608 bytes; these are neither peaks nor a process-tree sum.
Harness working set was separately 41,783,296 → 45,559,808 bytes. Worker Node RSS
was unavailable. The editor protocol does not expose the loaded parser/Source
identity, so that field stays unavailable rather than borrowing the site result.
The harness records exact server/client binary and client-source hashes instead.

## Interpretation and limits

Stage times reuse `SiteBuildTimings`. Verification worker intervals overlap
execution and may exceed total elapsed time; never sum them as total duration.
Site working set is sampled at 10 ms and is not a lifetime peak. Managed
allocation excludes Node; worker heap snapshots are not Node RSS.

The [earlier uncommitted snapshot reference](evidence/performance/2026-11-07-typed-docs-reference.json)
remains a separate capture. Its excluded source-changing Blog attempt and invalid
Docs front-matter fixture, and its residual editor client timer requiring a
separate host stop, belong only to that earlier capture. They are not outcomes
of this completed committed run. Lower absolute values than the earlier capture
do not establish a speedup, and samples from the two captures are not pooled.
Missing, unsupported, failed and timed-out work remains distinct from zero.

These runs do not cover 1,000/10,000 pages, other operating systems, a long-duration
watch/editor soak, Extension Host overhead or a new competitor comparison. The
optional CI trend check remains opt-in, with released 2.1.3 as its default
baseline. Its 14-day artifacts are not the only support for this reference.

## Reproduce

Use the [harness guide](../benchmarks/LithoSharp.Performance/README.md) for Release
builds, profile selection, the configured matrix and the optional CI check.
Keep ordinary execution evidence under `.local/`; curate only reviewed reference
observations for the public evidence directory.

## Earlier measurements

The earlier 0.3.0/1.0.0/1.1.0 tables, unmet incremental targets and original
limitations remain in [Performance history](history/performance-through-2.1.3.md).
They are not current-release results.

<a id="1.0.0-markdown-generation"></a>
<a id="100-markdown-generation"></a>
<a id="earlier-markdown-compatibility-measurements"></a>
<a id="mdx-generation-and-large-sites"></a>
<a id="selective-hydration"></a>
<a id="competitor-comparison"></a>
<a id="1.1.0-candidate-editor-latency-measured"></a>
<a id="110-candidate-editor-latency-measured"></a>
<a id="1.1.0-performance-limitation"></a>
<a id="110-performance-limitation"></a>
<a id="current-release-scope"></a>
<a id="what-was-measured"></a>
<a id="what-this-does-not-prove"></a>

Older section links are retained here; follow the history page for those tables.
