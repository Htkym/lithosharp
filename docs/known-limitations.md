# Known limitations

[日本語](known-limitations.ja.md)

These limits apply to 0.3.0. See [performance](performance.md) for
measurement conditions and [MDX](mdx.md) for configuration and execution rules.

## Build cost and platform coverage

The measured 10,000-page MDX corpus took 274.30 s cold, 105.13 s for a no-op and
213.39 s for one body edit. That edit compiled and rendered one module/page but
bundled 2,000 interactive entries. Across ten scenarios the observed simultaneous
process-tree working-set maximum was 11.807 GiB; this is not a minimum RAM
requirement or a single cold-build peak. A no-op still validates inputs and output.
`MdxOptions.Timeout` defaults to two minutes per request; this corpus used fifteen.

Performance measurements cover Windows only. Browser behavior is exercised in
Chromium; no equivalent Firefox or Safari verification is claimed. Static content
and ordinary links remain available without JavaScript, but interactive features
need the corresponding browser APIs. Trimming and Native AOT are unsupported for
the dynamically loaded CLI/site path; no trimmed or AOT deployment is certified.

PNG, JPEG and WebP were exercised with Skia. AVIF needs an explicitly configured
trusted `avifenc`; real AVIF encoding remains unverified in this environment.

## MDX and browser scope

Markdown-only sites need no Node.js. MDX uses Node.js 24.13.0 and the packaged
lockfile. Arbitrary Node versions and npm packages are not certified. Browser
imports must be browser-compatible and server imports must support build-time
execution. TypeScript is transpiled, not type-checked by the worker.

Selective hydration is opt-in. Explicit islands support load, idle, visible,
media and manual activation. Unknown interactive components, shared context or
unsafe boundaries fall back to page hydration. Static pages can omit the MDX
hydration entry while still loading optional documentation navigation/search code.
The measured static-page reduction does not apply to every island or fallback.

Only the documented Docusaurus aliases are implemented. Arbitrary Docusaurus
plugins and JavaScript configuration execution are unsupported. Migration reports
unsupported constructs; it is not a complete automated site conversion.

## Trust and publication

MDX, React modules, C# site code and compiler plugins execute with the build
account's permissions. HTML safety does not sandbox code execution. Do not build
untrusted MDX with credentials or private files available; use a separate
restricted environment. Import validation and the reduced worker environment are
not an operating-system sandbox. Trusted code can perform network or process work.

Normal MDX generation does not restore npm dependencies. `restore-mdx` explicitly
runs `npm ci --ignore-scripts --no-audit --no-fund` and needs network access unless
packages are cached. `dotnet restore` can also access configured feeds. Git metadata,
external-link checking, API builds, example tests and external AVIF encoding are
explicit options/commands, not automatic hidden build steps. External OpenAPI
references are diagnosed rather than fetched.

Publish only deliberately selected public DTO fields and JSON configuration.
`MdxPublicData` and island props require explicit schemas; never include secrets.
Algolia requires a public search-only key. `unlisted` and `noindex` are not access
control: their pages can still be requested directly.

Live code uses a sandboxed iframe with a limited React/render runtime; it is not a
general MDX/npm execution environment. Offline support is opt-in and needs HTTPS
or localhost. Deploy the whole revision together and retain older hashed assets
while clients can still reference them. Analytics is off by default and requires
an explicit consent event when using `DocumentationBrowserOptions.Analytics`.
That consent mechanism does not govern legacy Google Analytics snippets:
`SiteSettings.GoogleAnalyticsMeasurementId`, `GA_MEASUREMENT_ID` or
`GOOGLE_ANALYTICS_MEASUREMENT_ID` can enable those independently. Leave them unset
when relying on the documentation analytics consent flow.

Output staging and rollback protect cooperating same-user builds, not privileged
or hostile filesystem writers. Atomic rename support is required. Unix ACLs,
extended attributes and ownership are not portable preservation guarantees.
After-build observer failures occur after commit and cannot roll back publication.
