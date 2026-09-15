# Docusaurus migration

[日本語](docusaurus-migration.ja.md)

`migrate docusaurus` analyzes a Docusaurus site and, with `--output`,
converts it into a separate directory. It never executes JavaScript
configuration: `docusaurus.config`, sidebars and plugins stay reference-only
inputs with a warning. The input tree is never overwritten.

Only MDX, Markdown, static assets, front matter, categories, sidebars,
versions and locales migrate. The component judgments live in the
[MDX compatibility table](mdx.md#docusaurus-compatibility).

## Commands

```sh
lithosharp migrate docusaurus ./website --output ./converted \
  --expected-routes ./expected.json --base-url https://example.com/docs/
```

Without `--output` the command is a read-only dry run. `--expected-routes`
takes a JSON array of public paths and compares converted routes; `--base-url`
and `--default-locale` set the migration assumptions. Exit codes are `0` for
clean migration, `2` for usage errors and `3` when files need manual work or
are unconvertible. The printed JSON report carries `schemaVersion`,
per-file verdicts with positions and replacements, manifest variants and
authors, converted routes with missing/extra lists, and the exit code.

## Verdicts

| Verdict | Meaning |
| --- | --- |
| `Automatic` | Converted without changes. |
| `Convertible` | Converted with recorded mechanical edits (import removals, slug and id fixes). |
| `ManualActionRequired` | Converted with warnings; apply the documented manual step. |
| `Unsupported` | Not converted; needs an explicit replacement. |

## Executable fences

Top-level ` ```mdx-code-block ` fences wrap executable MDX, not display code:
their imports hoist and their JSX renders, so later prose can use what they
define. Samples inside outer fenced blocks stay display-only. The migration
treats executable fences as top-level code when it classifies imports.

## Route preservation

`versions.json` decides prefixes: `docs/` becomes the unreleased `next`
version, and the first listed version serves the version-less path. `@` in
paths stays literal. Duplicate document IDs gain numeric suffixes. Missing
front-matter titles are derived and recorded. Directory posts without date
information (such as blog release folders) keep directory routes instead of
dated slugs.

The generator emits trailing-slash routes. Originals built with
`trailingSlash: false` use flat `.html` files, so comparisons normalize the
slash style; the compared page set still comes from the original build.
Generated category indexes, blog authors/archive/pagination/tag indexes and
debug surfaces have no 1:1 source documents and stay outside the migration
route scope; the build may emit its own monthly archives and directory
indexes as a documented superset.

## Manual steps from the migrated sites

- Version-aware `UpgradeGuide` demos become static notes.
- `react-medium-image-zoom` imports are dropped; bare `<Zoom>` renders children.
- `LiteYouTubeEmbed` demos become YouTube links.
- `ColorModeToggle` demos become static notes.
- `raw-loader` source displays become static notes.
- Themed-image and inline-SVG live demos become static notes or images.
- Bare third-party imports are either installed in the target project or
  rewritten; `@docusaurus/useBaseUrl` call sites use static paths.

## Unsupported inputs

Arbitrary plugins and themes, theme overrides outside the import list,
JavaScript configuration execution, `react-live` imports from code editors
and `useBaseUrl`/`useDocusaurusContext` exports have no LithoSharp
equivalent. The report flags them (`LSMIG001`/`LSMIG002`/`LSMIG003`) and the
build fails explicitly instead of rendering silently wrong output.
