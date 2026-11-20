---
title: Operations and verification
supported_version: "4.0"
prerequisites: [quickstart]
---

Use `DocumentationVerification.VerifySnippetsAsync` to verify code examples with explicitly mapped tests.

A snippet requires evidence of executed passing tests. A successful process exit,
an inspection without execution, skipped tests or a stale previous result does
not prove current success. Snippet and translation results
do not create OKF trust attestations.

Generated-output quality checks cover links, anchors and configured policies.
Inspect diagnostics at the original source location and related include or target
locations. Unknown source precision must remain unknown. Use JSON or SARIF
diagnostics for CI according to the [diagnostic contract](../../reference/en/diagnostics/).

The build graph tracks source, configuration, transformations and output owners.
Compare full, warm and edited builds with the same declared inputs; a cache hit
is not permission to omit a validation outcome. Output publication is transactional,
but deploying files to a host is a separate operation.

Use [migration](../migration/) when comparing an existing 2.x deployment.
