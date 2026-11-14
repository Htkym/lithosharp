---
title: Open Knowledge Format
supported_version: "3.0"
prerequisites: [quickstart]
---

The website factory reads `site/knowledge/` with `OkfBundle.ReadAsync`, a fixed
`AsOf` timestamp and the site's explicit base URL. It registers `RenderDocs` and
the captured auxiliary assets in the ordinary output transaction. Visit the
[English concept](../../knowledge/en/typed-sites.html) and its
[Japanese counterpart](../../knowledge/ja/typed-sites.html).

Exact lowercase `index.md` and `log.md` are reserved at every level. Other Markdown
files are concepts; non-Markdown inputs are auxiliary files. Concept paths are
identities independent of ordinary typed-document schemas. The reader retains
unknown metadata and distinguishes structural conformance, lint, freshness and
trust. Draft, stale or unverified records remain readable by default.

This bundle declares its format version and sources, but has no invented
verification or generation attestation. A source entry and a footnote do not prove
technical truth. Explicit policy is required to restrict publication based on trust.

Preserved export retains captured bytes; it is separate from YAML serialization.
Use the CLI's documented OKF inspection/export commands with a new destination and
inspect diagnostics before publishing. Export does not update trust records.
Ordinary `DocumentSemanticRuleSet.Documentation` rules are not applied to this
bundle. The repository's [limitations](../../reference/en/known-limitations/)
describe remaining format and tooling boundaries.
