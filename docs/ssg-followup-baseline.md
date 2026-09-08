# Follow-up baseline — phase 10

Baseline commit: `65ff20e0de6f4b8018064354e56c45fb984f89cd`.
Audit date: 2026-09-08. The preceding roadmap records phases 0–8 as completed
and phase 9 as outside that implementation. Its optional features are carried
forward explicitly in [the parity matrix](docusaurus-parity-matrix.md).

This is the starting baseline for the MDX follow-up, not an R1 release.
[The architecture contract](mdx-architecture.md) defines the next integration.
The phase 10 audit itself did not change production Core, public APIs or legacy
output. Subsequent implementation changes are documented in [the MDX guide](mdx.md).
Keep this original snapshot separate from later measurements: the later local
comparison rebuilds both revisions with SDK 10.0.300 / runtime 10.0.8, rather than
comparing its timings directly with this earlier SDK 10.0.400 snapshot.

## Rechecked baseline

On Windows 10.0.26200 x64, .NET SDK 10.0.400 and runtime 10.0.11:

- Locked restore succeeded; Release build: 11 projects, zero warnings/errors.
- All 414 existing TUnit tests passed, including compatibility, deterministic
  output, incremental cache, atomic output and public testing-host coverage.
- Six product packages passed `eng/Validate-Package.ps1`: Core, Generators,
  Images, Testing, Tool and ProjectTemplates. Pack ran with package validation
  enabled. Packing the solution also created three non-product sample/benchmark
  packages and reported their missing READMEs; they are not distribution targets.
- Docs and Blog samples each ran twice at `2026-09-02T00:00:00Z`, with local
  quality checks and no diagnostics. All files, including the internal output
  manifest, matched within each pair: 14 public Docs files and 13 public Blog
  files. No expected-output fixtures were updated.
- Blog also generated successfully with an empty `PATH`, using the absolute
  dotnet executable. This checks operation without a discoverable Node/npm;
  it does not claim Node was uninstalled from the machine.

[Machine-readable inventory](ssg-followup-baseline.json) records API/lockfile
hashes, every sample output hash and product package entries/hashes. Internal
manifest hashes can change with assembly fingerprints; public output comparisons
must report that separately. Package hashes identify this run, not a guarantee
of reproducible ZIP timestamps across packaging environments.

Reproduce from the baseline commit with its locked dependencies:

```powershell
dotnet restore LithoSharp.slnx --locked-mode
dotnet build LithoSharp.slnx --no-restore -c Release
dotnet test --solution LithoSharp.slnx --no-build -c Release
$env:SOURCE_DATE_EPOCH = '1788307200'
dotnet samples/LithoSharp.DocsSample/bin/Release/net10.0/LithoSharp.DocsSample.dll --check --output artifacts/ssg-10/docs-1
dotnet samples/LithoSharp.Sample/bin/Release/net10.0/LithoSharp.Sample.dll --check --output artifacts/ssg-10/blog-1
```

Repeat into fresh `docs-2`/`blog-2` directories and compare relative paths and
SHA-256 values. The Docs factory sets the same timestamp explicitly. Preserve
the original untracked workspace files; they are not baseline inputs.
`global.json` allows feature-band roll-forward, so record the actual SDK;
the previous phase used 10.0.300/runtime 10.0.8.

## Performance conditions

Reuse `benchmarks/LithoSharp.Performance` unchanged. Its corpus sizes are 100,
1,000 and 10,000 Markdown pages, with timestamp `2026-01-01T00:00:00Z`, four
sequential workloads (clean, no-op, one body change, layout color change),
schema 3 and a 10 ms working-set sampling interval. Run no other builds/tests
or browser workloads during measurement.

```powershell
dotnet run --project benchmarks/LithoSharp.Performance -c Release --no-build -- --size 100 --smoke --output artifacts/ssg-10/baseline-100.json
dotnet run --project benchmarks/LithoSharp.Performance -c Release --no-build -- --size 1000 --output artifacts/ssg-10/baseline-1000.json
dotnet run --project benchmarks/LithoSharp.Performance -c Release --no-build -- --size 10000 --output artifacts/ssg-10/baseline-10000.json
```

The prior measured results remain in [ssg-verification.md](ssg-verification.md),
phase 8. This audit fixes the measurement conditions; it has not rerun the three
sizes or measured MDX/browser performance. Do not compare an SDK/runtime change
as if it were an MDX algorithm change. Prior no-op execution was zero nodes;
one body change executed three. The recorded 10,000-page working-set increases
of 25.2–34.2% remain a limitation, not a resolved regression.

## Toolchain and dependency policy

| Component | Fixed reference version | Scope |
| --- | --- | --- |
| .NET | SDK 10.0.400 / runtime 10.0.11 | This local baseline; repository still permits feature-band roll-forward |
| Node | 24.13.0 | Build-only MDX/reference environment |
| npm | 11.6.2 | Lockfile v3, explicit restore |
| MDX / MDX React | 3.1.1 | Official compiler/provider |
| React / React DOM | 19.2.4 | Single installed copy of each |
| esbuild | 0.25.12 | Separate MDX integration bundler candidate |
| Docusaurus Core / Classic | 3.10.2 | Comparison fixture only, not a LithoSharp runtime dependency |

Exact direct versions were checked against npm registry metadata. The reference
fixture pins all transitive resolutions and integrity values in its lockfile.
Docusaurus uses its own locked bundler; esbuild is tested separately rather
than substituted into Docusaurus. The fixture uses CommonJS configuration,
because its generated registry uses `require.resolveWeak` with webpack.

Restore only with `npm ci --ignore-scripts --no-audit --no-fund`. Build and test
must not install packages. Dependency scripts remain disabled; this fixture
uses the platform esbuild binary distributed as an optional dependency.
If a platform lacks that binary, report the failure instead of silently
enabling arbitrary install scripts. Dependency upgrades require an explicit
lockfile diff, repeated reference tests and license review.

The fixture's `verify.mjs` rejects duplicate React and React DOM lock entries.
It also compares the installed React module resolved from React DOM, MDX React
and Docusaurus Core, and checks the Node/npm versions used by the test.
This is a baseline guard; phase 11C must additionally validate resolved module
identities in actual site imports, including linked packages. A site-supplied
second copy must not evade the production check merely by being outside the
fixture lockfile.

MDX, React and esbuild direct licenses are MIT; the comparison fixture's
transitive licenses are recorded in the npm lockfile and installed package
license files. Preserve those notices for any future redistribution. This
change does not ship npm dependencies in a NuGet package. It does not certify
future arbitrary site dependencies or execute user package scripts.

Three transitive entries omit the modern lockfile license field: `eval@0.1.8`
and `require-like@0.1.2` include MIT license texts; `format@0.2.2` declares MIT
in its legacy `licenses` field and README. The locked `uuid@8.3.2` dependency
emits a deprecation warning during restore; no transitive override was added
to the pinned comparison target. Locked `npm ci` and the subsequent test passed.

Windows is the locally tested environment. Linux and macOS are target
validation environments, not locally verified results. Core's existing Linux
CI remains independent of this optional Node fixture. Worker process cleanup,
browser hydration, MDX security and R1 package distribution remain future
integration gates.

The separate `mdx-reference.yml` workflow runs the pinned fixture on Windows,
Linux and macOS when it changes or is dispatched explicitly. Its remote results
are not available in this local record. Local production builds for both
locales and `npm test` passed: MDX named export, initial server HTML, browser
bundle generation and the Docusaurus reference HTML. No browser was launched.

## 日本語での検証要約

フェーズ10の開始監査として、基準コミット、公開API、パッケージ内容、Docs／Blogの
全成果物、性能測定条件、識別契約、MDX処理の境界を記録した。Releaseビルドは警告0、
既存テストは414件成功し、固定時刻のサンプル出力はそれぞれ2回の生成で一致した。
NodeをPATHから参照できない状態でもBlogを生成できた。

今回のSDKとランタイムは前回と異なる。3規模の性能測定、Linux／macOSでの実行、
LithoSharpへのMDX統合とブラウザーでのHydrationは、この検証結果に含めない。
次の実装対象は11Aであり、11B・11C・12の受け入れ試験までR1を完了扱いにしない。
