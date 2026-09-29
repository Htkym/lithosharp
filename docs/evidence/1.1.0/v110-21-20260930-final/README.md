# Public evidence: V110-21 migration corpus

[日本語](README.ja.md)

Sanitized rerun records for the pinned third-party migration corpus
(`20260930-final`). Full traces, workspace checkouts and machine logs stay
local; this bundle carries everything needed to judge scope and reproduce:

- `version-manifest.json`: corpus/run IDs, repository commit, package versions,
  SDK/Node/worker pins, container digests.
- `run.json`: overall state and the per-site pass/fail list. No equivalence
  beyond the declared page set is claimed.
- `sites/<id>.json`: per-site pins, stage list with commands, networks, exit
  codes and input/output hashes, migration verdicts, candidate build/serve
  summaries, manual-action tallies, known limits.
- `routes/<id>-route-oracle.json`: the complete typed original route oracle.
- `commands.txt`: exact rerun commands.
- `sanitizer-manifest.json`: root replacements applied and the (empty) secret
  scan result.

Regenerate with `eng/New-EvidenceBundle.ps1 -RunId <run-id> -WorkspaceRoot
<workspace>` after a `Run-MigrationCorpus.ps1` run. Machine-specific roots are
replaced with `<repo>`/`<workspace>` tokens; stage results and counts are
preserved verbatim. The sanitizer fails the run if secrets remain.
