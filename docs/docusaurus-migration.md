# Docusaurus migration

[日本語](docusaurus-migration.ja.md)

`migrate docusaurus` analyzes a Docusaurus site and, with `--output`,
converts it into a separate directory. It never executes JavaScript
configuration: `docusaurus.config`, sidebars and plugins stay reference-only
inputs with a warning. The input tree is never overwritten.
Conversion rejects directory links in the source and destination paths, including
their parents. Use the real paths of separate directories.

Only MDX, Markdown, static assets, front matter, categories, sidebars,
versions and locales migrate. The component judgments live in the
[MDX compatibility table](mdx.md#docusaurus-compatibility).

## Commands

```sh
lithosharp migrate docusaurus ./website --output ./converted \
  --expected-routes ./expected.json --base-url https://example.com/docs/
```

Without `--output` the command is a read-only dry run. The legacy
`--expected-routes` JSON string array keeps its raw, ordinal exact comparison.
For route classification and provenance, use an object oracle:

```json
{
  "sourceVersion": "3.10.2",
  "basePath": "/old-site/",
  "routes": [
    { "path": "/old-site/docs/start/", "kind": "document", "locale": "en" },
    { "path": "/old-site/docs/category/", "kind": "categoryIndex", "locale": "en" },
    { "path": "/old-site/blog/tags/dotnet/", "kind": "blogTag", "locale": "en" }
  ]
}
```

The mutually exclusive `kind` values are `document`, `categoryIndex`,
`blogIndex`, `blogAuthor`, `blogTag`, `blogArchive`, `blogPagination`,
`other`, and `unclassified`. `locale` is a facet, not an extra denominator.
Only declared `document` routes enter the document page-set comparison;
derived routes and their counts/reasons remain visible as exclusions. A
`reason` on a document route explicitly excludes that route from the page set.
Without an oracle the report says `NotCompared`; empty missing/extra arrays
are not a pass. It also reports the analyzed source-tree hash, the oracle hash,
and a source Docusaurus version when declared or found in `package.json`.

Raw public-path equality stays the default. To additionally compare a
normalized document page set, opt in with `--compare-normalized-pages`. The
report lists every applied rule: declared source/target base paths are removed
only at segment boundaries, trailing slashes are normalized, path segments
are decoded once with strict UTF-8 and re-encoded after NFC normalization, and
comparison remains ordinal and case-sensitive. It does not case-fold or
recursively decode percent escapes. This comparison does not change exit codes.
A legacy string-array oracle leaves routes unclassified, so the normalized
page set stays `NotCompared`; use an object oracle with `document` kinds.
`--source-version` and `--source-base-path` can supply/override oracle metadata.

Exit codes are `0` for completed analysis/conversion, `1` for processing
failure, `2` for usage errors and `3` when files are unconvertible. The printed
JSON report keeps the existing fields and adds route-comparison scope and
component-change classifications. A route match covers only the declared
page set; it does not claim that the original site as a whole is equivalent.

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

The generator emits trailing-slash routes. The default comparison still uses
raw public paths; the optional page-set comparison can normalize the declared
slash and base-path differences. The compared page set must come from the
original build. Generated category indexes, blog index/author/archive/
pagination/tag routes and other surfaces have no 1:1 source documents; the
oracle classifies them separately and the report shows their exclusion count
and reason. A matching document page set is not a whole-site equivalence claim.

## Manual steps from the migrated sites

- Version-aware `UpgradeGuide` demos become static notes.
- `react-medium-image-zoom` imports are dropped; bare `<Zoom>` renders children.
- `LiteYouTubeEmbed` demos become YouTube links.
- `ColorModeToggle` demos become static notes.
- `raw-loader` source displays become static notes.
- Themed-image and inline-SVG live demos become static notes or images.
- Bare third-party imports are either installed in the target project or
  rewritten; `@docusaurus/useBaseUrl` call sites use static paths.

The report labels manual component changes as appearance changes, interaction
changes, staticization, deletion, or unverified. It records known losses (for
example, a YouTube link does not preserve embedded playback) as non-equivalent;
unverified components remain unverified and never become automatic actions.

## Third-party reproduction corpus

Three pinned upstream sites reproduce the full flow from a fixed manifest
(`eng/verification/1.1.0/migration-sites.json`): Prettier 3.6.2, Jest 30.2.0
and Docusaurus 3.10.2, all MIT, built in digest-pinned containers without host
profiles or credentials. Each site keeps its original route oracle, classified
exclusions with reasons, a bounded clean-page candidate build, and a loopback
serve check of representative pages, navigation, assets and the search index.
Manual patches are recorded, never silently applied. A route match covers only
the declared page set. Sanitized summaries, hashes, rerun commands and the
version manifest are published under `docs/evidence/1.1.0/`; full traces stay
local. See the [V110-21 memo](verification/1.1.0/tasks/V110-21.md).

## Unsupported inputs

Arbitrary plugins and themes, theme overrides outside the import list,
JavaScript configuration execution, `react-live` imports from code editors
and `useBaseUrl`/`useDocusaurusContext` exports have no LithoSharp
equivalent. The report flags them (`LSMIG001`/`LSMIG002`/`LSMIG003`) and the
build fails explicitly instead of rendering silently wrong output.
