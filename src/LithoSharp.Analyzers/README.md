# LithoSharp.Analyzers

Compiler diagnostics help .NET documentation teams keep C# content schemas and generated references consistent. Use version 2.2.0 alongside matching LithoSharp packages. The analyzer targets netstandard2.0 and Roslyn 4.14.0; it does not execute site factories, user getters or Core runtime code in the compiler host.

## Install and use

Add this reference to the consuming project's `ItemGroup`:

```xml
<PackageReference Include="LithoSharp.Analyzers" Version="2.2.0" PrivateAssets="all" />
```

Use [LithoSharp.Generators](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Generators/README.md) for rules requiring generated catalogs. IDE fixes are an optional, separate [LithoSharp.CodeFixes](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.CodeFixes/README.md) package. The [typed-content walkthrough](https://github.com/Htkym/lithosharp/blob/main/samples/LithoSharp.DocsSample/README.md#from-a-c-model-to-checked-output) connects schemas, references and output checks.

## Analysis scope

`Known` values are bounded compiler facts. `Closed` coverage belongs only to the lookup or owner described by a rule. `Deferred`, `Open`, `Stale` and `Unavailable` do not prove validity. Unknown values, unsupported expressions, user-defined lookalikes/conversions and exceeded limits remain unproven. Generated code is excluded.

Constants, supported conversions, string concatenation, unformatted string interpolation, single local assignments and finite branches may produce up to eight known values. Every value must violate the selected guard before an Error is reported. Mixed valid/invalid sets do not produce that Error. Properties, mutable/readonly fields, arbitrary calls, formatting, loops, ref escapes and exceptional control flow can prevent local-value analysis. A null-forgiving `!` preserves known facts but does not prove an unknown value.

Use normal Roslyn configuration, `.editorconfig`, `NoWarn` or original-source pragmas to configure individual diagnostics. Suppression does not remove runtime guards. A clean build does not validate the whole site: run generation and [output quality checks](https://github.com/Htkym/lithosharp/blob/main/docs/generated-output-checks.md) too. YAML/schema diagnostics remain owned by the Generator.

### LSA1001

Error: all known arguments to `SiteUrl.FromAbsolute(string)` are invalid. Use an absolute HTTP(S) URL without whitespace, control characters or backslashes.

### LSA1002

Error: known arguments violate registered route or URL guards. `SiteUrl.ForFile`, `SiteUrl.ForDirectory`, `SiteRoute.ForFile` and `SiteRoute.ForDirectoryIndex` check relative paths and optional base URLs. Avoid traversal, reserved device names, invalid percent/UTF-8 encoding and invalid bases. Empty directory routes are valid.

`SiteUrl.FromRoute`, `StaticSiteManifest.GetUrl(string)` and the route argument of `ContentRef<TEntry>` reject a definitely null argument. `ContentRef<TEntry>.GetUrl(baseUrl)` checks an explicit base URL: null or omission preserves the existing route; an empty/relative base or one containing a query or fragment is invalid. These checks do not establish receiver non-nullness, call reachability or route/catalog membership.

### LSA1003

Error: a known `SiteAsset` relative input/output path or `SiteAssetOutput` relative output path is invalid. These are filesystem names, so percent text is literal. Correct the relative path; input-root resolution, file existence and registry completeness require runtime checks.

### LSA1004

Error: known quality-option arguments violate their guards. `SiteQualityOptions.failureThreshold` must be a supported severity. `ExternalLinkCheckOptions` requires a nonblank cache path, nonnegative cache duration and bounded request interval/timeout. An interval may be zero; a timeout may not. The timer maximum is 42949672940000 ticks. Known null durations retain defaults. Supported `TimeSpan` values include constants, the ticks constructor and integral factories; fractional/nonfinite values, overflow, uncertain rounding and user factories remain Deferred.

### LSA1005

Error: every known `MaxDegreeOfParallelism` value is less than one in a directly constructed `SiteGenerationOptions` passed to `SiteGenerator.GenerateWithOptionsAsync`. Set it to at least one. Omitted defaults are valid; options aliases and `with` expressions are outside this subset.

### LSA1101

Info: the contents passed to `Html.UnsafeRaw(string)` cannot be determined statically. Review the raw output. This reports unverified content and does not assert a vulnerability.

### LSA1102

Error: an explicit `ToHtmlString()` result from a proven raw fragment appears in a known quoted HTML attribute. Use an appropriately encoded attribute value. Sealed `HtmlText` and `HtmlAttributeValue` encode their contents and are permitted there. An arbitrary `IHtmlContent` variable does not establish a raw fragment; direct interpolation of the raw object emits its type name rather than its rendered HTML.

### LSA1103

Error: interpolation appears in a known tag name, attribute name or unquoted attribute value. Add an appropriate quoted attribute or change the template manually. There is no automatic quote fix.

### LSA1104

Warning: `Html.Encode`, `HtmlText` or `HtmlAttributeValue` appears in known script/style text or a quoted event-handler/style attribute. HTML encoding does not establish JavaScript/CSS safety; choose appropriate serialization manually.

Context checks support direct and finite flow-composed interpolated strings reaching `Html.UnsafeRaw(string)`. Unknown holes, formatting/alignment, comments, malformed prefixes, foreign SVG/MathML and unsupported parsing modes can prevent later definite diagnostics. Direct templates allow 65,536 source/decoded UTF-16 units, 256 holes and 128 characters per tag/attribute name; composed shapes allow eight alternatives and 513 pieces each. Exceeding a limit leaves analysis Deferred.

### LSA1105

Error: statically known HTML has a base-independent URL violation under the runtime `LSQ001` rules: an empty resource URL, malformed explicit absolute scheme, unsupported absolute scheme or HTTP(S) userinfo. Correct the URL at the reported original C# location.

Supported inputs include normal/verbatim/raw literals, built-in string additions, source string constants, known locals/captures, finite branches and unformatted string-only interpolation. Parsing and mapping back to the original source must be complete for every value and origin. Cross-piece/ambiguous locations, incomplete parsing and exceeded limits remain Deferred. A direct literal expression admits at most 64 leaves; HTML input is bounded to 65,536 decoded UTF-16 units, 655,360 source units, 8,192 nodes and depth 128.

Checks cover supported unnamespaced resource attributes, accepted `srcset` candidate URLs and inline CSS URLs in active style attributes/elements, including top-level stylesheet `@import` strings. Inert template content is excluded. CSS parsing must also be complete. Relative/protocol-relative URLs, external stylesheets, base/canonical policy, site membership, anchors, image selection and general browser conformance require separate checks. No base URL or site graph is guessed.

### LSA1201

Error: every known path in `Guides.Manifest.GetUrl(path)` is absent from the same fresh Closed generated lookup. Opt into that lookup with `EmitStaticSiteManifest = true`, and use a declared exact public path or add the missing declaration.

Mixed present/missing values, unknown arguments, receiver aliases, custom factories, metadata-only manifests and ordinary catalogs are Deferred. Queries, fragments and URL resolution are outside this ordinal path lookup. A collection catalog does not establish site publication, anchor or asset coverage.

### LSA1202

Error: a fragment is definitely missing from another page in the same compiler-owned fixed HTML plan. Declare a top-level static partial class with `FixedHtmlSite`, and parameterless static `IHtmlContent` methods with `FixedHtmlPage`:

```csharp
using LithoSharp;
using LithoSharp.Content;

[FixedHtmlSite("https://example.com/")]
public static partial class Pages
{
    [FixedHtmlPage("source.html")]
    private static IHtmlContent Source() => Html.UnsafeRaw("<a href='/target.html#intro'>Intro</a>");

    [FixedHtmlPage("target.html")]
    private static IHtmlContent Target() => Html.UnsafeRaw("<h1 id='intro'>Intro</h1>");
}
// await Pages.FixedHtmlPlan.GenerateAsync("dist");
```

Both bodies must be fully mapped literal `Html.UnsafeRaw(string)` calls in a fresh plan. Supported links are unnamespaced `a`/`area` site-root hrefs with no query and a nonempty fragment. Decoded `id`/`a name` values compare ordinally; plus signs are not spaces. Add the target anchor or correct the link.

Other pages, missing targets, relative/external/fragment-only URLs, static constructors, stale/incomplete inputs, scripts, iframe/srcdoc and custom/foreign markup remain Deferred. At most 64 pages are supported. The plan owns its declared output; built-in output names, including `index.html`, must not collide. General `StaticSiteManifest.AnchorCoverage` remains Open. There is no CodeFix.

### LSA1203

Error: every known input/output instance is absent from its inline transform handler's declarations. Use the same `SiteAsset` instance in `SiteAssetTransform` inputs and its context's `GetUrl`/`OpenRead`; use the same `SiteAssetOutput` instance in outputs and `WriteAsync`. Equal IDs/paths on different instances are not membership.

The constructor must receive inline arrays/collection expressions and an inline handler. Unchanged local aliases are supported; shared arrays, reassignment, ref escapes, other closures, control-flow ambiguity, unknown/mixed membership and opaque handlers remain Deferred. Input and output scopes are independent. Limits include eight candidates, identity depth 64 and 4,096 owner syntax nodes. This checks declaration membership, not file existence, duplicate writes, unwritten outputs or whole-site assets.

### LSA1205

Info: the generated manifest's fingerprint differs from current compiler inputs. Rebuild generated catalogs after changing declarations or inputs. Route absence remains Deferred while the lookup is stale. Explicit static constructors, including those in another partial declaration, make the collection Unavailable. Unreadable inputs, unsupported references and exceeded limits also prevent definite absence. Fingerprinting supports at most 8,192 source/additional inputs and 8,388,608 framed text units.

### LSA1401

Info: a direct string literal in a verified fresh Closed `StaticSiteManifest.GetUrl(string)` has one equivalent generated `PageRef`. The optional CodeFix replaces only that argument with its fully qualified `Route.PublicPath`. It preserves the receiver, overload and runtime guard and revalidates current inputs. Ambiguous, stale, handwritten, side-effecting and unproved references receive no fix. See the [CodeFix guide](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.CodeFixes/README.md) for FixAll limits.

### LSA1402

Error: a known entry ID is paired with a different valid route in the same uniquely selected fresh Closed generated catalog. Select the catalog through `Catalog.CollectionId`, and use the declared route or generated reference. The route is the primary location and the entry ID is related.

Missing/unknown IDs, ambiguous scopes, handwritten/Open/Stale/Unavailable catalogs, selector aliases, non-null or unknown base URLs, dynamic routes and user conversions do not produce this Error. Invalid route syntax is `LSA1002`. This does not establish final publication, anchor or asset coverage. There is no CodeFix.

### LSA1403

Info: selected, registered documentation producer contributions require runtime preparation and cannot be enumerated statically. `SiteGenerationOptions.StaticRouteGuaranteeProducers` selects instances already registered in `Extensions`; it does not register or prepare them.

Both lists must be explicit arrays/collection expressions in directly constructed `SiteDefinition.Options`. Unchanged local identities/aliases are supported and repeats count once, with at most 64 items. The recognized Deferred producer is `LithoSharp.Mdx.DocumentationSite` with a preceding `AddCollection(new DocumentationCollection(...))` on that instance. A source producer whose exact `PrepareAsync` returns `Task.FromResult(new SiteBuildContribution(...))` with provably empty `ContentCollections` needs no Deferred item. Unregistered selections, getters/factories, ambiguity, other producer bodies and exceeded limits remain Unavailable. The count is not a page/route count, and missing Info does not imply Closed coverage. There is no CodeFix.

### LSA1501

Warning: a bound legacy `LithoSharp.Testing.SiteTestDocument.Document` getter returning `AngleSharp.Dom.IDocument` needs migration. Use the [Testing migration map](https://github.com/Htkym/lithosharp/blob/main/docs/Testing-v2-api.md#migration) to select `Snapshot.Query/QueryAll` or assertion helpers. Mutation, third-party casts/extensions and unsupported selectors require manual changes. Already-v2 snapshots, lookalikes and unresolved syntax are excluded. There is no automatic CodeFix.

### LSA9001

RequireStaticCoverage is disabled by default. Enable it with `dotnet_diagnostic.LSA9001.severity = error` in `.editorconfig` or a per-ID compilation option. It reports the scope and reason when a requested `StaticSiteManifest.GetUrl(string)` lookup or explicit `StaticRouteGuaranteeProducers` selection is Open, Stale, Unavailable or unknown. Fresh Closed lookups with known strings and proven empty producer selections need no policy diagnostic.

This is not a missing-reference assertion or a guarantee about arbitrary HTML, anchors, assets, dynamic producers or the whole site. Other diagnostics and runtime guards retain their separate scopes; no CodeFix is provided.
