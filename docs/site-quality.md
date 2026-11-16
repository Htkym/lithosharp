# Site quality checks

<a id="site-quality-checks--サイト品質検査"></a>
<a id="english"></a>
<a id="日本語"></a>

[日本語](site-quality.ja.md)


Quality checks run after artifacts are written to staging and before the output
transaction is committed. Enable them per generation with
`SiteGenerationOptions.Quality`; leaving it `null` performs no quality checks and
no network requests from the quality checker. A verified full no-op may inspect
the published tree in place without making a staging copy; current bytes and
site-wide rules are still checked.

These are checks of the generated site, after compiler and content-loading
checks. They can detect broken links or anchors introduced by a layout even
when the C# schema and typed references were valid. The
[Docs sample](../samples/LithoSharp.DocsSample/README.md#from-a-c-model-to-checked-output)
demonstrates the stages with separate invalid fixtures. To inspect files that
already exist without compiling a project or regenerating, use
[`check-output`](generated-output-checks.md).

```csharp
using LithoSharp.Diagnostics;
using LithoSharp.Quality;

var options = new SiteGenerationOptions
{
    Quality = new SiteQualityOptions(
        failureThreshold: SiteDiagnosticSeverity.Error,
        checkOrphans: true)
};

try
{
    var result = await generator.GenerateWithOptionsAsync(
        site, posts, output, clean: true, customization, options, cancellationToken);
    Console.WriteLine(result.QualityReport.Format(SiteDiagnosticFormat.Text));
}
catch (SiteQualityValidationException exception)
{
    Console.Error.WriteLine(exception.Report.Format(SiteDiagnosticFormat.Text));
    return 1;
}
```

The default failure threshold is `Error`. Set it to `Warning` to fail a build on
warnings as well. A failed check leaves the previously committed output intact.
Reports support `Text`, `Json`, and SARIF 2.1.0 through
`SiteQualityReport.Format`.

Final HTML includes declared `.html`/`.htm` assets and retained pages in subset
builds. Incomplete parser coverage, parser limits, cancellation, or bytes changing
after inspection stop publication. Previously published output remains available.

Local HTML observations are cached by content hash and parser/projection version
in the output's existing build-cache partition. References, anchors, canonical URLs,
and all site-wide rules are checked against the current build on every run. A cache
hit is not a cached quality verdict. Set
`SiteGenerationOptions.HtmlFactsCacheEnabled = false` to disable
this reuse. `HtmlParseCount` and `HtmlFactsCacheHitCount` on `QualityReport` count
final HTML parsing and reuse; both are zero when quality checks are disabled.

The cache admits at most 128 records and 32 MiB per output, with a 1 MiB record
limit. Pending records have the same bounds and are released after the build;
trees and cancellation tokens are not retained. Successful publication saves local
observations and evicts older records. Missing, corrupt, foreign-version, oversized,
or unavailable records cause a fresh parse. A no-op build parses no final HTML when
its complete working set remains in this cache; output bytes are still read and
verified. `SiteGenerator.ClearCache` clears this partition. Other build-cache data
keeps its existing retention policy.

External HTTP checks are off by default. Opt in with a cache path outside the
site output directory:

```csharp
Quality = new SiteQualityOptions(
    externalLinks: new ExternalLinkCheckOptions(
        cacheFilePath: Path.Combine(cacheDirectory, "external-links.json")))
```

The versioned cache lasts 24 hours by default. Requests are at least 500 ms apart
within the process and time out after 10 seconds; all three settings are configurable.
The checker follows at most ten redirects and retries HEAD with GET only for HTTP
405 or 501. Private, reserved, and transition addresses are rejected; IPv6 is limited
to global unicast. DNS results are checked before connecting directly to the selected
address. Cancellation stops the check. Cache corruption causes a fresh check, and an
unwritable cache does not discard the live diagnostics.

The checker validates internal URLs, fragments, canonical URLs, SEO fields,
duplicate titles, pages unreachable from the home page, unused registered
assets, and optional external HTTP links. It inspects HTML URL attributes and
literal `url(...)` values in CSS. Registered `.css` assets must be valid UTF-8;
invalid bytes abort generation before commit. Generated or otherwise dynamic
CSS URLs cannot be inferred and are not inspected.

Built-in layouts omit image metadata and use the `summary` Twitter card when no
social-image source is available. Explicit custom image URLs remain supported.
This corrects the earlier output's references to images that were never generated.

| ID | Severity | Rule |
| --- | --- | --- |
| `LSQ001` | Error | A URL is invalid, unsupported, empty where a resource is required, or points to a missing internal artifact. |
| `LSQ002` | Error | A fragment points to an anchor that does not exist. |
| `LSQ003` | Error | An HTML page does not have exactly one canonical URL for its public route. |
| `LSQ004` | Error | A redirect participates in a cycle. |
| `LSQ005` | Error | A redirect targets another redirect; target the final route directly. |
| `LSQ006` | Error | A redirect target does not exist. |
| `LSQ007` | Warning | A page is unreachable from the home page. |
| `LSQ008` | Warning | A registered asset has no HTML or CSS reference. |
| `LSQ009` | Warning | Multiple pages use the same non-empty title. |
| `LSQ010` | Warning | A required SEO field is missing or empty. |
| `LSQ011` | Warning | An optional external HTTP check failed. |

Redirects are owned build artifacts and participate in route collision checks.
Sources must be unique and must not collide with another output. Targets must
exist, and redirect chains and cycles are rejected even when quality checks are
disabled.

```csharp
using LithoSharp.Routing;

var options = new SiteGenerationOptions
{
    Redirects =
    [
        new SiteRedirect(
            SiteRoute.ForFile("old-home.html"),
            SiteRoute.ForDirectoryIndex(""))
    ]
};
```
