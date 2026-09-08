# LithoSharp performance benchmark

This project measures deterministic Blog-template workloads from a Markdown corpus. It is separate from the unit-test project and uses the fixed `SiteGenerationOptions.BuildTimestamp` value `2026-01-01T00:00:00+00:00`.

## Commands

Run the small CI smoke measurement:

```powershell
dotnet run --project benchmarks/LithoSharp.Performance --configuration Release -- --size 100 --smoke --output artifacts/performance-baselines/smoke.json
```

Smoke mode checks the corpus, clean output, the no-op and mutation invalidation scopes, and the working-set invariant. The result schema is version 3 and records `clean`, `no-op`, `single-page-change`, and `layout-change` workloads with elapsed milliseconds, allocations, start/end/peak working set, generated and invalidated node counts, and artifact count. The layout mutation changes the deterministic theme color consumed by every HTML rendering node; its invalidation metric therefore means rendering nodes in that scope, not every build node. It does not enforce timing or memory thresholds.

Run a baseline for one corpus size:

```powershell
dotnet run --project benchmarks/LithoSharp.Performance --configuration Release -- --size 10000 --output artifacts/performance-baselines/baseline-10000.json
```

`--size` accepts `100`, `1000`, or `10000`. Large baselines are manual; normal CI runs only the 100-page smoke. The output is JSON containing the corpus details, workload measurements, and runtime/environment metadata. The generated corpus and site are placed next to the result file under `corpus-{size}` and `site-{size}`.

Working-set sampling starts immediately after forced GC and immediately before `GenerateWithOptionsAsync`, then continues at the recorded fixed interval until the awaited generation task has completed or failed. The end working-set and allocation counters are captured immediately when generation completes, before monitor shutdown and artifact enumeration. The monitor's final sample is included before peak calculation, so each workload's peak is at least its start and end samples. `GenerationPeakWorkingSetBytes` is therefore the maximum sampled working set during clean generation; it is not the process-lifetime peak. The allocation and elapsed-time measurements use the same generation interval, with only the small measurement harness overhead included.

Each workload passes the immediately preceding workload's `SiteGenerationResult.BuildPlan` as `PreviousBuildPlan`: clean → no-op → single-page-change → layout-change. This keeps each invalidation count attributable to only the mutation introduced by that workload.

The first baseline pass records comparison data only. It does not enforce the planned 10% regression threshold; timing and working-set values are intentionally not used as pass/fail criteria.
