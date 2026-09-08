# Changelog

[日本語](CHANGELOG.ja.md)

## 0.3.0 — release candidate

### Added

- Typed Markdown, YAML, JSON and CSV collections, routes, references, schemas and
  source generation with build-time diagnostics.
- Dependency graphs, persistent caches, invalidation reports, artifact ownership,
  staged output transactions, quality checks, redirects and fingerprinted assets.
- Optional image transformations, .NET testing APIs, CLI/site factories and four
  project templates: Docs, Blog, empty C# layout and MDX.
- Optional MDX 3 and React with build-time HTML, page hydration, explicit islands
  and selective activation strategies.
- Versioned and multilingual Docs, multiple collections, Blog/Pages, local search,
  supported Docusaurus aliases, XML/OpenAPI reference pages and exact xrefs.
- Progressive browser navigation, opt-in offline support, sandboxed live code and
  consent-controlled analytics.

### Changed

- Publication conditions now filter legacy posts before rendering and derived
  artifact generation. Output routes and ownership receive stricter validation.
- Missing social images no longer produce dangling image metadata. Text output
  normalization and content-based search fingerprints improve reproducibility.
- Core adds AngleSharp 1.7.3. Six optional package IDs join `LithoSharp`; existing
  Markdown applications do not need to adopt them.

### Performance

- The measured static-page fixture reduced JS from 449,585 to 15,817 bytes and
  hydration roots from one to zero with selective hydration.
- Markdown 100/1,000-page repeated medians stayed within 10% of the benchmark
  baseline for time, allocation and resident-memory increases.
- Large MDX incremental rendering can still bundle many interactive entries.
  These are conditional Windows measurements, not competitor comparisons.
  See [performance](docs/performance.md).

### Compatibility and migration

No incompatible public signatures or new obsolete warnings were found against
the actual NuGet 0.2.0. The isolated legacy consumer passed source and binary
upgrade checks for Docs, Blog and custom templates. Behavioral changes affect
previously ignored publication keys, unsafe base URLs and exact diagnostic text;
serialized output bytes also change. Follow the
[0.2.0 migration guide](docs/migration-0.3.md), including rollback instructions.

0.3.0 targets .NET 10. MDX alone requires Node.js 24.13.0 and explicit restore;
its lockfile pins MDX 3.1.1, React 19.2.4 and esbuild 0.25.12. Future 0.x minor
releases may change public APIs. See [Known limitations](docs/known-limitations.md)
for trust boundaries, large-site costs, browser coverage, AOT and AVIF status.

## 0.2.0

Published Core package providing Markdown-based Docs and Blog output, front
matter, custom templates and shared site customization. It remains available as
the compatibility baseline for 0.3.0.
