# LithoSharp 1.1.0 verification records

Committed record of the v1.1.0 (Core) and v0.1.0 (VS Code extension) verification work.
Raw logs, environment dumps and package downloads stay outside git in
`.local/verification/1.1.0/<run-id>/`; this directory keeps the summary and the
per-task memos that reviewers need.

- Plan of record: `.local/roadmap/1.1.0/LithoSharp_v1.1.0_実装計画書.md` (not committed)
- Raw evidence root: `.local/verification/1.1.0/`

## Task status

| Task | State | Memo | Notes |
| --- | --- | --- | --- |
| V110-00 | VERIFIED | [tasks/V110-00.md](tasks/V110-00.md) | B0/B1 fixed; B1 test baseline is locally BLOCKED by the generator host incompatibility on the pinned SDK 10.0.300 |
| V110-01 | VERIFIED | [tasks/V110-01.md](tasks/V110-01.md) | Fixture manifest, 18 scenario definitions, and the evidence runner with its self-test |
| V110-02 | VERIFIED | [tasks/V110-02.md](tasks/V110-02.md) | Generator Roslyn reference pinned back to 4.14.0; 1.0.0 package baseline validation, consumer and generator-host gates wired into CI |
| V110-03 | VERIFIED | [tasks/V110-03.md](tasks/V110-03.md) | Capability contract and `lithosharp capabilities`; existing 1.0 envelopes unchanged |
| V110-04 | IMPLEMENTED | [tasks/V110-04.md](tasks/V110-04.md) | Scenario runner and harness scenario selection; B0 baseline and dominant-factor analysis. B1r 10,000-page and MDX baselines are BLOCKED by a machine slowdown |
| V110-05 | VERIFIED | [tasks/V110-05.md](tasks/V110-05.md) | Opt-in `SiteBuildTimings` counters; measured proof that input/plan/fingerprint is 8-11% and output staging is ~87-89% (handed to V110-06) |
| V110-06 | IMPLEMENTED | [tasks/V110-06.md](tasks/V110-06.md) | Review withdrew the in-place overlay because readers could observe mixed generations; verified full no-op bypass remains, changed builds use staged directory replacement. Earlier overlay A/B values are historical; candidate performance is pending V110-25 |
| V110-07 | VERIFIED | [tasks/V110-07.md](tasks/V110-07.md) | Entry-level rebundle counter (`RebundledPages`): a static body edit rebundles 0 interactive entries while shared component/CSS edits rebundle every dependent; gated in the MDX harness, and the runner's `bundledEntries` counter typo is fixed |
| V110-08 | IMPLEMENTED | [tasks/V110-08.md](tasks/V110-08.md) | Cache inventory; explicit per-output reporting/reclamation shares the output lock with builds; optional cache-write failures no longer fail valid output. MDX stale-scratch cleanup rejects reparse points. Byte-budget LRU and soak timers remain |
| V110-09 | IMPLEMENTED | [tasks/V110-09.md](tasks/V110-09.md) | Markdown compat ledger (`eng/verification/1.1.0/markdown-compat.json`); opt-in LIT003/004/005 advisories sharing the fence/code/escape scan; `lithosharp markdown-compat --advisory on\|off` wired to the same inspection |
| V110-10 | IMPLEMENTED | [tasks/V110-10.md](tasks/V110-10.md) | Project-aware inspection (`ProjectInspectionSnapshot`, route candidates, builtin binder validation, draft/unlisted/explicit statuses); `markdown-compat --project` shares the same analysis. Generation matching and cancellation go to V110-12 |
| V110-11 | IMPLEMENTED | [tasks/V110-11.md](tasks/V110-11.md) | MDX analysis-only worker request plus an editing-owned `MdxInspectionSession` (no plugin/module load, bundle, SSR, or network); fatal diagnostics keep the worker alive. LSP wiring goes to V110-14 |
| V110-12 | IMPLEMENTED | [tasks/V110-12.md](tasks/V110-12.md) | Workspace generation management; superseded requests now cancel while waiting for an analysis slot, and request token sources are disposed after use. LSP wiring is recorded in V110-14/17 |
| V110-13 | IMPLEMENTED | [tasks/V110-13.md](tasks/V110-13.md) | Structured serve shutdown (`serve --control-stdin`), generation IDs, served/generated base-path separation, route snapshot; `eng/Test-ServeControl.ps1` wired into CI. Extension controller goes to V110-16 |
| V110-14 | IMPLEMENTED | [tasks/V110-14.md](tasks/V110-14.md) | Minimal LSP stdio server (`src/LithoSharp.LanguageServer`): diagnostics, symbols, project context, cancellation; 17実stdio tests. Extension shell goes to V110-15 |
| V110-15 | IMPLEMENTED | [tasks/V110-15.md](tasks/V110-15.md) | Extension shell (`extensions/lithosharp-vscode` 0.1.0): static project detection, limited trust, CLI resolution; 22 unit tests. Real Host verification goes to V110-19 |
| V110-16 | IMPLEMENTED | [tasks/V110-16.md](tasks/V110-16.md) | BuildRunner is shared per project so concurrent commands serialize; serve state reflects startup, recovery and process exit events. Preview wiring goes to V110-18 |
| V110-17 | IMPLEMENTED | [tasks/V110-17.md](tasks/V110-17.md) | LSP document analyses are superseded/cancelled per URI; startup replays already-open documents, context replacement preserves the last valid snapshot, and unpositioned diagnostics are not assigned fabricated ranges |
| V110-18 | IMPLEMENTED | [tasks/V110-18.md](tasks/V110-18.md) | Preview matches exact source paths from inspect data, waits for the actual startup event, keeps the current route across port changes, and marks dirty documents as unsaved |
| V110-19 | IMPLEMENTED | [tasks/V110-19.md](tasks/V110-19.md) | Extension responsiveness and stability (editor latency/soak harness, real Host smoke 3/3, §5 Editor gates pass); 74 unit tests. VSIX/other-OS/30-minute soak goes to V110-23/25/26 |
| V110-20 | IMPLEMENTED | [tasks/V110-20.md](tasks/V110-20.md) | Additive migration route comparison (exact + explicit normalized page set, classified exclusions/source hashes) and component functional-change report; Tool build passes, 843 tests pass |
| V110-21 | IMPLEMENTED | [tasks/V110-21.md](tasks/V110-21.md) | Third-party migration corpus (pinned Prettier/Jest/Docusaurus sources, isolated Docker stages, route oracles, bounded candidate build/serve); 3 sites verified-scoped |
| V110-22 | IMPLEMENTED | [tasks/V110-22.md](tasks/V110-22.md) | Shipping-quality docs (1.1 feature/upgrade/golden-path docs, measured editor latency with unmeasured markers, public evidence bundle + sanitizer, JA/EN sync); no product code changes |
| V110-23 | IMPLEMENTED | [tasks/V110-23.md](tasks/V110-23.md) | Distribution gates verify a lock-hash completion marker for worker restore and separate publish-time publisher injection from pack. Marketplace remains BLOCKED pending a real publisher account |
| V110-24 | IMPLEMENTED | [tasks/V110-24.md](tasks/V110-24.md) | Core correctness on candidate (862 tests, markdig 71, worker 14, 3-site re-run, soaks, eng/browser suites on Windows x64); 3 fixes (UTC dates, LF feeds, Prism hooks); 3-OS matrix + console-Ctrl+C left to CI |
| V110-25 | IN_PROGRESS | [tasks/V110-25.md](tasks/V110-25.md) | Review fixes pass the Windows regression checks; pre-review candidate measurements are not comparable after withdrawing in-place overlay publication; clean candidate matrix pending |

2026-10-03 review follow-up: code review found and fixed output-publication, cancellation, cache-lifetime, LSP, preview, worker-restore and release-pipeline issues. The overlay-era A/B values are historical only; the reviewed candidate still needs a clean commit, Release build and V110-25 matrix. See [tasks/V110-25.md](tasks/V110-25.md) and the plan's final review log for verification details and next steps.

States follow the plan: `TODO → IN_PROGRESS → IMPLEMENTED → VERIFIED`, with
`BLOCKED`, `VERIFIED_UNCHANGED`, `DEFERRED_APPROVED` as auxiliary states.

## 1.0.0 baseline reminder

- `v1.0.0` = `11f7e74494e9646033dce1937385c5aadd58d602`; all seven published packages carry that repository commit.
- The seven packages are `LithoSharp`, `LithoSharp.Generators`, `LithoSharp.Images`, `LithoSharp.Mdx`, `LithoSharp.Testing`, `LithoSharp.Tool`, `LithoSharp.ProjectTemplates`.
