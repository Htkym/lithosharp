---
title: Feature inventory
supported_version: "3.0"
prerequisites: [introduction]
sidebar_position: 3
---

| Feature | Guide and executable source | Conditions |
|---|---|---|
| Typed Markdown and native YAML | [Typed content](../typed-content/), `WebsiteModels.cs` | Declared DTO and input root; supported YAML subset |
| Generated schemas, catalogs and references | [Typed content](../typed-content/), generated `GeneratedExamples` | Rebuild after membership changes; catalog is not publication proof |
| Conditional and document reference rules | [Typed content](../typed-content/), `WebsiteFactory.cs` | Unknown or incomplete catalog evidence remains deferred |
| Markdown profiles and C# components | [Profiles and components](../profiles/), `VersionBadge` | Explicit profile and registration; literal attributes only |
| Docs layout, routes and scoped search | [Docs and languages](../documentation/), handbook collection | Collection/version/locale identity; no external search service enabled |
| Translation revisions and snippets | [Verification](../operations/), `checks/` | Explicit reviewed hashes and mapped trusted tests |
| OKF bundles and preserved bytes | [Knowledge](../knowledge/), `knowledge/` | Separate conformance, freshness and trust policies |
| API reference | [API documentation](../api/), selected package metadata and compiler XML | Same exported IDs and translated descriptions; absent XML is explicit |
| Core assets, optional PNG and social images | [Images](../images/), `website-social` transform | Explicit dimensions and declared transforms; PNG codec limits |
| CLI, editor and LSP | [Tooling](../tooling/) | Trusted build/inspection acquired separately from typing |
| Build graph, diagnostics and publication | [Operations](../operations/) | Validation success and publication success are separate evidence |
| Templates, MDX, migration and testing | [Extended workflows](../extended/) | Additional packages, trusted Node setup or explicit fixtures as documented |

The repository's
[machine-readable coverage map](https://github.com/Htkym/lithosharp/blob/main/site/feature-coverage.json)
links stable feature IDs to public entry points, both language pages, source
examples and limitations. It distinguishes examples executed by this site from
workflows demonstrated by the existing samples. Listing a feature does not assert
that every possible configuration was tested.

The [detailed guide index](../../reference/en/README/) reuses the repository's
canonical guides and package documentation. Each paired source has an explicit
reviewed revision. The API inventory is generated from the selected packages;
CLI commands, editor settings, templates, diagnostics and public API contract files
are captured by the source inventory so changes require an explicit review.
