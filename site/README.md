# LithoSharp website source

[日本語](README.ja.md)

This package consumer builds the English/Japanese 3.0 handbook, canonical public
guides, package/sample documentation and runtime-library API reference.
`LithoSharpVersion` defaults to the intended `3.0.0` package contract. It does not
assert formal package or site publication. The manual GitHub Pages workflow targets
https://htkym.github.io/lithosharp/; configuring the workflow does not establish
successful publication. Check that URL and the
[publication prerequisites](../eng/github-pages.md).

Use the SDK in `global.json`, Node for the lightweight declaration check, and
PowerShell 7. From the repository root:

```powershell
node site/validate-coverage.mjs
./site/Build.ps1 -LithoSharpVersion <available-version> -PackageFeed <candidate-feed> -Output <new-directory>
```

`PackageFeed` accepts an array when the selected Syntamark dependency pair is in a
separate feed. After formal package publication, omit it to use NuGet.org. The
script builds against NuGet artifacts and their imported generator metadata; it
does not reference implementation projects. Keep candidate feeds and package
caches outside the site source. Use a short `-WorkDirectory` on Windows. The
script checks planned path lengths before starting restore.

The script builds the consumer and mapped tests, runs generated-output quality
checks and verifies both source-inclusion fences against executed tests. It
neither deploys nor updates publication or translation-review baselines. Supply
`-BaseUrl` to check an explicitly selected host/subpath; `example.invalid` is the
reserved preview default. The output directory is updated transactionally by the
generator without a recursive source-tree cleanup.

`feature-coverage.json` links feature IDs to public entries, paired pages, examples
and limitations. Existing-sample and reference-only workflows have separate
verification. `documentation-inventory.json` captures each canonical English/Japanese
source, public API contract files, CLI usage, editor commands/settings, templates
and diagnostic IDs. `PublicDocumentation` projects those same manuscripts into the
site; they are not copied into a second maintained set of guides.

The API reference reads all exported public/protected declarations of the selected
Core, MDX, Images and Testing package assemblies. Matching compiler XML supplies
English explanations, and reviewed content-addressed units supply Japanese with
unchanged XML IDs and inline elements. Metadata is read passively without running
the selected library. Missing declaration-specific comments are explicit in both
views. External dependency cards are limited to referenced declarations. The
shared executable example also uses compiler XML and an explicit Japanese view.
The site uses Markdown rather than starting the optional MDX worker.

The build first exports fresh `english-units.json` and `public-symbols.json` under
its work directory. `validate-api-localization.mjs` rejects missing descriptions;
`validate-generated-site.mjs` then checks actual output coverage and both API
languages. When package documentation changes, translate and review its new units
before retrying. Never weaken this check to fall back to English on Japanese pages.

`translation-reviews.json` contains explicit reviewed byte hashes. Update these
only after reviewing both language files; the build deliberately leaves them
unchanged. `reference-translation-reviews.json` does the same for the canonical
repository sources; staging does not advance those records. After an explicit
source-pair review, `node site/update-documentation-inventory.mjs --write` captures
the current inventory. Japanese proofreading evidence belongs outside public input roots.
The source project is intentionally separate from the solution until matching
candidate packages are available. CI should call `Build.ps1` after all referenced
packages have been packed, preserve its diagnostics/output, and never deploy from
this validation step.
