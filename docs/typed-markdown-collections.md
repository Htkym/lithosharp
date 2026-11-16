# Typed Markdown collections

`MarkdownContentCollectionLoader<TFrontMatter>` loads Markdown files into a
`ContentCollection<TFrontMatter, string>`. It is the typed replacement beneath
the compatibility `MarkdownPostReader`.

For a complete C# schema → generated reference → Markdown → editor → site →
output-check walkthrough, use the [Docs sample](../samples/LithoSharp.DocsSample/README.md).
The [source-generator guide](source-generators.md) covers compiler inputs and
generated binders; this page covers runtime loading and generation.

For Docs presentation with the existing sidebar, TOC and previous/next links,
use [`RenderDocs`](docs-template.md#typed-content-and-the-docs-shell) on the loaded
collection. The LithoSharp 3.0.0 Docs template supplies this path;
see [Quick Start](quickstart.md) for matching compiler/runtime inputs and Quality.

## Site generation integration

Check `ContentLoadResult.IsSuccess` before using `Collection`, then register it through
`SiteGenerationOptions.ContentCollections`. The generator evaluates each
entry's route and publication mapper before rendering, validates all published
routes and output ownership, and only then starts the atomic output
transaction. The same published set is used for navigation, search, RSS,
sitemap, social images, and `llms.txt`; unpublished entries do not own routes
or derived artifacts.

```csharp
var registration = new SiteContentCollection<ArticleFrontMatter, string>(
    result.Collection!,
    (entry, context) => context.RenderDocument(
        $"<h1>{Html.Encode(entry.FrontMatter.Title)}</h1>" +
        context.RenderMarkdown(entry.Body)));

var generation = await new SiteGenerator().GenerateWithOptionsAsync(
    site,
    posts: [],
    "_site",
    clean: true,
    customization,
    new SiteGenerationOptions
    {
        BuildTimestamp = DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
        ContentCollections = [registration]
    },
    cancellationToken);
```

`ContentPageRenderingContext` exposes the normalized route, mapped metadata,
optional layout identity, resolved build timestamp and environment. Its
`RenderMarkdown` and `RenderDocument` helpers reuse the generator's existing
Markdown and document layout paths. Markdown link targets are not a security
allowlist: enable [site quality checks](site-quality.md) to diagnose unsupported
URL schemes in output. JSON, CSV and YAML collections use the same
registration API.

For cacheable collections, set a stable `ContentTransformationId` and declare
every captured file or value dependency. Page build nodes include collection
and entry identity, source fingerprints, route, metadata, layout, rendered
content, relevant configuration, and resolved dependency fingerprints.
Missing declared files fail explicitly before output mutation. Social-image
paths use SHA-256 of the normalized output route, so changing a source file
without changing its route does not rename the asset.

## Markdown profiles and static components

Set `SiteGenerationOptions.Markdown` to choose body interpretation. Front matter
continues to use native YAML and is separated before parsing the body.

```csharp
var markdown = new MarkdownOptions
{
    ProfileId = MarkdownOptions.DocumentationProfile,
    Components = new MarkdownComponentRegistry([
        MarkdownComponentRegistration.Create("version-badge",
            new VersionBadgeComponent(), GeneratedVersionBadges.Binder,
            "version-badge:v1")
    ])
};
```

The complete props model, generated binder declaration, and encoded HTML
component are in the [Docs sample](../samples/LithoSharp.DocsSample/MarkdownComponents.cs)
and its [models](../samples/LithoSharp.DocsSample/SampleModels.cs).
The sample registers them in `SiteGenerationOptions.Markdown` and runs without Node.js.

| Profile | Body behavior |
|---|---|
| `lithosharp-markdown/1` | Existing extension behavior and heading text policy; footnotes remain literal with diagnostics. This is the default. |
| `commonmark/0.31.2` | CommonMark interpretation without documentation extensions. HTML is encoded by the site renderer. |
| `syntamark-docs/2` | Documentation extensions, resolved footnotes with repeated-reference backlinks, and explicit static components. |

```markdown
:::component name="version-badge" version="3.0" stability="Stable"
:::
```

Components accept literal attribute values and an empty body. The `name`
attribute selects the registration; use a different `ContentName` for a props
member otherwise named `name`. Braces are ordinary string data. Required,
unknown, enum, and scalar props use the registered binder, and diagnostics retain
attribute locations. Ordinary containers such as `:::feature` keep their existing
behavior. Unregistered explicit components fail with `LSM020`; malformed
invocations use `LSM021`. Generated schema providers enable static editor checks
without executing binders or components. A custom binder without a schema is
reported as requiring a build (`LSM022`).

Declare every captured value, file, and registered asset consumed by a component.
File dependencies require an explicit `dependencyRoot`. Change
`configurationFingerprint` when captured configuration changes. Registrations,
implementation identities, schemas, declared inputs, and the selected profile
participate in generation cache keys.

For a document-reference prop, pass a common rule such as
`semanticRules: new([DocumentSemanticRule.Reference("target")])` to the registration.
Its literal document ID is resolved in the current collection, version, and locale.
`DocumentationSite` supplies the captured current document and reference universe;
components can read `context.Document` and `context.DocumentSemantics` to render
the resolved route. Missing, unpublished, and out-of-scope targets retain the
same structured diagnostics as document metadata rules. Unavailable context
remains deferred during editing and fails rendering with `LSM024`.

Changing a profile can change interpretation and heading anchors. Keep the last
published compatibility manifest, inspect the generated pages, and run
[`check-output` publication comparison](site-quality.md) before publishing the new output.
The selected profile also invalidates parser and output caches.

## Generated aggregate pages

Use `GeneratePages<TPageContent>` to create tag, year, category, or custom
indexes from the published entries of a collection. The selector returns one
or more string keys, the factory returns a normal `SitePage<TPageContent>`,
and the renderer uses the same `ContentPageRenderingContext` as entry pages.

```csharp
var tagPages = articles.GeneratePages(
    new ContentCollectionId("article-tags"),
    entry => entry.FrontMatter.Tags,
    group => new SitePage<TagIndex>(
        new PageId($"tag:{group.Key}"),
        SiteRoute.ForDirectoryIndex($"tags/{group.Key}"),
        new TagIndex(group.Key, group.Entries),
        new PageMetadata($"Articles tagged {group.Key}")),
    (page, context) => context.RenderDocument(
        RenderTagIndex(page.Content)),
    groupKeys: ["featured"],
    layoutId: new ContentLayoutId("tag-index:v1"),
    declaredDependencies:
    [
        ContentDependency.FromValue("tag-index-locale", "en"),
    ],
    transformationId: new ContentTransformationId("tag-index:v1"),
    isCacheable: true,
    derivedSurfaces: GeneratedPageDerivedSurfaces.Default
        | GeneratedPageDerivedSurfaces.Navigation);
```

Add `tagPages` to `SiteGenerationOptions.ContentCollections`. Register the
ordinary `SiteContentCollection<TFrontMatter, TBody>` separately when entry
pages should also be emitted.

Publication filtering happens before grouping. `groupKeys` can seed deliberate
empty groups. Keys are normalized to Unicode NFC, compared case-sensitively,
and ordered ordinally unless `groupOrderingComparer` is supplied; the entry
order remains the source collection's deterministic order. Canonically
equivalent keys from one entry are added once. Invalid keys report `LSG001`,
duplicate generated `PageId` values report `LSG002`, and route collisions use
the existing `SiteRouteTable` diagnostics.

`GeneratedPageDerivedSurfaces` controls the generator-owned aggregate
surfaces. The safe default includes search, sitemap, per-page social images,
and `llms.txt`, preserving the previous discoverability behavior. It excludes
global navigation, where a large tag or year set can overwhelm the menu, and
RSS, where aggregate pages can consume the 20-item feed budget. Combine flags
to opt in, use `All` for every supported surface, or use `None` to opt out of
all of them. Ordinary typed entry pages and legacy Markdown or extra pages keep
their existing behavior.

Generated-page build nodes include the selected surfaces along with the exact
source-entry membership and fingerprints, both source and generated
transformation identities, merged declared dependencies, route, metadata,
layout, and rendered output. Each derived artifact only fingerprints aggregate
pages selected for that surface. `GeneratePages` returns
`SiteContentCollection`, so generated pages cannot recursively become a source
collection.

## File discovery and identity

- The input directory is searched recursively for `.md` files, with
  case-insensitive extension matching. Files or directories whose names start
  with `_` are excluded from publication and front-matter binding. Markdown
  does not import those partials as document modules.
- Files are processed in ordinal order by their `/`-separated, NFC-normalized
  path relative to the input root.
- The relative path, including `.md`, is the `ContentEntryId`.
- Paths that become identical after NFC normalization are diagnosed instead of
  being selected by file-system enumeration order.
- `ContentEntry.Body` is the decoded text after the closing front-matter marker,
  with any declared code inclusions resolved. Without inclusions it is the exact
  suffix; the typed loader does not trim or render it.
- `ContentEntry.SourceFingerprint` is `sha256:` followed by the lowercase
  SHA-256 of the exact file bytes, including a UTF-8 BOM and line endings.
- Markdown source must be valid UTF-8.
- Links/reparse points and unsafe or colliding normalized paths are rejected.
- Cancellation is checked before discovery, while enumerating and processing
  files, and before and after file I/O.

## Front-matter delimiters

The first logical line must be exactly `---`. A later logical line that is
exactly `---` closes the front matter. Empty front matter, a missing opening or
closing marker, and a non-mapping YAML root are diagnosed as content errors.
A leading UTF-8 BOM is supported without losing original diagnostic positions.
The shared Syntamark header scan has a 1 Mi UTF-16 code-unit header budget;
the body after a confirmed closing marker does not consume that budget.
An unavailable (`Unknown`) boundary is `LSM013`, not proof that the header is
missing or unterminated. The compiler reports unavailable boundaries as `LSG002`.

## Code inclusions and body analysis

A fenced block can read a code file inside the content input root:

````markdown
```csharp source="_examples/Hello.cs" region="Greeting"
```
````

`source` is relative to the Markdown file; `region` is optional and selects a
named `#region`. Inclusion replaces the fence body, not its language or other
metadata. A missing/unreadable target reports `LSM011`; a missing or unterminated
region reports `LSM012`. Incomplete Markdown analysis uses `LSM013`, independently
of inclusion failures. Targets cannot escape the input root
or bypass its link/reparse-point checks.

The source fingerprint still hashes the original Markdown bytes. Target files
and resolved inclusion content contribute separate build dependencies, so a
code-only edit invalidates the page. Original-source warnings retain their
original positions; diagnostics on transformed bodies refer to that transformed
text, not a guessed source position after the splice.

The reader inspects the original body for warnings, using a budget up to 8 Mi
UTF-16 code units. Final analyzed post/collection bodies have the stricter 1 Mi
limit, including code brought in by inclusions. Limit failures report `LSM013` Error
and stop publication. Render-only Markdown snippets use a separate path, so
successfully rendering a snippet is not evidence that a full document passes
analysis. See [known limitations](known-limitations.md) and
[incremental builds](incremental-builds.md).

## YAML rules

Typed Markdown loading uses these deterministic rules:

- Mapping keys are case-sensitive strings.
- Duplicate keys are rejected at the second key.
- YAML anchors and aliases are always rejected. This avoids shared mutable
  graphs and alias expansion as an input-amplification mechanism.
- Unknown fields are rejected by the default reflection binder.
- YAML `null`, `~`, and an empty plain scalar map to null. Quoted values such as
  `"null"` remain strings.
- Null cannot be assigned to a non-nullable value or reference member.
- Missing members retain values established by the public parameterless
  constructor or member initializer. A `required` member must be present.
- Scalar conversion uses the invariant culture. Boolean values use .NET
  `true`/`false`; enum names are case-sensitive. Numeric, `Guid`, `DateTime`,
  `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, and `Uri` values are
  supported.
- Sequences bind to arrays and compatible `List<T>` abstractions. String-keyed
  mappings bind to compatible `Dictionary<string, T>` abstractions or nested
  front-matter objects.
- Property and field names use snake_case by default.
  `[ContentName("custom_name")]` overrides the generated name, and
  `[ContentIgnore]` excludes a member. Both attributes are in `LithoSharp.Content`.

YAML syntax comes from Syntamark's `native-yaml/1` profile. Typed runtime and
compiler inputs use the same explicit nesting limit of 64, with the native
defaults for other limits: 4 MiB UTF-8, 1 Mi UTF-16 code units, 131,072 nodes,
1 Mi UTF-16 code units per scalar, and 16,777,216 scan units. Unsupported
anchors, aliases, explicit tags, directives, complex keys and merge expansion
produce diagnostics. A literal `<<` string key is allowed. Partial or limited
parses are rejected before required-member or unknown-field validation.
Native scalar classification preserves quoted strings and never infers dates.
Typed binders explicitly convert decoded text to the declared CLR type, including
quoted boolean and numeric text; numeric formats, overflow and nonfinite values
follow the existing invariant .NET target conversion rules.

`ReflectionContentFrontMatterBinder<TFrontMatter>` requires a public
parameterless constructor for reference types. `LithoSharp.Generators` already
provides an `IContentFrontMatterBinder<TFrontMatter>` through the generated
`Binder` property. Pass it as `frontMatterBinder: GeneratedArticles.Binder`, as
the Docs sample does. Static checks cover explicitly registered compiler
inputs; runtime loading still checks the content it actually discovers.
Set `rejectUnknownFields` to `false` only for an intentionally extensible
schema.

### Reader, writer and attribute migration

Replace `[YamlMember(Alias = "custom_name")]` with
`[ContentName("custom_name")]` and `[YamlIgnore]` with `[ContentIgnore]`.
YamlDotNet attributes and its serializer configuration are no longer used.

`MarkdownFrontMatterYaml.Deserialize<T>` now accepts a DTO mapping supported by
`ReflectionContentFrontMatterBinder<T>`, with its public parameterless
constructor, snake_case names, required/nullability checks, and unknown-field
rejection. Replace camelCase YAML keys with snake_case or declare `ContentName`.
Scalar, sequence, dictionary and untyped `object` roots are not generic reader
targets; use Syntamark's `YamlParser` and native nodes for untyped documents.
Invalid or unsupported YAML and binding failures throw `FormatException`;
unsupported target types throw `NotSupportedException`. The dedicated
`Deserialize(string)` post reader retains its legacy unknown-field ignoring.

`Serialize(PostFrontMatter)` creates new YAML in a fixed order with LF line
endings and quotes strings, dates and numeric-looking text. Optional null
publication/sidebar values are omitted. This writer does not retain an existing
document's comments, spelling or bytes. For unedited standalone YAML, use
Syntamark `YamlParser.ParseUtf8` and `YamlDocument.GetOriginalUtf8`; keep the
original Markdown bytes separately when preserving its header and body together.
There is no general formatting-preserving edit API.

## Diagnostics and failure behavior

Expected source errors are returned through `ContentLoadResult.Diagnostics`;
they do not throw. Every YAML or binding diagnostic includes the normalized
relative source path and, when available, a one-based line and column.
Discovery, unsafe paths, read failures and invalid UTF-8 also produce content
diagnostics. Invalid API configuration and cancellation still throw; user
callbacks can also throw. A failed load has no usable collection, even when
some entries were individually valid.

The editor does not execute arbitrary C# constructors, getters, setters or
validators. After an explicit Build or Inspect acquires versioned generated
schema metadata, supported custom field, type, required, enum and literal
conditional checks run statically on the current native YAML snapshot.
Initializer-dependent requirements and custom code still require a build.
`FrontMatterSchemas.For<T>` reads metadata without constructing T or reading its
property values; `Required` describes explicit static requirements and
`RequiresBuildValidation` marks constraints that metadata cannot establish.
See [editor schema snapshots](language-server.md#markdown-symbols-and-unavailable-analysis).
`NoContext` means project-bound schema/route evidence
is unavailable; `Partial` means analysis is incomplete. Neither state establishes
that the content is valid. Typed-reference contradiction checks also need a
complete, current (`Closed`) catalog; dynamic routes remain unproven.

`MarkdownPostReader` converts typed-loader content diagnostics to
`InvalidOperationException` for compatibility. It retains its existing output
paths, body trimming, required title/date checks, and newest-first ordering.

## Example

This example assumes Markdown inputs under `content/articles` and a
`cancellationToken` supplied by the caller. A document needs at least
`title: Example` inside its front matter; `published_on` and `tags` are optional
for this schema.

```csharp
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

var loader = new MarkdownContentCollectionLoader<ArticleFrontMatter>(
    new ContentCollectionId("articles"),
    "content/articles",
    entry => SiteRoute.ForDirectoryIndex(entry.Id.Value[..^3]),
    entry => new PageMetadata(entry.FrontMatter.Title));

var result = await loader.LoadAsync(cancellationToken);
if (!result.IsSuccess)
{
    foreach (var diagnostic in result.Diagnostics)
    {
        Console.Error.WriteLine(
            $"{diagnostic.Location?.FilePath}:{diagnostic.Location?.Line}:{diagnostic.Location?.Column}: " +
            $"{diagnostic.Id}: {diagnostic.Message}");
    }
}

public sealed class ArticleFrontMatter
{
    public required string Title { get; init; }
    public DateOnly PublishedOn { get; init; }
    public List<string> Tags { get; init; } = [];
}
```
