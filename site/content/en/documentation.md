---
title: Documentation, languages and search
supported_version: "3.0"
prerequisites: [quickstart]
---

`DocumentationSite` registers a `handbook` collection with two `DocumentVariant`
inputs: `3.0/en` under `en/`, and `3.0/ja` under `ja/`. Shared document IDs connect
language targets; an explicit `MissingDocumentPolicy.Error` rejects missing pairs.
Adding a version requires its own source input and route prefix. A label does not
turn current text into archived documentation.

`DocsSiteTemplate` and `DocsPageLayout` supply navigation and the page structure.
`Browser.Search = true` enables local partitions for the current collection,
version and locale. Search data derives from the final published page bodies,
including rendered component and included-source text. It does not expose draft
content merely because the input was discovered. No Algolia or analytics endpoint
is registered by this site.

Use the language control to visit the same document in Japanese. Use the sidebar,
table of contents and normal links to navigate; the published HTML remains usable
without progressive JavaScript navigation. Check search and keyboard behavior in
the generated output as part of accessibility review.

`translation-reviews.json` records the exact reviewed English and Japanese byte
hashes. `DocumentTranslationReview` consumes those records; the build never
refreshes them. A later source or translation edit makes the record stale until
another explicit review. These records do not attest to technical correctness.
