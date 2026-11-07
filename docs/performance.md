# Performance

[日本語](performance.ja.md)

The latest reference was measured on 2026-11-07: released 2.1.3 and an
uncommitted 2.2.0 development snapshot, plus a standalone baseline for the new
typed Docs + Quality path. These are source-build observations, not measurements
of published 2.2.0 packages, release acceptance, or a speed guarantee. Later
navigation-cache and correctness fixes are outside this capture. No speedup is
claimed.

[Raw observations, aggregates, source/binary hashes and conditions](evidence/performance/2026-11-07-typed-docs-reference.json)
are retained with this document. They identify the measured snapshot even when
later changes use the same product version. Full logs and generated sites stay
local. The [harness guide](../benchmarks/LithoSharp.Performance/README.md) explains
reproduction and measurement boundaries.

## Versions and conditions

- Baseline: clean 2.1.3 product at `25d92d4605533260b22abffd2618971dd6f60c8a`,
  built separately with the common measurement sources overlaid.
- Candidate: 2.2.0 source build based on that commit. The reference records
  repository-relative product source hashes and the actual loaded binary hashes;
  the base commit alone does not identify this uncommitted snapshot.
- Windows 10.0.26300 x64, Intel Core Ultra 7 258V, 8 logical CPUs, SDK 10.0.403,
  runtime 10.0.14. MDX/editor used Node.js 24.13.0 and the recorded worker lockfile.
- The observed Core runtime is Syntamark 1.2.0. Its parser fingerprint matches
  the Generator's observed embedded Source fingerprint
  `1/17494090defc826957640640ddcdbca4268a9a1011cfe24a0e534247910a7faa`.
  The profile is `lithosharp-markdown/1`. Runtime assembly and Source-host identity
  are recorded separately; package declarations are not substituted for them.
- Site corpora contain 100 pages with build timestamp `2026-01-01T00:00:00Z`.
  Runs were sequential without concurrent builds. Fresh processes do not mean a
  cold operating-system cache. Corpus creation, restore and compilation are untimed.

## Blog continuity comparison

The existing Blog generation interval excludes manuscript reading and has no
explicit Quality configuration. Both products used identical Blog harness
sources and settings, three independent processes each, with alternating target
order. Each process ran clean → no-op → body edit → layout edit.

Times below are median milliseconds, with observed minimum–maximum in parentheses.

| Scenario | Released 2.1.3 | 2.2.0 snapshot |
| --- | ---: | ---: |
| Initial generation | 1,869.63 (1,584.33–1,902.90) | 1,840.49 (1,623.12–3,183.85) |
| No change | 169.91 (161.51–194.21) | 171.72 (143.53–182.80) |
| One body edit | 848.01 (797.38–973.90) | 920.58 (822.84–1,416.21) |
| Layout edit | 832.57 (745.13–942.26) | 1,006.84 (727.91–1,331.50) |

The candidate's body/layout medians are higher. The small sample and overlapping
ranges do not establish a stable difference or a performance improvement. Final
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
| Initial generation | 3,120.93 (2,655.32–3,389.91) | 3,520.91 |
| No change | 1,206.14 (889.23–2,240.43) | 1,386.23 |
| One body edit | 1,480.47 (1,272.97–3,177.47) | 1,600.48 |
| Layout edit | 3,089.29 (3,041.90–3,517.67) | 3,380.00 |

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
One pair does not establish an improvement or regression.

| MDX scenario | 2.1.3 seconds | 2.2.0 snapshot seconds |
| --- | ---: | ---: |
| Initial generation | 23.20 | 19.37 |
| No change | 12.14 | 10.43 |
| One body edit | 21.33 | 15.39 |
| Layout edit | 13.80 | 17.25 |

Generator measurements exercised the actual embedded Source `ParseForCompiler`
header-scanning substage, including reflection-call overhead. For 100 inputs
with 4,096-character bodies, the median of three process medians was 0.8157 ms
for 2.1.3 and 0.6947 ms for the snapshot. Each process excluded one warmup and
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
| Markdown | 66.37 / 84.59 / 90.28 | 67.69 / 80.24 / 92.68 |
| MDX | 74.08 / 83.98 / 97.53 | 73.44 / 78.03 / 78.37 |

Separately from the table, a short soak used one LSP client to alternate between
20 KiB documents at two root URIs: 20 edits each, 40 in total, with one bounded restart.
That scenario used two LSP processes, before and after the restart. Together with
the four table processes, all six harness-owned LSP processes exited. Their
pre-stop working-set snapshots were
58,114,048–66,035,712 bytes; these are neither peaks nor a process-tree sum.
Harness working set was separately 46,026,752 → 49,119,232 bytes. Worker Node RSS
was unavailable. The editor protocol does not expose the loaded parser/Source
identity, so that field stays unavailable rather than borrowing the site result.
The harness records exact server/client binary and client-source hashes instead.

## Interpretation and limits

Stage times reuse `SiteBuildTimings`. Verification worker intervals overlap
execution and may exceed total elapsed time; never sum them as total duration.
Site working set is sampled at 10 ms and is not a lifetime peak. Managed
allocation excludes Node; worker heap snapshots are not Node RSS.

The reference retains explicit excluded-attempt reasons: an initial campaign
changed source metadata mid-run, and an initial Docs fixture had front matter
outside its minimal schema. Neither was relabeled successful or substituted
for the completed reference. Missing, unsupported, failed and timed-out work is
kept distinct from zero. After the editor samples were saved and all LSP children
exited, a residual client timer kept the harness host alive; that verified-owned
host required a separate stop. This execution limitation is retained separately
from the completed latency data. The later cleanup-only exit fix is not a new
performance observation; the reference keeps the harness hash actually measured.

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
