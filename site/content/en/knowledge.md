---
title: Open Knowledge Format
supported_version: "4.0"
prerequisites: [quickstart]
---

Open Knowledge Format (OKF) bundles package knowledge as Markdown concepts with
YAML metadata and related assets. LithoSharp 4.0.0 provides preview support for
inspecting, validating, exporting and rendering local OKF 0.2 and 0.3 bundles.

## Choose the bundle version

Create a root `index.md` with a quoted `okf_version: "0.2"` or
`okf_version: "0.3"` in its YAML frontmatter. This is a LithoSharp requirement
for validation and site generation, not a requirement for ordinary Markdown.
Use 0.2 to keep existing bundle behavior. For 0.3:

- Only current verification records affect trust; earlier records remain available
  as history. Date-only metadata values mean midnight UTC.
- Relative metadata paths start at the containing file, with no retry at the
  bundle root. A leading slash starts at the root. References between concepts
  form graph edges; assets and source-scope descriptions are separate.
- Untyped Markdown is skipped as a concept. Dot-prefixed files and directories
  are outside the bundle. The reserved `log.md` does not take frontmatter.

## Inspect a local bundle

```sh
lithosharp okf inspect --input knowledge --format json
lithosharp okf validate --input knowledge --format sarif
```

Use the [OKF guide](../../reference/en/open-knowledge-format/) for a bundle
example, strict lint and byte-preserving export. Unsupported versions and YAML
features are not reported as validated conformance. Live editor diagnostics and
reference edits do not apply the 0.3 rules. The reader does not execute
computations, attest results or fetch referenced URLs.

## Render a bundle in Docs

The official site's [knowledge examples](../../knowledge/index.html) retain the
0.2 profile. The site factory reads the bundle with `OkfBundle.ReadAsync` and
publishes the documents through `RenderDocs` together with captured auxiliary
assets. Reading verification metadata does not establish the truth of a claim.

Agent Ready publishing is a separate opt-in that serves public Markdown and
discovery links for published pages. See the
[Agent Ready guide](../../reference/en/agent-ready/) for what it exposes.
