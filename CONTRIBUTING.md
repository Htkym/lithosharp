# Contributing to LithoSharp

LithoSharp is a static site and documentation generator for .NET.
Contributions of all kinds are welcome: bug reports,
documentation, tests, and code.

## Getting started

Use the .NET 10 SDK selected by [`global.json`](global.json) and Node.js 24.13.0
for the full MDX test suite. Markdown-only library consumers do not need Node.

```powershell
dotnet restore LithoSharp.slnx --locked-mode
npm ci --prefix src/LithoSharp.Mdx/worker --ignore-scripts --no-audit --no-fund
dotnet build LithoSharp.slnx --no-restore -c Release
dotnet test --solution LithoSharp.slnx --no-build -c Release
dotnet run --project samples/LithoSharp.DocsSample -c Release --no-build -- --output .local/docs-site
dotnet run --project samples/LithoSharp.Sample -c Release --no-build -- --output .local/blog-site
```

Running the sample is a quick end-to-end check: it depends only on the published
library surface, so it confirms the generator works without any private code.

For Docs template layout or navigation changes, use the existing Playwright
dependencies in `tests/fixtures/mdx-browser`. Serve the generated sample with
`node tests/fixtures/mdx-browser/serve.mjs .local/docs-site 4328`, then run
`node tests/fixtures/mdx-browser/docs.mjs http://127.0.0.1:4328` in another terminal.
This checks desktop and mobile overflow in both color schemes, system preferences,
saved theme choices, blocked storage, menu keyboard behavior, the skip link,
and navigation with JavaScript disabled.

For the Blog template, serve `.local/blog-site` on port 4329 with the same server,
then run `node tests/fixtures/mdx-browser/blog.mjs http://127.0.0.1:4329`.
This also checks listings, archives, tags, article pages, and search submission.

README screenshots live in `docs/images`. Capture the generated Docs page
`/posts/getting-started/installation.html` in light mode and the Blog home page in
dark mode with Chromium at 1440 × 960, device scale 1 and reduced motion enabled.
Use a fresh browser context without a saved theme; wait for fonts before capturing.
Keep these two documentation images, not the generated sites, in version control.

## Conventions

- **Local working files.** Keep plans, audit notes, measurement results and agent
  artifacts under the ignored `.local/` directory. Commit reusable tests and
  benchmark tools, not their execution records or generated output.
- **Lock files.** `RestorePackagesWithLockFile` is enabled. When you change a
  dependency, restore and commit the updated `packages.lock.json` files.
- **EditorConfig.** Formatting is defined in [`.editorconfig`](.editorconfig)
  (LF line endings, UTF-8, final newline). Most editors apply it automatically.
- **Public API docs.** `GenerateDocumentationFile` is on. Write XML doc comments in
  English on public types and members so IntelliSense stays in English.
- **Tests.** Add or update tests under `tests/LithoSharp.Tests` for behavior changes.
  Tests use TUnit on the Microsoft.Testing.Platform runner.

## Output-byte stability

Deterministic builds should retain bytes for the same declared inputs, but output
serialization across releases is not a compatibility guarantee. Changes to
templates, encoding or line endings still require review. When generation changes,
run the samples and compare routes, semantic HTML and artifacts against the
[compatibility contract](docs/compatibility-contract.md).

## Release preparation

Keep package versions, templates and lockfiles aligned. Move reviewed API and
analyzer baselines into their shipped files for the release. Core package
validation retains the published NuGet 0.2.0 baseline for 0.3.1. Run
`eng/Validate-Package.ps1` for each of the seven shipping packages and
`eng/Test-Templates.ps1` against an isolated package directory. The build workflow
covers Windows, Linux and macOS; a configured workflow is not proof of a passed run.

Update the README, changelog, migration guide and limitations from actual
verification results. Keep release drafts and readiness records under `.local/`.
Publishing requires separate approval; the tag workflow checks all package versions
against the tag before packaging and publishing them.

## Pull requests

1. Fork the repository and create a topic branch.
2. Keep changes focused; one logical change per pull request is easiest to review.
3. Make sure `build`, `test`, and the sample run succeed locally.
4. Describe what changed and why, and call out any change to generated output.

By contributing, you agree that your contributions are licensed under the
[MIT License](LICENSE).
