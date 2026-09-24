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
| V110-05 | TODO | — | |

States follow the plan: `TODO → IN_PROGRESS → IMPLEMENTED → VERIFIED`, with
`BLOCKED`, `VERIFIED_UNCHANGED`, `DEFERRED_APPROVED` as auxiliary states.

## 1.0.0 baseline reminder

- `v1.0.0` = `11f7e74494e9646033dce1937385c5aadd58d602`; all seven published packages carry that repository commit.
- The seven packages are `LithoSharp`, `LithoSharp.Generators`, `LithoSharp.Images`, `LithoSharp.Mdx`, `LithoSharp.Testing`, `LithoSharp.Tool`, `LithoSharp.ProjectTemplates`.
