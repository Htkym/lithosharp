# Docusaurus compatibility corpus

Committed migration fixtures for C11. Each fixture runs
diagnose, convert, build, then route and structural DOM comparison.
MDX rendering itself is covered by `../mdx-baseline` (pinned reference)
and `../mdx-browser` (production hydration); these fixtures cover only
migration behavior and never duplicate that content.

Layout per fixture:

- `source/` is the Docusaurus-shaped input.
- `expected-routes.json` lists the converted public routes.
- `expected.json` pins exit code, per-file verdicts, diagnostic codes,
  manifest variants, built artifacts, and structural DOM checks.
- `build.json` selects the build shape (`mdx`, `blog`, generated `png`).

The `png` entry names a PNG the harness writes from fixed bytes before
analysis, so no binary file is committed. Structural DOM checks assert the
presence and absence of structural markers, not full normalized DOM dumps.

C09 compatibility table coverage:

| C09 construct | Fixtures |
| --- | --- |
| Tabs, TabItem | mdx-components |
| Admonition | mdx-components, admonitions |
| Details | mdx-components |
| CodeBlock | mdx-components, admonitions |
| TOCInline | mdx-components |
| Link | mdx-components |
| BrowserOnly | mdx-components |
| Translate | mdx-components |
| useBaseUrl, useDocusaurusContext | full-site (manual rewrite diagnostics) |
| Other `@theme/*` | full-site (unsupported diagnostic) |
| Other `@docusaurus/*` | full-site (unsupported diagnostic) |
| front matter, id, slug | docs, minimal |
| numeric prefixes, README, index | docs |
| `_category_.json`, `_category_.yml` | docs, sidebars |
| generated-index categories | sidebars |
| sidebars.js, docusaurus.config.js | sidebars, full-site |
| versioned_docs, versions.json, versioned_sidebars | docs-versioned, full-site |
| i18n locales | docs-i18n, full-site |
| blog dates, slugs, authors | blog, full-site |
| static assets, content images | assets, full-site |
