# Publish Agent Ready Markdown

[日本語](agent-ready.ja.md)

Agent Ready adds a public Markdown counterpart to eligible HTML pages. The HTML
URL remains canonical. Discovery links and the existing llms.txt use the same
publication plan as the generated files. The feature is opt-in and does not
guarantee external-agent use, search visibility, or scanner scores.

The [version matrix](../eng/version-matrix.json) distinguishes development candidates from published releases.

## Enable the feature

Enable Agent Ready in the site's existing customization. Select the Docs or Blog
template as usual; enabling Markdown does not select a template or start an MDX
worker.

```csharp
using LithoSharp;
using LithoSharp.AgentReady;

var customization = new SiteCustomization
{
    AgentReady = new AgentReadyOptions { Enabled = true },
    GenerateLlmsTxt = true
};
```

`Enabled` defaults to false. With the feature disabled, existing HTML publication
and legacy llms.txt selection remain unchanged. JSON-LD and robots generation are
separate opt-in settings. See [metadata and discovery](agent-ready-metadata.md)
for their configuration and custom-layout head helpers.

Setting `GenerateLlmsTxt = false` disables llms.txt and its describedby links,
including when Agent Ready is enabled. It does not disable Markdown provision.

## Select Markdown provision and index listing

Set `AgentReadyPageOptions` on `MarkdownPost.AgentReady`,
`SiteExtraPage.AgentReady`, or `PageMetadata.AgentReady` for a typed page.

- `Auto` generates Markdown only when a complete public body is available.
  Unsupported conversion leaves HTML available, omits the Markdown artifact and
  alternate link, and reports `LSQ012` with a reason.
- `Required` requires complete automatic conversion or an explicit replacement.
  Failure stops the build before publication, even when optional Quality checks
  are disabled.
- `Disabled` deliberately omits Markdown. It takes priority over a replacement
  and does not by itself remove a page from llms.txt.

HTML publication is evaluated first. Drafts and pages outside their publication
period or environment do not become public merely because Markdown is Required.
NoIndex and Unlisted pages are excluded from automatic Markdown provision and
index selection. Required or an explicit replacement can provide their Markdown;
index inclusion remains a separate choice. These settings are not access control.

```csharp
using LithoSharp.Pages;

var metadata = new PageMetadata("Internal-facing guide")
{
    NoIndex = true,
    AgentReady = new AgentReadyPageOptions
    {
        MarkdownMode = PublicMarkdownMode.Required,
        Indexing = AgentIndexMode.Exclude,
        MarkdownReplacement = "# Guide\n\nThe complete public counterpart."
    }
};
```

`AgentIndexMode.Auto` applies the new automatic index selection. `Include` can
explicitly include a NoIndex or Unlisted page, while `Exclude` omits it. Neither
mode overrides an applicable `GeneratedPageDerivedSurfaces.LlmsTxt` exclusion.
Generating Markdown does not change any other derived-surface flags. For an
included page without Markdown, llms.txt retains the HTML URL.

A replacement is the complete public body, not a summary or an extra paragraph.
A null replacement requests the normal provision path; an empty string is an
explicit empty body. Declare essential descriptions, warnings, and component
content in the replacement rather than silently dropping them.

## Routes, source text, and public metadata

The default route replaces the final HTML output extension with `.md`:
`guide.html` becomes `guide.md`, and `guide/index.html` becomes `guide/index.md`.
A directory-index HTML URL remains the page's canonical URL. A non-`.html` output
requires an explicit Markdown route; explicit routes must end in `.md`.

```csharp
using LithoSharp.Routing;

var provision = new AgentReadyPageOptions
{
    MarkdownMode = PublicMarkdownMode.Required,
    MarkdownRoute = SiteRoute.ForFile("readable/guide.md")
};
```

Conversion starts from the final body and its matching Syntamark analysis, not a
reverse conversion of rendered HTML. It preserves supported headings, code,
tables, quotes, references, and profile-specific content. HTML comments outside
code are removed. Code examples retain their literal content. Unknown HTML,
unsupported components, incomplete analysis, or unsafe source ranges do not
produce a successful partial Markdown body.

Links are resolved through registered public routes and assets. Source-document
links use the actual source-to-HTML mapping, including declared include origins;
fragments refer to the current HTML page. The resolver does not guess a public
route from a Markdown filename. Multi-source generated providers should use
public absolute or root-relative URLs when no single source origin is available.
URL resolution and Markdown destination escaping are separate steps.

If a provider changes an include-expanded body, its previous source map no longer
describes the complete text. Source-relative destinations are rejected in that
case rather than being rebased onto the page source. Absolute, root-relative,
query-only, and fragment-only destinations can still use the normal resolver.

Generated public front matter contains an allowlist: title, canonical HTML URL,
and available description and language. Source front matter, arbitrary custom
metadata, local paths, source maps, cache records, and provider exception details
are not copied into that header. A body beginning with `---` remains body content
after the generated header.

## Typed renderers and complete providers

A custom HTML renderer may add information that is absent from the source body.
Supply a corresponding complete Markdown provider rather than assuming the
source alone describes the rendered page. For an existing
`ContentCollection<DocumentFrontMatter, string> collection`, a body-only Docs
renderer can use:

```csharp
using LithoSharp.Content;
using LithoSharp.Documentation;

var pages = collection.RenderDocsWithMarkdown(
    entry => new DocsPagePresentation { Title = entry.FrontMatter.Title },
    (entry, context) => context.RenderMarkdown(entry.Body),
    (entry, context) => entry.Body,
    rendererFingerprint: "docs-public:v1",
    isThreadSafe: true);
```

Both delegates receive the prepared entry and context. Add `pages` to the site's
`ContentCollections` as with other typed collections. If the HTML renderer adds
download links, images, warnings, or explanatory text, include their meaningful
counterparts in the Markdown provider too. A plain `RenderDocs` registration
without a provider does not promise that its arbitrary HTML can be converted.

For other typed renderers, set `SiteContentCollection<TFrontMatter, TBody>.MarkdownProvider`.
Use `GeneratePagesWithMarkdown` for grouped C# pages; its provider receives the
same factory-produced `SitePage<TPageContent>` and context as the HTML renderer.
Returning null means unsupported. An empty string explicitly supplies an empty
body. Providers run only after publication and provision selection, never during
read-only inspection.

An explicitly registered static Markdown component can implement
`IPublicMarkdownComponent<TProps>`. Its HTML and Markdown methods receive the
same bound props. A missing complete alternative is unsupported; provider failure
fails Markdown provision without turning a successful HTML renderer into an
HTML error. Required still fails the build if no complete Markdown is available.

The [Docs sample](../samples/LithoSharp.DocsSample/SiteFactory.cs) shows typed Docs
and grouped-page providers, including links and images added by the HTML renderer.
Its [registered component](../samples/LithoSharp.DocsSample/MarkdownComponents.cs)
also supplies Markdown. The [Blog sample](../samples/LithoSharp.Sample/SiteFactory.cs)
enables Agent Ready on the existing Blog pipeline.

## Static MDX and explicit providers

With Agent Ready enabled, `MdxSite` can derive a limited static Markdown body
when using its default HTML renderer without an explicit provider. It consumes
the same worker AST used for HTML, preserves ordinary source text, and replaces
only known wrapper ranges. The candidate must match the original single-source
body snapshot, source hash, and worker implementation contract. A synthetic or
unverified body is not assumed complete. A custom C# HTML renderer requires its
own complete provider rather than automatically inheriting this candidate.

The supported flow wrappers and attributes are:

- `Admonition`: literal `type` and `title`; retains the kind, title, and body.
- `Details`: literal `summary` and `open`; includes the summary and full body,
  including content that is initially collapsed in HTML.
- `Tabs`: literal `defaultValue`, `groupId`, and `queryString`; includes every
  `TabItem` body, not just the selected tab. `TabItem` accepts literal `value`,
  `label`, and `default`, with its label or value kept as a heading.
- Known `note`, `tip`, `info`, `warning`, `danger`, and `caution` directives;
  retains the kind, optional label, and body.

These components may use their fixed `@theme/Admonition`, `@theme/Details`,
`@theme/Tabs`, and `@theme/TabItem` default imports, or named exports from
`@lithosharp/runtime`, including safe uppercase aliases. Attributes must use
supported literal strings or boolean-presence syntax rather than expressions.

```mdx
import Details from '@theme/Details';

<Details summary="Important background">

This explanation remains in public Markdown even when initially collapsed.

</Details>
```

Ordinary Markdown links, images, reference definitions, code, and math retain
their source form before the normal public-URL conversion. MDX comments outside
code are removed while literal code examples remain. Unknown JSX, executable
expressions, user imports or exports, compiler plugins, `ComponentsModule`
overrides, source-changing `mdx-code-block` preprocessing, wrappers inside lists
or blockquotes, and `Tabs values={...}` are unsupported. `StaticComponents` is
not a declaration that an arbitrary component has a complete Markdown meaning.
Auto keeps the HTML page and reports why Markdown is not provided. Required
needs a complete alternative or fails.

`MdxSite.AddCollectionWithMarkdown` and `MdxBlogSite.AddCollectionWithMarkdown`
accept explicit complete providers, which take priority over a static candidate.
They receive the prepared MDX entry used by the HTML renderer. An excerpt, feed
text, extracted plain text, or disabled hydration is not proof of a complete
Markdown counterpart. Keep an explicit replacement or provider when the limited
static rules cannot establish the full public meaning. See [MDX sites](mdx.md)
for the existing trusted-build-code and worker boundaries; static conversion does
not make MDX execution a sandbox.

## Inspect provision outcomes

After generation, inspect `SiteGenerationResult.AgentReadyPages`. Each
`PublicMarkdownPageResult` exposes `HtmlPath`, optional `MarkdownPath`, `Status`,
and a sanitized `Reason` when provision was unavailable. Paths are output paths,
not source filenames. With Agent Ready disabled, this collection is empty.

```csharp
foreach (var page in result.AgentReadyPages)
{
    Console.WriteLine($"{page.HtmlPath}: {page.Status} -> {page.MarkdownPath}");
    if (page.Reason is not null)
        Console.WriteLine(page.Reason);
}
```

`Automatic` and `ExplicitReplacement` are complete provision outcomes.
`IntentionallyExcluded` is a policy choice. `Unsupported`, `Unavailable`, and
`Failed` report distinct unsuccessful outcomes with a reason. The enum also
reserves `NotRequested` for work that was not requested. These results describe
generation, not an HTTP delivery check. Required failures throw rather than
returning a successfully published result.

## Cache reuse and declared dependencies

Public Markdown is a formal build artifact with an availability outcome. Verified
cache entries replay both complete bodies and unsupported or failed outcomes.
Corrupt records, invalid UTF-8 bodies, and mismatched hashes are cache misses;
cancellation does not become a successful result. Warm unchanged builds reuse
compatible outcomes instead of reparsing the original body for public conversion.

The key includes the body/source identity, analysis/compiler profile, public route
and asset catalog, page metadata, and captured renderer dependencies. Registered
component context also includes text, theme, and build time. Typed providers use
the collection's existing captured inputs.

For reusable custom rendering, set a stable `rendererFingerprint`, version the
transformation identifier when its rules change, and declare every external file,
asset, and captured value used by either renderer. A fingerprint is a declaration,
not automatic discovery of arbitrary closure state. Missing cache eligibility
causes rendering to run again. See [incremental builds](incremental-builds.md).

## Partial builds, removal, and disabling

`OutputScope` selects artifact paths. Selecting only `guide.html` does not select
`guide.md`. A change that affects the pair requires both old and new relevant
HTML/Markdown paths, or an enclosing directory. This also applies to removing or
unpublishing a previously generated page. If the index would change, include
`llms.txt` or run a full build. Inconsistent scope is rejected before output
writes rather than silently expanding the requested scope.

```csharp
var options = new SiteGenerationOptions
{
    OutputScope = ["guide.html", "guide.md", "llms.txt"]
};
```

Unchanged artifacts outside scope retain the existing partial-build behavior.
Global Agent Ready, robots, or index-policy changes require a full build. Disable
Agent Ready with a full build to remove stale owned Markdown and discovery links;
user-owned files remain protected by the existing ownership and collision checks.
Failures and cancellation retain the previously published output through the
normal output transaction. See [build graph and ownership](build-graph.md).

## Quality and serving checks

Inspect `SiteGenerationResult.QualityReport` and `BuildReport.Diagnostics`.
`LSQ012` distinguishes unavailable Auto provision from deliberate exclusion and
participates in the existing Quality failure threshold. For example, a Warning
threshold can fail an Auto build without treating unsupported Markdown as complete.

Required provision, route collisions, scope violations, incorrect discovery,
canonical/language mismatches, and managed robots/sitemap inconsistencies remain
mandatory checks even with `Quality = null`. Optional JSON-LD diagnostics
(`LSQ013`) follow the opt-in Quality path. Metadata generation remains separately
configurable. See [metadata checks](agent-ready-metadata.md) and
[site quality](site-quality.md).

A successful build verifies generated files, actual head links, and planned
public-artifact bytes. It does not prove local or production HTTP behavior.
Local serving uses `text/markdown; charset=utf-8` for `.md`. Production MIME,
redirects, unknown-URL 404 behavior, and origin-root robots require explicit
HTTP inspection. Follow [generated output checks](generated-output-checks.md),
including the opt-in `check-output --http` command. No production HTTP check or
release is performed by enabling Agent Ready.
