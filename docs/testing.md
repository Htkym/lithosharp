# Testing sites

`LithoSharp.Testing` is a framework independent DOM testing API built on AngleSharp. It parses HTML without launching a browser, executing scripts, or making network requests.

```csharp
using LithoSharp;
using LithoSharp.Testing;

using var document = SiteTestDocument.Parse("<main><h1>Home</h1><p>&lt;safe&gt;</p><meta name='description' content='Description'></main>");
document.AssertElement("main");
document.AssertText("h1", "Home");
document.AssertText("p", "<safe>");
document.AssertMeta("description", "Description");
```

`AssertText` checks the first matching element's exact parsed `TextContent`; entities are decoded. `AssertAttribute`, `AssertMeta`, `AssertLink`, and `AssertImage` compare exact values, including quoted and multiline values. Missing or mismatched values throw `SiteTestException`. Null arguments, blank selectors, and invalid attribute names raise standard argument exceptions; invalid CSS selectors raise AngleSharp syntax exceptions. Empty HTML and expected text/attribute values are valid. A disposed document rejects further assertions and `Document` access.

For standalone rendering, create the existing contexts explicitly:

```csharp
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Pages;
using LithoSharp.Routing;
using LithoSharp.Testing;

var site = new SiteSettings { BaseUrl = "https://example.test/" };
var componentContext = ComponentRenderingContext.Create(site);
using var componentDocument = SiteTestDocument.RenderComponent(new HelloComponent(), "Home", componentContext);
var page = new SitePage<string>(new PageId("home"), SiteRoute.ForFile("index.html"), "Home", new PageMetadata("Home"));
using var layoutDocument = SiteTestDocument.RenderLayout(new HelloLayout(), page, PageRenderingContext.Create(site));
componentDocument.AssertText("h1", "Home");
layoutDocument.AssertText("main", "Home");

sealed class HelloComponent : ISiteComponent<string>
{
    public IHtmlContent Render(string value, ComponentRenderingContext context) => Html.UnsafeRaw($"<main><h1>{Html.Encode(value)}</h1></main>");
}
sealed class HelloLayout : IPageLayout<string>
{
    public IHtmlContent Render(SitePage<string> page, PageRenderingContext context) => Html.UnsafeRaw($"<main>{Html.Encode(page.Content)}</main>");
}
```

`SiteTestHost` runs a `SiteDefinition` or an `ISiteFactory` in isolated temporary output and cache directories. It preserves an explicit build timestamp and otherwise uses Unix epoch, overriding `SOURCE_DATE_EPOCH`. Local quality checks are enabled by default; explicit thresholds and orphan settings are honored. External links are checked only when configured. Enabled caches are redirected into the temporary tree; disabled asset caching remains disabled. A factory receives the real project root through `SiteFactoryContext.ProjectDirectory`. This example runs from the repository root in a test project referencing `LithoSharp.Testing` and the Docs sample:

```csharp
using LithoSharp;
using LithoSharp.Testing;

await using var host = await SiteTestHost.CreateAsync(
    new DocsSampleFactory(),
    new SiteFactoryContext(Path.GetFullPath("samples/LithoSharp.DocsSample")));
host.AssertSucceeded();
host.AssertNoDiagnostics();
host.AssertRoute("/index.html", "index.html");
host.AssertNoRoute("/index/");
host.AssertArtifact("index.html");
host.AssertNoArtifact("missing.html");
using var page = await host.OpenPageAsync("/index.html");
page.AssertElement("main");
```

A directory route is asserted with its trailing slash, while a file route uses its file path. The built-in samples record their home as `/index.html`, not a directory route. `AssertLink` compares the decoded `href` without resolving it; host quality validation checks resolved targets and fragments.

Route, build-plan and quality validation failures return `Succeeded == false`, `Result == null`, and diagnostics. For a deliberately broken internal link, use `host.AssertDiagnostic("LSQ001", LithoSharp.Diagnostics.SiteDiagnosticSeverity.Error)` instead of `AssertSucceeded`. A diagnostic assertion alone does not assert build success. Other factory/renderer/I/O exceptions and cancellation propagate after cleanup.

Disposal removes the temporary tree, including edited or extra files created within it. Sources are read in place; generated output/cache paths from the definition are not used for writes. Linked paths are rejected before reads/deletion; failed cleanup retains the remaining tree for inspection. Custom factories, renderers and transforms are trusted code and may perform their own I/O: the host is not a sandbox. The package adds neither an in-memory filesystem nor Playwright support.

| Assertion | Purpose |
|---|---|
| `AssertRoute` / `AssertNoRoute` | Verify the route manifest and file-vs-directory URL form |
| `AssertArtifact` / `AssertNoArtifact` | Check a declared regular file / absence from both declarations and disk |
| `AssertDiagnostic` / `AssertNoDiagnostics` | Check structured generation diagnostics / a minimum severity threshold |
| `OpenPageAsync` | Open a generated page as `SiteTestDocument` |
