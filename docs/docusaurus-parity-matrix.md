# Docusaurus parity audit

The tables below preserve the **starting audit** at `65ff20e`. Their unimplemented
labels describe that revision, not the current implementation. Current APIs and
usage are in [the English guide](mdx.md) and [the Japanese guide](mdx.ja.md).
The following evidence supersedes the starting implementation status. Measured
results and measurement limits are recorded in [verification](mdx-verification.md).

| Scope | Current implementation and executable evidence |
| --- | --- |
| 11A–11C, AT-02–12, AT-21 | Official worker, typed loader, SSR/hydration, schema, import/asset graph, persistent worker and atomic publication; `MdxIntegrationTests`, worker `compiler.test.mjs` and `eng/Test-MdxWatch.ps1` |
| 12, AT-13–14 | Built-in components, code regions, Prism, Mermaid/KaTeX, aliases and explicit compiler plugins; worker tests, production browser `check.js`, TSX/npm/partial/plugin fixture and migration tests |
| 13–14, AT-15–17 | `DocumentCatalog`, `DocumentationSite`, variants, sidebars/categories, unlisted, snapshots, shared UI messages, missing-document policy and Git updates; `DocumentationTests` covers two collections × three versions × two locales and subset retention |
| 15, AT-18 | Shared search schema, Japanese/API matching, sections, partitions and optional Algolia; `DocumentationTests`, local browser search and public search-only configuration |
| 16A, AT-19 | Static C# layout plus browser theme/navigation/history/root cleanup; production `check.js` and missing-API/chunk-recovery `failures.js` |
| 16B–16C, AT-20, AT-22 | Shared Docs/Blog/Pages graph, authors/listings/feeds, extension hooks, CLI and template; `MdxIntegrationTests`, `BuildExtensionTests`, `eng/Test-Templates.ps1` consumes separately packed C# and React extensions and compares CLI/API output |
| 17B, AT-27–32 | Page baseline, static omission, five island strategies, public props, shared chunks, lifecycle and measurement; worker tests, production browser suite and `measure.mjs` |
| 18, AT-25–26 | Sandboxed live React, offline revisions/retirement and consent-based analytics; `optional.js`, `analytics.js`, `offline-update.mjs` |
| 19 | XML/API IDs, overloads/generics, OpenAPI JSON, version differences and explicit project/example verification APIs; `DocumentationTests.ApiReferencesResolveOverloadsAndGenerateVersionDifferencesAndOpenApi` and `eng/Test-DocumentationVerification.ps1` verify real solution XML and passing/failing examples |
| Compatibility and distribution | 427 .NET tests, four worker tests, four packaged templates and seven package-content checks passed on each of the three CI operating systems. |
| AT-23 | Windows/Linux/macOS worker, reference, .NET, package, watch and browser jobs all passed at `ab3b92c` in [CI run 34208355089](https://github.com/Htkym/lithosharp/actions/runs/34208355089). |
| AT-24 | Completed 100/1,000/10,000-page Markdown comparisons and 30 MDX workloads, repeated small Markdown comparisons, process-tree RSS, five-run browser comparisons and deferred-island activation measurements; raw JSON, costs and limitations are in the verification record. |

## Starting audit

Audited 2026-09-08 against LithoSharp commit
`65ff20e0de6f4b8018064354e56c45fb984f89cd`.
The comparison target is Docusaurus **3.10.2**, with the exact configuration and
dependency lock under `tests/fixtures/mdx-baseline`. No v4 future flags are
enabled. MDX v1 compatibility is disabled explicitly. Compare visible text,
links, metadata, interactions and publication behavior, not HTML byte equality
between generators. Byte equality remains required within LithoSharp's
deterministic regression fixtures.

States: **充足** means the stated scope has existing evidence; **拡張必要** means
an existing implementation needs additional behavior or MDX integration;
**未実装** means the requested feature is absent; **対象外** means an explicit
non-goal. Partial reuse is never a passing MDX acceptance test.

## Implementation requirements

| Requirement IDs | State | Existing implementation / remaining work |
| --- | --- | --- |
| 10.1 | 充足 | Baseline JSON, package inventory, fixed-time sample comparison and recorded performance conditions; see baseline document for environment limits |
| 10.2 | 充足 | This audit covers all phases and acceptance IDs |
| 10.3–10.4 | 充足 | `mdx-architecture.md` fixes identity and execution contracts; variant implementation belongs to 14A |
| 10.5–10.6 | 充足 | Exact reference toolchain, lockfile, duplicate React detection, fixture and comparison policy; local validation and OS limits are recorded in baseline document |
| 11A.1, 11A.4, 11A.6 | 拡張必要 | `MarkdownContentCollectionLoader`, strict binder, `StaticContentGenerator`; add MDX body/loader, offsets and static metadata participation |
| 11A.2–11A.3, 11A.5 | 未実装 | Official MDX module compilation, import graph and structured MDX information in LithoSharp |
| 11B.1–11B.6 | 未実装 | React server rendering, hydration, client-only boundaries, public DTOs and mapped diagnostics |
| 11C.1–11C.7 | 拡張必要 | Reuse `ContentCollectionBuildPlanAdapter`, `AssetRegistry`, incremental cache, atomic output and `DevServer`; add worker/module/chunk integration |
| 12.1 | 拡張必要 | Existing Markdown conversion and Docs headings; add MDX GFM/anchors/partials/mapping |
| 12.2–12.8 | 未実装 | Full authoring components, highlighting/imports, Mermaid/math, compatibility aliases, extension whitelist and migration diagnostics |
| 13.1–13.5 | 拡張必要 | `DocsNavigationComponent`, `DocsSiteTemplate`, `Breadcrumbs`, typed collections; add categories, mixed sidebars, multiple Docs, Git metadata and stable navigation |
| 13.6 | 拡張必要 | `PagePublicationPolicy` and `GeneratedPageDerivedSurfaces`; add consistent unlisted discovery policy |
| 14A (all tasks) | 拡張必要 | Existing content IDs, `ContentRef`, `SiteRouteTable`, ownership transaction; implement variant resolver, switching and subset scope |
| 14B.1–14B.5 | 未実装 | Version snapshots, current/default versions, pinned/shared imports and missing-document fallback |
| 14C.1–14C.5 | 拡張必要 | `SiteSettings.Language`, `SiteText`, formatting; add translation catalogs, locale variants, fallback, RTL and alternate links |
| 15.1–15.3, 15.5–15.7 | 拡張必要 | `SearchIndex`, Blog search UI, RSS/sitemap/llms and derived content; add contextual schema, Japanese/API fixtures, partitioning and MDX text |
| 15.4 | 未実装 | Opt-in external search provider adapter |
| 16A.1–16A.2, 16A.5 | 拡張必要 | `SiteThemeOptions`, built-in components/layouts and theme script; complete responsive theme and shared persistence contract |
| 16A.3–16A.4, 16A.6 | 未実装 | Enhanced navigation, React root lifetime, history/head/focus/scroll and chunk failure fallback |
| 16B (all tasks) | 拡張必要 | Blog listings/tags/archive/RSS and typed generated pages; add MDX, multiple authors/blogs, excerpts, Atom and JSON Feed |
| 16C.1, 16C.3–16C.5 | 拡張必要 | `ISiteFactory`, CLI, templates, build reports; extend registration, MDX preset, restore/variant commands and inspect |
| 16C.2, 16C.6 | 未実装 | Node extension contract and Docusaurus dry-run migration |
| 17A (all tasks) | 拡張必要 | Existing deterministic, rollback, publication, API and package tests; add cross-feature MDX/browser/OS evidence |
| 17B.1–17B.12 | 未実装 | Island descriptors, five strategies, JS omission, shared chunks, lifecycle, fallbacks, inspect and measured comparison |
| 18.1–18.2 | 未実装 | Live-code execution and offline/service-worker consistency; favicon webmanifest alone does not satisfy PWA |
| 18.3 | 拡張必要 | Existing analytics insertion is not the requested provider/consent/navigation contract |
| 19.1–19.5 | 対象外 | Separate optional .NET differentiation milestone, not an R1–R3 gate; no implementation claimed |

## Phase 9 carry-forward

The preceding roadmap explicitly excluded phase 9 from its completed scope.
Existing features below predate that optional milestone; do not describe the
whole of phase 9 as adopted or complete.

| Optional feature | State | Follow-up |
| --- | --- | --- |
| Code syntax highlighting | 未実装 | 12.2 |
| Full-text search provider | 拡張必要 | Existing Blog search → 15 |
| RSS / Atom / JSON Feed | 拡張必要 | Existing RSS → 16B; Atom/JSON absent |
| Versioned documentation | 未実装 | 14A–14B |
| i18n / locale routing | 拡張必要 | Existing language/UI strings → 14C |
| Breadcrumbs / related pages / backlinks | 拡張必要 | Existing Breadcrumbs component → 13 |
| Git update/author information | 未実装 | 13.5 |
| Deployment presets | 対象外 | Not required by the follow-up releases; no hosting implementation |

## Acceptance evidence

The reference Counter fixture establishes the comparison input for AT-02 and
AT-05. It does not satisfy either test in LithoSharp. Browser actions must run
against production static output, including `/product/`, and must eventually
cover JavaScript-disabled rendering. Add fixtures as their phases are reached;
do not replace a missing fixture with a passing label.

| ID | State | Evidence or required fixture |
| --- | --- | --- |
| AT-01 | 拡張必要 | `CompatibilityContractTests`, `SiteTestHostTests` pass for Markdown; verify no worker startup after MDX integration exists |
| AT-02 | 未実装 | JSX/expressions/props/children/exports/MDX imports in LithoSharp; reference `docs/counter.mdx` |
| AT-03 | 未実装 | JS/TSX/npm/CSS/image execution and rendering fixture |
| AT-04 | 拡張必要 | Existing strict YAML tests; add MDX nullability/type/duplicate-key/original-position cases |
| AT-05 | 未実装 | Counter initial SSR value 3; click changes to 4 without browser hydration warnings |
| AT-06 | 拡張必要 | Existing static Markdown output; add no-JS MDX navigation and fallback browser checks |
| AT-07 | 未実装 | Client-only import touching window with static fallback |
| AT-08 | 拡張必要 | Existing incremental tests; add zero MDX no-op compile/render/bundle and clean/incremental hashes |
| AT-09 | 拡張必要 | Existing invalidation tests; add partial/component/CSS/image/config/lock changes |
| AT-10 | 拡張必要 | `SiteGeneratorAtomicOutputTests`; add worker crash/cancel/protocol/timeout failures |
| AT-11 | 拡張必要 | Existing ownership tests; add MDX delete/rename and user-edited/unowned preservation |
| AT-12 | 拡張必要 | Existing route/BaseUrl tests; add MDX Unicode/encoding/case/special-character references |
| AT-13 | 未実装 | Tabs synchronization, admonition/details/code/TOC/diagram/math fixtures |
| AT-14 | 未実装 | Alias/front-matter/partial migration and unsupported-feature diagnostics |
| AT-15 | 未実装 | Two collections × three versions × two locales, links/switching/search |
| AT-16 | 未実装 | Subset locale/version builds preserve outputs outside their scope |
| AT-17 | 拡張必要 | Existing publication tests; add all MDX discovery/derived surfaces |
| AT-18 | 未実装 | Fixed Japanese/API query expectations with section/version/locale |
| AT-19 | 未実装 | History/hash/focus/scroll/head/root lifecycle browser fixture |
| AT-20 | 拡張必要 | Existing Blog fixtures; add multiple authors, MDX and React Pages |
| AT-21 | 未実装 | Secret/path/server-only canary fixture across public artifacts |
| AT-22 | 拡張必要 | Existing external template/tool checks; add distributed MDX extensions |
| AT-23 | 拡張必要 | Local Windows Markdown evidence and Linux CI configuration; MDX OS/process tests absent |
| AT-24 | 拡張必要 | Existing fixed 100/1,000/10,000 corpus; MDX and browser costs unmeasured |
| AT-25 | 未実装 | Live code, offline and deployment-update consistency |
| AT-26 | 未実装 | Analytics opt-in/consent/no-double-pageview fixture |
| AT-27 | 未実装 | Static-only MDX produces no page-specific hydration/React entry |
| AT-28 | 未実装 | Counter island and shared-Context page fallback |
| AT-29 | 未実装 | load/idle/visible/media/manual, unavailable APIs and no-JS fixture |
| AT-30 | 未実装 | Shared runtime/chunk ownership and inspect fixture |
| AT-31 | 未実装 | Island navigation/cancel/chunk-failure lifecycle fixture |
| AT-32 | 未実装 | Same input, page versus selective hydration browser measurement |

## 開始時点の判定

`65ff20e`の時点ではR1・R2・R2H・R3はいずれも未完了であった。既存のMarkdown基盤を再利用する箇所と、
追加実装が必要な箇所を分けて記録した。MDXの必須要件を対象外に置き換えていない。
互換プリセットの対象はフェーズ12の明示した執筆APIであり、Docusaurusの内部APIや
任意のpluginをそのまま使えることは保証しない。
