# Known limitations

[日本語](known-limitations.ja.md)

These are the current limitations for the 2.1.3 source and 0.3.0 extension. Historical measurements retain their stated versions and do not establish performance for the current release. See [performance](performance.md) for measurement conditions and [MDX](mdx.md) for configuration and execution rules. The older 1.1 scope remains in [1.1 features](history/release-1.1.md).

## Markdown compatibility

The runtime uses the Litho compiler, with a tested subset of CommonMark 0.31.2
and GFM 0.29. It supports tables, task lists, strikethrough, autolinks,
admonitions, containers and code metadata. Math and diagrams need browser assets
provided by the template; `LIT002` identifies this requirement.

The Markdig extensions for footnotes, definition lists, abbreviations, citations,
figures, footers, media embeds, grid tables, generic attributes, alphabetical or
Roman list markers, subscript/superscript, inserted/marked text, emoji and smart
punctuation are not implemented. Unsupported syntax remains literal text;
footnotes additionally produce `LIT001`. Replace those constructs with supported
Markdown before upgrading. The compiler is not a complete CommonMark/GFM implementation.
An opt-in compatibility advisory reports high-confidence definition lists (`LIT003`),
generic attributes (`LIT004`), and grid tables (`LIT005`) without changing default
output: `DocumentInspectionOptions.EnableCompatibilityAdvisory` and
`lithosharp markdown-compat --advisory on` share the same scan. The shared scan
with the editor covers these advisories; full conformance to the official specifications is not claimed.

The verified Syntamark 1.2.0 runtime/Source pair supplies syntax and raw UTF-16
ranges. The default product profile remains `lithosharp-markdown/1`; upgrading
the component does not opt into a different CommonMark profile. LithoSharp still
owns YAML/C# binding, heading display and slug policy, routes, and diagnostic IDs.

| Analysis path | Input budget | What failure means |
| --- | --- | --- |
| Final analyzed post/collection body | 1 Mi UTF-16 code units | `LSM013` Error stops publication; unavailable facts are not validated. |
| Editor snapshot and original-body warning inspection | Up to 8 Mi UTF-16 code units | This larger budget does not establish that the transformed final body meets its stricter limit. |
| Header-only scan | 1 Mi UTF-16 code units for the header | The body after a closing delimiter is excluded. `Unknown` produces Generator `LSG002` or runtime `LSM013`, not a missing/unterminated-header conclusion. |

Other resource limits can also stop analysis. Render-only snippets do not use
the final-body analysis path. The generator checks explicit header inputs, not
body validity, and custom C# schemas are compiler/site-build checks rather than
per-keystroke editor execution. `NoContext` means project/schema/route evidence
is unavailable; `Partial` means analysis is incomplete; `Unknown` means a fact
could not be established. None of these statuses means the content passed validation. A definitive
typed-reference contradiction also needs a complete, current (`Closed`) catalog.
See the [Docs sample](../samples/LithoSharp.DocsSample/README.md) and
[analysis and cache contracts](migration-2.0.md#package-versions).

Markdown outlines use section subtree ranges and separate heading selection
ranges. MDX keeps its worker heading-based outline; the Markdown section-range
guarantee does not apply to MDX. Unclosed headers can recover as body text for
editing while retaining a diagnostic; normal loading/builds do not accept them
as valid front matter.

## Build cost and platform coverage

The 10,000-page MDX corpus measured with 0.3.0 took 274.30 s cold, 105.13 s for a no-op and
213.39 s for one body edit. That edit compiled and rendered one module/page but
bundled 2,000 interactive entries. Across ten scenarios the observed simultaneous
process-tree working-set maximum was 11.807 GiB; this is not a minimum RAM
requirement or a single cold-build peak. A no-op still validates inputs and output.
`MdxOptions.Timeout` defaults to two minutes per request; this corpus used fifteen.
These historical measurements are not current 2.1.3 benchmark results.

Performance measurements cover Windows only; the continuous build runs
Windows x64, Linux x64 and macOS arm64. Browser behavior is exercised in
Chromium; no equivalent Firefox or Safari verification is claimed. Static content
and ordinary links remain available without JavaScript, but interactive features
need the corresponding browser APIs. Trimming and Native AOT are unsupported for
the dynamically loaded CLI/site path; no trimmed or AOT deployment is certified.

PNG, JPEG and WebP were exercised with Skia. AVIF needs an explicitly configured
trusted `avifenc`; real AVIF encoding remains unverified in this environment.

1.1.0 did not meet its generation speed targets. A one-page edit still copies the
existing output tree into independent staging, so its cost can scale with total
output size. A verified full no-op may skip that copy, but this does not eliminate
the one-page-edit cost or establish a current speedup. See [incremental builds](incremental-builds.md)
and [the historical comparison](performance.md#110-performance-limitation).

## MDX and browser scope

The standard Docs/Blog palettes require CSS `light-dark()` support. Restoring
a saved theme uses an inline head script, which must be allowed by the host's CSP.
See [theme configuration](layout-css-contract.md#theme-switching--テーマ切り替え).

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

DOMPurify is updated to 3.4.16. With a DOM node input and `IN_PLACE: true`,
sanitization throws `TypeError` when DOMPurify records that its removal policy
removed the input root, including a hook that changes that policy to forbid the
root. In the observed case, 3.4.15 returned the removed node and 3.4.16 rejected
it. A hook that only detaches the root is outside this accepted difference.
Custom callers must handle the exception and discard the failed input/result;
do not reuse or serialize the removed node as sanitized content. This change is
retained for the [upstream XSS fix](https://github.com/cure53/DOMPurify/security/advisories/GHSA-6688-9rhm-gjv2).
The ordinary Flow/Class/State tooltip callers pass strings and do not enter this
DOM-node plus `IN_PLACE` branch. This limited compatibility difference is accepted;
complete DOM equality is not claimed.

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

## Deployment and migration scope

In Docusaurus migration, published routes use trailing slashes with directory
indexes; originals built with `trailingSlash: false` use flat files, so the
default route comparison uses raw public paths while the optional page-set
comparison normalizes the slash style. No `404.html` is emitted; unknown paths
fall back to the host's default 404 response. Docusaurus category indexes and
blog authors, archive, pagination and tag indexes have no 1:1 source documents
and stay outside the migration route scope; monthly archives and directory
indexes may appear as a documented superset.

Standard Markdown does not restrict link URL schemes; generated-output checks
report unsupported URLs as `LSQ001`. The built-in MDX `Link` rejects
`javascript:`, `vbscript:` and `data:` during build-time rendering, including
leading whitespace and case variations. Unresolved `xref:` links also fail
rendering. Ordinary links and fragments are supported; generated-output checks
report missing local targets as `LSQ001` and missing anchors as `LSQ002`.

## Editor and stability scope

Editor diagnostics run in stages: Markdown first, MDX through the restored worker.
Preview shows saved documents only; the worker transpiles TypeScript without
type-checking. Long-duration
Watch and Editor checks were not run for 1.1.0. Existing rapid-edit/recovery,
multi-OS CI and packaged-VSIX checks do not establish 30-minute continuous operation.
