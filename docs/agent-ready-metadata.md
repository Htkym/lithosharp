# Agent Ready metadata and discovery

Agent Ready is an opt-in group of publishing features. It does not promise that
an external agent will use a page, that a search engine will show a rich result,
or that a site will achieve a scanner score. `llms.txt` is a proposal, not an
access-control or indexing standard.

## Enable metadata explicitly

The library defaults do not generate JSON-LD or robots.txt. Enable the overall
feature and select each metadata policy explicitly:

```csharp
using LithoSharp;
using LithoSharp.AgentReady;

var customization = new SiteCustomization
{
    GenerateLlmsTxt = true,
    AgentReady = new AgentReadyOptions
    {
        Enabled = true,
        StructuredData = new AgentReadyStructuredDataOptions
        {
            Mode = AgentReadyStructuredDataMode.Automatic,
            Publisher = new AgentReadyIdentity
            {
                Kind = AgentReadyIdentityKind.Organization,
                Name = "Example documentation",
                Url = "https://example.com/"
            }
        },
        Robots = new AgentReadyRobotsOptions
        {
            Mode = AgentReadyRobotsMode.Generated
        }
    }
};
```

`Generated` robots management requires the site's base URL to be an origin root,
such as `https://example.com/`. Use `External` for a project site such as
`https://example.com/project/` when another system owns the origin's robots.txt.
`Disabled` emits no managed robots artifact. Changing this setting does not
change a host's root configuration.

## Discovery and llms.txt

A page that has a complete generated or explicit replacement Markdown artifact
receives a `rel="alternate" type="text/markdown"` head link. The href comes from
the registered Markdown route. No `.md` URL is guessed for an unsupported or
excluded page.

When an emitted llms.txt includes a page, that page can refer to the index using
`rel="describedby"`. Setting `GenerateLlmsTxt = false` disables the index and its
discovery links, even when Agent Ready is enabled. An llms.txt can live under a
site's base path. It does not have robots.txt's origin-root restriction.

The existing llms.txt generator prefers an available Markdown URL for an included
page, otherwise retaining the HTML URL. Index selection is separate from Markdown
availability: generating Markdown does not automatically include an unlisted or
NoIndex page in an index or override a derived-surface exclusion. Titles and
descriptions come from public metadata. Multiline values are flattened and
Markdown-significant text is escaped. The index never concatenates full bodies.

The HTML URL remains canonical. An alternate Markdown URL and a link to the
original page inside its body are not HTTP canonical declarations. No
`Vary: Accept`, HTTP Link header, or content negotiation is implied.

## Structured data policies

Choose `Disabled`, `Automatic`, or `UserManaged` at site level. A page's
`AgentReadyPageStructuredDataOptions.Mode` overrides the site policy when set.

Automatic generation emits a WebSite and a page subject. Select `WebPage`,
`TechArticle`, or `BlogPosting` to describe the actual content. The default is
WebPage; a technical title alone is not a reason to claim TechArticle.

```csharp
using LithoSharp.Pages;

var metadata = new PageMetadata("How to configure the parser", "Parser setup")
{
    Language = "en",
    AgentReady = new AgentReadyPageOptions
    {
        StructuredData = new AgentReadyPageStructuredDataOptions
        {
            Kind = AgentReadyPageKind.TechArticle,
            Author = new AgentReadyIdentity { Name = "Alex Example" },
            Published = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)
        }
    }
};
```

Authors, publishers, sameAs URLs, dates, and images are emitted only when explicit
public facts are available. Build time is never used as a publication or
modification date. No Person is invented for the home page. JSON is serialized
with HTML-safe escaping, so a metadata value containing `</script>` cannot close
the generated script element.

Use `UserManaged` when custom head content owns JSON-LD. In Automatic mode,
existing JSON-LD in the supplied custom head prevents an automatic duplicate and
produces an ownership diagnostic. Select one owner rather than combining two
competing descriptions of the same page. User-managed JSON is still checked for
parse errors, missing verifiable page subjects, page URL conflicts, and duplicate page subjects. This is not a full
Schema.org validator or verification of search-engine rich-result eligibility.

## Custom layouts

A custom layout can call the same typed head helper as the built-in layouts:

```csharp
var additions = AgentReadyMetadata.RenderHead(
    new AgentReadyHeadData
    {
        SiteTitle = "Example documentation",
        SiteUrl = "https://example.com/project/",
        SiteLanguage = "en",
        CanonicalUrl = "https://example.com/project/guide.html",
        Title = "Guide",
        Description = "An introduction to the public API",
        Language = "en",
        Page = new AgentReadyPageStructuredDataOptions
        {
            Kind = AgentReadyPageKind.TechArticle
        },
        MarkdownUrl = "https://example.com/project/guide.md",
        LlmsUrl = "https://example.com/project/llms.txt"
    },
    customization.AgentReady.StructuredData);
```

Insert the returned `IHtmlContent` inside the actual HTML head. Pass MarkdownUrl
only for a complete registered artifact, and LlmsUrl only for an emitted index
that includes this page. The generator's page plan remains authoritative. A
missing or incorrect link in the final custom layout is detected; computing the
head additions without rendering them is not sufficient.

`ExistingHead` can hold custom head additions for ownership inspection. It is not
included in the returned content, so the layout should render its existing head
content once. Public helper URL inputs must be absolute HTTP(S) URLs without
credentials, fragments, or control characters.

## robots.txt and sitemap

Generated robots.txt adds no crawler-specific or AI-training policy. It references
the existing canonical HTML sitemap by an absolute URL. Markdown copies are not
added to the sitemap. Explicit rules can be supplied as typed groups:

```csharp
Robots = new AgentReadyRobotsOptions
{
    Mode = AgentReadyRobotsMode.Generated,
    Groups =
    [
        new AgentReadyRobotsGroup
        {
            UserAgents = ["*"],
            Disallow = ["/private/"]
        }
    ]
}
```

An existing user-owned robots.txt is not silently overwritten or merged. Keep
external management or resolve the ownership conflict explicitly. Disallow rules
are not authorization, and NoIndex or Unlisted is not access control.

## What is verified

Generated-artifact checks inspect actual HTML discovery links, artifact existence,
canonical URL and language, and actual JSON-LD script contents. They distinguish
these checks from serving or production inspection:

- `LSQ013`: invalid, conflicting, or mismatched JSON-LD.
- `LSQ014`: discovery, canonical, language, or artifact-plan inconsistency.
- `LSQ015`: robots placement or sitemap-reference inconsistency.

Required Markdown availability, route collisions, and incorrect discovery cannot
be bypassed by disabling optional quality checks. The ordinary quality report
continues to handle existing missing SEO fields rather than inventing a second
missing-description rule.

A normal build performs no production HTTP checks. After explicitly publishing,
use [`check-output --http`](generated-output-checks.md#explicit-post-publication-http-inspection) or
verify the actual public HTML and Markdown URLs, redirects, content type, unknown
URL 404 behavior, and the origin's `/robots.txt`. Local serving should return
Markdown as `text/markdown; charset=utf-8`; production MIME types and headers are
the host's responsibility. A generated file alone does not prove the deployed
host's behavior.
