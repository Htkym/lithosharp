---
title: Operations and verification
supported_version: "3.0"
prerequisites: [quickstart]
---

`Build.ps1` checks the bilingual coverage declarations, restores and builds the
package consumer, generates the output with `SiteQualityOptions`, and invokes
`DocumentationVerification.VerifySnippetsAsync` for two explicit fences and the
trusted `checks/LithoSharp.Website.Checks.csproj` project. The tests compile the
same source file that the pages include. Input fingerprints include source,
project, package-lock and SDK configuration files and the selected package version.

A snippet requires evidence of executed passing tests. A successful process exit,
an inspection without execution, skipped tests or a stale previous result does
not prove current success. Translation review hashes are maintained only by an
explicit review; this script never writes them. Snippet and translation results
do not create OKF trust attestations.

Generated-output quality checks cover links, anchors and configured policies.
Inspect diagnostics at the original source location and related include or target
locations. Unknown source precision must remain unknown. Use JSON or SARIF
diagnostics for CI according to the [diagnostic contract](../../reference/en/diagnostics/).

The build graph tracks source, configuration, transformations and output owners.
Compare full, warm and edited builds with the same declared inputs; a cache hit
is not permission to omit a validation outcome. Output publication is transactional,
but deploying files to a host is a separate operation.

Keep deliberately broken fixtures outside the public input roots. Run the
repository's existing negative checks separately. Capture URL/anchor baselines
only after a confirmed successful publication; this source does not promote them.
Use [migration](../migration/) when comparing an existing 2.x deployment.
