# LithoSharp website source

[日本語](README.ja.md)

This package consumer builds the English/Japanese handbook, public guides,
package/sample documentation and runtime-library API reference.

## Build this branch

This branch uses new documentation layout and search APIs planned for 3.1.0.
Build matching candidate packages from this checkout before building the site.
The `LithoSharpVersion` default of `3.0.0` does not make this branch compatible
with the published 3.0.0 packages. Pass the candidate version explicitly.

This is the workflow for contributors to the official website. To create your own
site with the published packages, follow the [Quick Start](../docs/quickstart.md).

Use the SDK in `site/global.json`, Node for declaration checks and PowerShell 7.
The existing [build workflow](../.github/workflows/build.yml) packs all nine
packages in `eng/release-packages.json` before calling `site/Build.ps1`.
For a local build, use that same package set and the fixed Syntamark feed.
From the repository root:

```powershell
node site/validate-coverage.mjs
./site/Build.ps1 -LithoSharpVersion <candidate-version> -PackageFeed @('<candidate-feed>', './eng/markdown/feed') -Output <new-directory>
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

## Publish separately

Candidate builds never deploy. Publish this revision only after its matching stable
packages are available on NuGet.org (3.1.0 is the current release target).
Run the existing manual [publish-pages workflow](../.github/workflows/publish-pages.yml)
with the stable release tag at current main. Its default is validation only;
publication also requires its explicit publish option and any configured Pages environment approvals.
The destination is https://htkym.github.io/lithosharp/.
See [publication prerequisites](../eng/github-pages.md).

The website stays outside the library solution. Ordinary library builds do not
build or publish the site.
