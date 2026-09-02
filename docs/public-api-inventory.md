# Public API inventory and compatibility policy

This inventory records all types exported by the LithoSharp 0.2 assembly before
the planned architecture changes.

## Policy

- Existing API remains available until a replacement and migration path exist.
- `ObsoleteAttribute` will be added only when it can identify the replacement,
  show migration guidance, and name the intended removal version.
- Types marked as deprecation candidates below are not deprecated today. The
  label records that they expose implementation-level concerns which may move
  behind a higher-level API in a future major version.
- `SiteGenerator.GenerateAsync`, `MarkdownPostReader`, `SiteCustomization`, and
  `ISiteTemplate` are adapter-retained entry points. Architectural replacements
  must preserve these entry points through adapters until the documented
  deprecation policy permits removal.

## Adapter-retained entry points

| API | Current signature or role |
| --- | --- |
| `LithoSharp.SiteGenerator.GenerateAsync` | Legacy overload plus the overload that accepts `SiteGenerationOptions` before the cancellation token |
| `LithoSharp.Content.MarkdownPostReader` | Parameterless reader with `ReadAllAsync(string)` and `ReadAsync(string, string)` |
| `LithoSharp.SiteCustomization` | Parameterless record carrying text, theme, template, validators, extra pages, favicon source, and `llms.txt` selection |
| `LithoSharp.ISiteTemplate` | `Task<SiteTemplateResult> RenderAsync(SiteTemplateContext, CancellationToken = default)` |

## Compatibility surfaces

These types model the supported generation workflow, its inputs and results, or
documented extension points.

| Namespace | Exported type | Classification |
| --- | --- | --- |
| `LithoSharp` | `SiteGenerator` | Core compatibility surface; `GenerateAsync` is adapter-retained |
| `LithoSharp` | `SiteCustomization` | Adapter-retained customization surface |
| `LithoSharp` | `ISiteTemplate` | Adapter-retained template surface |
| `LithoSharp` | `DocsSiteTemplate` | Built-in template selection surface |
| `LithoSharp` | `BlogSiteTemplate` | Built-in legacy-template selection surface |
| `LithoSharp` | `SiteText` | Customization data contract |
| `LithoSharp` | `SiteThemeOptions` | Customization data contract |
| `LithoSharp` | `SiteExtraPage` | Extra-page compatibility contract |
| `LithoSharp` | `SiteGenerationOptions` | Per-run build timestamp, publication environment, and typed content registrations |
| `LithoSharp` | `SiteGenerationResult` | Generation result contract |
| `LithoSharp` | `SiteTemplateContext` | Template extension contract |
| `LithoSharp` | `SiteTemplateDocument` | Template extension contract |
| `LithoSharp` | `SiteTemplateFile` | Template extension contract |
| `LithoSharp` | `SiteTemplateHeading` | Template extension contract |
| `LithoSharp` | `SiteTemplateNavigationNode` | Template extension contract |
| `LithoSharp` | `SiteTemplatePage` | Template extension contract |
| `LithoSharp` | `SiteTemplatePageLink` | Template extension contract |
| `LithoSharp` | `SiteTemplateResult` | Template extension contract |
| `LithoSharp.Configuration` | `SiteSettings` | Site input contract |
| `LithoSharp.Content` | `MarkdownPostReader` | Adapter-retained content entry point |
| `LithoSharp.Content` | `MarkdownPost` | Parsed-content contract |
| `LithoSharp.Content` | `PostFrontMatter` | Front-matter contract |
| `LithoSharp.Content` | `PostSourceReference` | Front-matter source contract |
| `LithoSharp.Content` | `SiteContentCollection<TFrontMatter, TBody>` | Typed collection generation registration |
| `LithoSharp.Content` | `ContentPageRenderingContext` | Typed page rendering context |
| `LithoSharp.Content` | `ContentPageGroup<TFrontMatter, TBody>` | Published source group for `GeneratePages<TPageContent>` |
| `LithoSharp.Content` | `ContentPageGroupSelector<TFrontMatter, TBody>` | Generated-page grouping contract |
| `LithoSharp.Content` | `GeneratedPageFactory<TFrontMatter, TBody, TPageContent>` | Typed generated-page factory |
| `LithoSharp.Content` | `GeneratedPageRenderer<TPageContent>` | Typed generated-page renderer |
| `LithoSharp.Content` | `GeneratedPageDerivedSurfaces` | Aggregate-page inclusion policy for navigation, search, RSS, sitemap, social images, and `llms.txt` |
| `LithoSharp.Content` | `GeneratedPageDiagnosticIds` | Stable generated-page diagnostic identifiers |
| `LithoSharp.Diagnostics` | `SiteDiagnostic` | Structured generation diagnostic contract |
| `LithoSharp.Diagnostics` | `SiteDiagnosticSeverity` | Diagnostic severity contract |
| `LithoSharp.Diagnostics` | `SiteSourceLocation` | Optional diagnostic source location |
| `LithoSharp.Validation` | `IContentValidator` | Validation extension contract |
| `LithoSharp.Validation` | `ContentValidationContext` | Validation extension contract |
| `LithoSharp.Validation` | `RequiredSummaryValidator` | Built-in validator selection surface |
| `LithoSharp.Routing` | `SiteRoute` | Immutable canonical public/output route value |
| `LithoSharp.Routing` | `SiteRouteTable` | Whole-site route and artifact ownership validation |
| `LithoSharp.Routing` | `SiteRouteValidationResult` | Collected route diagnostic result |
| `LithoSharp.Routing` | `SiteRouteValidationException` | Invalid route table failure contract |
| `LithoSharp.Routing` | `SiteRouteDiagnosticIds` | Stable route diagnostic identifiers |

## Potential future deprecation candidates

These exported types remain supported in 0.2. They are candidates for a future
major-version migration because they expose low-level helpers, generated-output
DTOs, or a result type outside the primary workflow.

| Namespace | Exported type | Reason to reconsider after a replacement exists |
| --- | --- | --- |
| `LithoSharp` | `ContentValidationResult` | Not returned by the current validation entry point |
| `LithoSharp` | `Html` | Low-level encoding helper rather than a site model |
| `LithoSharp` | `SiteFormatting` | Rendering implementation helper tied to current formatting rules |
| `LithoSharp.Content` | `MarkdownDocumentParser` | Low-level front-matter splitting implementation |
| `LithoSharp.Content` | `MarkdownFrontMatterYaml` | Serialization implementation tied directly to YAML |
| `LithoSharp.Content` | `SlugHelper` | Routing implementation helper expected to be superseded by a route model |
| `LithoSharp.Search` | `SearchIndex` | DTO for the current built-in search artifact |
| `LithoSharp.Search` | `SearchDocument` | DTO for the current built-in search artifact |

The classification does not authorize a breaking change. Each candidate follows
the same replacement, adapter, `ObsoleteAttribute`, and major-version policy.

## Automated compatibility gate

`src/LithoSharp/PublicAPI.Shipped.txt` is the machine-readable 0.2.0 baseline.
The `Microsoft.CodeAnalysis.PublicApiAnalyzers` diagnostics for additions and
removals are treated as errors, so an intentional API change must update the
baseline in the same pull request. `PublicAPI.Unshipped.txt` is reserved for
the normal analyzer workflow while an API change is being reviewed.

CI also runs NuGet package validation against the published 0.2.0 package and
checks the packed library, XML documentation, README, icon, symbols package,
and dependency metadata. Local builds do not enable package validation by
default because that step needs to download the published baseline; the CI pack
command enables it explicitly.

New API under review is recorded in `PublicAPI.Unshipped.txt`; `SiteRoute` and
its factories remain there until a release promotes them to the shipped
baseline.

## Package boundary decision

F1C-06 reviewed the API and dependency graph. LithoSharp remains one physical
NuGet package for 0.2. Typed loaders, page models, routing, build planning,
templates, and publishing share the same generation transaction and have no
stable independent dependency or API boundary. Splitting them now would add
versioning, package-validation, and migration cost without reducing the
consumer dependency set. Reconsider a split only when a separately versioned
consumer scenario or a concrete dependency boundary appears.

`SiteGenerationOptions.EnvironmentName` defaults to `Production` and is matched
case-insensitively against publication front matter. `SiteGenerationResult.PostCount`
counts only Markdown posts that passed publication filtering and were supplied
to the selected template.
