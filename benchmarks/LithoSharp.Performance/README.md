# Performance harnesses

The existing Blog generation interval is retained for continuity. The candidate
adds a separate typed Docs + Quality profile and a Generator header-scan
substage. These are measurements, not latency guarantees or speed targets.
Current reference observations and their durable numeric evidence are linked
from [Performance](../../docs/performance.md). Historical results keep their
original source and environment in [History](../../docs/history/performance-through-2.1.3.md).

## Commands

Build Release before measuring. Restore is explicit; use the SDK selected by
`global.json`, the same runtime and worker lockfile for both targets, and avoid
concurrent builds or browser work. Fresh processes are not a cold OS cache.

```powershell
dotnet restore LithoSharp.slnx --locked-mode
dotnet build benchmarks/LithoSharp.Performance -c Release --no-restore -p:EnableTypedDocsBenchmark=true
dotnet run --project benchmarks/LithoSharp.Performance -c Release --no-build -- --size 100 --smoke --output .local/performance/blog/result.json
dotnet run --project benchmarks/LithoSharp.Performance -c Release --no-build -- --size 100 --profile typed-docs-quality --output .local/performance/docs/result.json
dotnet run --project benchmarks/LithoSharp.Performance -c Release --no-build -- --size 100 --mode generator --generator-assembly src/LithoSharp.Generators/bin/Release/netstandard2.0/LithoSharp.Generators.dll --output .local/performance/generator.json
```

`--size` accepts 100, 1,000 or 10,000. `--scenario` accepts `all`, `clean`,
`no-op`, `single-page-change` or `layout-change`. A selected incremental scenario
runs after a successful clean preparation in its own process. `all` runs the
sequence in one process. Do not mix these cache/process conditions.

The typed Docs feature does not exist in 2.1.3. It is disabled at compile time
unless `EnableTypedDocsBenchmark=true`; identical Blog harness sources can
therefore build against 2.1.3 with this property omitted. Record the new Docs
path as its own standalone baseline, not a ratio against legacy Docs or Blog.
Its runtime binder is the built-in reflection binder over a deterministic
runtime corpus. Template compilation/generated binders are a separate compiler
path; this profile is not a template build-time benchmark.

Generator mode uses the actual Generator assembly's `ParseForCompiler` Source
host. It includes reflection invocation overhead and header scanning, excluding
schema binding, Roslyn, code emission and C# compilation. It is not full
Generator throughput. Build that assembly before running. `--warmup` and
`--iterations` apply to this substage and engine mode, not site mode.

Engine microbenchmarks remain available through `--mode engine` or `--mode all`
and `--engine-output`. Parse-only, pre-prepared render-only, parse+render and
analyze remain separate. The fixed corpora and per-iteration elapsed/allocation
statistics are unchanged. The last removed Markdig reference is historical,
not a fresh dependency-conformance result.

## Configured campaigns

Copy [matrix.example.json](../../eng/performance/matrix.example.json) under
`.local/`, resolve paths relative to that configuration, fill in the candidate
SHA, and choose a new evidence directory. Point each target at separately built
products containing identical benchmark and shared identity sources. The
baseline's product files must remain clean at the released 2.1.3 SHA
`25d92d4605533260b22abffd2618971dd6f60c8a`. Only common harness overlays may
modify the baseline tree.

```powershell
./eng/Run-PerformanceMatrix.ps1 -Configuration .local/performance/campaign.json
```

The driver validates commits and harness sources, records dirty candidate
changes plus product source/file hashes, alternates order and refuses to call a
source-changing campaign a fixed comparison. All attempts have fresh evidence
directories. The incremental runner retains raw files, identity, stages,
failures, timeouts and unavailable values. `-Resume` skips only completed work
for the same profile and harness binary; it does not replace failed records.

## Measurement boundaries (C00)

- Blog elapsed/allocation/10 ms sampled process working set cover generation,
  not corpus reading. Existing meanings are unchanged.
- Typed Docs generation elapsed excludes input load. Its separately named
  read-and-generate elapsed, allocation and sampled working set cover both.
- Site stages come from `SiteBuildTimings`. Verification worker intervals
  overlap execution and may exceed total wall time. Do not sum stage values.
- Process working set excludes a Node worker. MDX worker heap snapshots are
  not worker RSS. [MDX](../LithoSharp.MdxPerformance) records worker stages,
  runtime identity, Node version and lockfile hash separately.
- [Editor](../../extensions/lithosharp-vscode/eng/measure-editor.mjs) saves raw
  monotonic edit-to-diagnostics milliseconds, including debounce, transport and
  scheduling. It separately labels harness and owned server PID working sets;
  unmeasured Node RSS and protocol-unavailable loaded dependency identity are
  explicitly unavailable. `--samples`, `--warmup`, `--soak`, `--gap`, `--out`,
  `--server`, `--worker`, `--timeout` and `--languages` configure bounded runs.
- Loaded Core runtime/parser/profile and embedded Generator Source identity are
  observations. Package declaration strings alone do not establish a loaded pair.
- Failures, missing measurements, unsupported features and timeouts are never
  replaced by zero or successful empty arrays. No timing thresholds are enforced.

## Optional CI comparison

The normal build and tests remain required. A pushed commit containing
`[measure]`, or manual `run_measurement`, additionally runs the small Blog
comparison on Ubuntu 24.04. Default baseline is released 2.1.3, not the previous
branch head; an explicit full SHA can select another control. The common
candidate harness builds against both revisions with typed Docs disabled.
SDK/runtime/page count/output are configurable in `compare-site-generation.py`.

The CI check uses A/B/B/A, retains raw observations and final input/output file
hashes for 14 days and never fails on timing differences. Two observations per
revision cannot establish a small speedup. Generated output differences are
reported without hiding them by normalization; correctness invariants can fail.
Short-lived CI artifacts are working evidence, not the sole durable support for
public performance claims. Curated reference observations may be retained under
`docs/evidence/performance/`; full logs, sites and corpora remain in `.local/`.
