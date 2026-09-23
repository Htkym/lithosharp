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
| V110-01 | TODO | — | |
| V110-02 | TODO | — | Must fix the CS9057 generator host gate found in V110-00 |

States follow the plan: `TODO → IN_PROGRESS → IMPLEMENTED → VERIFIED`, with
`BLOCKED`, `VERIFIED_UNCHANGED`, `DEFERRED_APPROVED` as auxiliary states.

## 1.0.0 baseline reminder

- `v1.0.0` = `11f7e74494e9646033dce1937385c5aadd58d602`; all seven published packages carry that repository commit.
- The seven packages are `LithoSharp`, `LithoSharp.Generators`, `LithoSharp.Images`, `LithoSharp.Mdx`, `LithoSharp.Testing`, `LithoSharp.Tool`, `LithoSharp.ProjectTemplates`.
