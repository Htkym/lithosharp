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
| V110-06 | VERIFIED | [tasks/V110-06.md](tasks/V110-06.md) | No-op bypass plus a same-shape overlay commit: rebuilt artifacts are published file by file with a journaled backup, so unchanged artifacts are neither copied nor rewritten (1-page edit 4,016ms → 1,723ms and 301.7MB → 126.9MB per 1,000 pages) |
| V110-07 | VERIFIED | [tasks/V110-07.md](tasks/V110-07.md) | Entry-level rebundle counter (`RebundledPages`): a static body edit rebundles 0 interactive entries while shared component/CSS edits rebundle every dependent; gated in the MDX harness, and the runner's `bundledEntries` counter typo is fixed |
| V110-08 | IMPLEMENTED | [tasks/V110-08.md](tasks/V110-08.md) | Cache inventory; explicit per-output reporting/reclamation (`SiteGenerator.MeasureCache`/`ClearCache`, `lithosharp cache info\|clean`) and MDX crash-leftover cleanup. Byte-budget LRU and soak timers remain |
| V110-09 | IMPLEMENTED | [tasks/V110-09.md](tasks/V110-09.md) | Markdown compat ledger (`eng/verification/1.1.0/markdown-compat.json`); opt-in LIT003/004/005 advisories sharing the fence/code/escape scan; `lithosharp markdown-compat --advisory on\|off` wired to the same inspection |
| V110-10 | IMPLEMENTED | [tasks/V110-10.md](tasks/V110-10.md) | Project-aware inspection (`ProjectInspectionSnapshot`, route candidates, builtin binder validation, draft/unlisted/explicit statuses); `markdown-compat --project` shares the same analysis. Generation matching and cancellation go to V110-12 |

States follow the plan: `TODO → IN_PROGRESS → IMPLEMENTED → VERIFIED`, with
`BLOCKED`, `VERIFIED_UNCHANGED`, `DEFERRED_APPROVED` as auxiliary states.

## 1.0.0 baseline reminder

- `v1.0.0` = `11f7e74494e9646033dce1937385c5aadd58d602`; all seven published packages carry that repository commit.
- The seven packages are `LithoSharp`, `LithoSharp.Generators`, `LithoSharp.Images`, `LithoSharp.Mdx`, `LithoSharp.Testing`, `LithoSharp.Tool`, `LithoSharp.ProjectTemplates`.
