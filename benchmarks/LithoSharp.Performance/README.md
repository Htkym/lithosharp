# LithoSharp performance benchmark

This project measures deterministic Blog-template workloads from a Markdown corpus. It is separate from the unit-test project and uses the fixed `SiteGenerationOptions.BuildTimestamp` value `2026-01-01T00:00:00+00:00`.

C00 baseline: site/pipeline workloads and engine micro-benchmarks are measured
separately. Render-only never includes parsing. The engine fixed corpora are
`paragraph-heavy`, `heading-heavy`, `nested-list`, `link-heavy`, `code-heavy`,
`gfm-table`, `task-list`, `admonition`, `practical-docs`, `docusaurus-doc`, and
`pathological`.

## Commands

Run the small CI smoke measurement (site mode is the default for compatibility):

```powershell
dotnet run --project benchmarks/LithoSharp.Performance --configuration Release -- --size 100 --smoke --output artifacts/performance-baselines/smoke.json
```

Smoke mode checks the corpus, clean output, the no-op and mutation invalidation scopes, and the working-set invariant. The result schema is version 3 and records `clean`, `no-op`, `single-page-change`, and `layout-change` workloads with elapsed milliseconds, allocations, start/end/peak working set, generated and invalidated node counts, and artifact count. The layout mutation changes the deterministic theme color consumed by every HTML rendering node; its invalidation metric therefore means rendering nodes in that scope, not every build node. It does not enforce timing or memory thresholds.

Run a baseline for one corpus size:

```powershell
dotnet run --project benchmarks/LithoSharp.Performance --configuration Release -- --size 10000 --output artifacts/performance-baselines/baseline-10000.json
```

`--size` accepts `100`, `1000`, or `10000`. Large baselines are manual; normal CI runs only the 100-page smoke. The output is JSON containing the corpus details, workload measurements, and runtime/environment metadata. The generated corpus and site are placed next to the result file under `corpus-{size}` and `site-{size}`.

Run the engine micro-benchmarks (parse-only, render-only, parse+render, analyze):

```powershell
dotnet run --project benchmarks/LithoSharp.Performance --configuration Release --no-build -- --size 100 --mode engine --warmup 1 --iterations 5 --engine-output .local/verification/1.0.0/engine-100-run-01.json
dotnet run --project benchmarks/LithoSharp.Performance --configuration Release --no-build -- --size 100 --mode all --output .local/verification/1.0.0/baseline-100-run-01.json --engine-output .local/verification/1.0.0/engine-100-run-01.json
```

`--mode` accepts `site` (default), `engine`, or `all`. `--warmup` sets unrecorded
warmup runs, `--iterations` sets recorded iterations per corpus operation (default 5).
These flags apply to engine workloads only: site workloads execute once per
process, and site provenance records warmup 0 with 1 iteration to match the
actual measurement.
Each operation records raw elapsed/allocation lists, median, mean, variance, min, max,
first-vs-warm first value, and median allocation. Official comparison uses at least 5
independent process executions per size; in-process iterations measure variance within
a process, not across processes.

## Measurement boundaries (C00)

- Site workloads measure the Blog-template pipeline overall: elapsed, allocation,
  sampled working set, generated/invalidated node counts, render-adjacent artifact counts,
  and cache-hit behavior via `BuildReport`.
- Engine workloads measure the Litho frontend over the fixed corpora and
  iteration statistics (`LithoCorpora` with parse-only, render-only,
  parse+render, and analyze). The Markdig reference columns were removed in
  C13; the last side-by-side comparison is `c12-engine.json` at the old
  commit, and the explicit comparison project can regenerate it. Link new-tab
  attributes and asset dependencies need site context and are covered by site
  workloads only. The supported scope renders identically to the last
  reference run; `nested-list` (`a.` markers, U14), `admonition`
  (alerts/containers, U05/U06), and `docusaurus-doc` (containers, U06)
  intentionally diverge.
- `GenerationPeakWorkingSetBytes` is the maximum sampled working set during clean
  generation; it is not the process-lifetime peak. Engine files do not record working
  set; site files do.
- Provenance records input hash (site corpus SHA-256 over `*.md`), commit, SDK/runtime,
  OS/CPU, dependency versions, execution arguments, and warmup/iteration counts.
- Out of scope: MDX/Node workloads (see `LithoSharp.MdxPerformance`), OS peak RSS,
  timing/memory thresholds, and cross-machine comparison. The first baseline pass
  records comparison data only.

Working-set sampling starts immediately after forced GC and immediately before `GenerateWithOptionsAsync`, then continues at the recorded fixed interval until the awaited generation task has completed or failed. The end working-set and allocation counters are captured immediately when generation completes, before monitor shutdown and artifact enumeration. The monitor's final sample is included before peak calculation, so each workload's peak is at least its start and end samples. `GenerationPeakWorkingSetBytes` is therefore the maximum sampled working set during clean generation; it is not the process-lifetime peak. The allocation and elapsed-time measurements use the same generation interval, with only the small measurement harness overhead included.

Each workload passes the immediately preceding workload's `SiteGenerationResult.BuildPlan` as `PreviousBuildPlan`: clean → no-op → single-page-change → layout-change. This keeps each invalidation count attributable to only the mutation introduced by that workload.

The first baseline pass records comparison data only. It does not enforce the planned 10% regression threshold; timing and working-set values are intentionally not used as pass/fail criteria. Candidate regression thresholds from C00 variance are recorded in the implementation instruction (C05 record); final confirmation happens in R19. Do not treat the exploration target in the plan as achieved.
