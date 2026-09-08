# Typed Markdown collections

`MarkdownContentCollectionLoader<TFrontMatter>` loads Markdown files into a
`ContentCollection<TFrontMatter, string>`. It is the typed replacement beneath
the compatibility `MarkdownPostReader`.

## Site generation integration

Register a loaded collection through
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
safe Markdown and document layout paths. JSON and CSV collections use the same
registration API.

For cacheable collections, set a stable `ContentTransformationId` and declare
every captured file or value dependency. Page build nodes include collection
and entry identity, source fingerprints, route, metadata, layout, rendered
content, relevant configuration, and resolved dependency fingerprints.
Missing declared files fail explicitly before output mutation. Social-image
paths use SHA-256 of the normalized output route, so changing a source file
without changing its route does not rename the asset.

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

- The input directory is searched recursively for `*.md`.
- Files are processed in ordinal order by their `/`-separated, NFC-normalized
  path relative to the input root.
- The relative path, including `.md`, is the `ContentEntryId`.
- Paths that become identical after NFC normalization are diagnosed instead of
  being selected by file-system enumeration order.
- `ContentEntry.Body` is the exact decoded text after the closing front-matter
  marker. It is not trimmed or rendered.
- `ContentEntry.SourceFingerprint` is `sha256:` followed by the lowercase
  SHA-256 of the exact file bytes, including a UTF-8 BOM and line endings.
- Markdown source must be valid UTF-8.
- Cancellation is checked before discovery, while enumerating and processing
  files, and before and after file I/O.

## Front-matter delimiters

The first logical line must be exactly `---`. A later logical line that is
exactly `---` closes the front matter. Empty front matter, a missing opening or
closing marker, and a non-mapping YAML root are diagnosed as content errors.

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
  `YamlMemberAttribute.Alias` overrides the generated name, and
  `YamlIgnoreAttribute` excludes a member.

`ReflectionContentFrontMatterBinder<TFrontMatter>` requires a public
parameterless constructor for reference types. A later source generator may
replace reflection without changing `IContentFrontMatterBinder<TFrontMatter>`.
Set `rejectUnknownFields` to `false` only for an intentionally extensible
schema.

## Diagnostics and failure behavior

Expected source errors are returned through `ContentLoadResult.Diagnostics`;
they do not throw. Every YAML or binding diagnostic includes the normalized
relative source path and, when available, a one-based line and column.
Configuration errors and file-system failures still throw their specific .NET
exceptions.

`MarkdownPostReader` converts typed-loader content diagnostics to
`InvalidOperationException` for compatibility. It retains its existing output
paths, body trimming, required title/date checks, and newest-first ordering.

## Example

```csharp
public sealed class ArticleFrontMatter
{
    public string Title { get; init; } = string.Empty;

    public DateOnly PublishedOn { get; init; }

    public List<string> Tags { get; init; } = [];
}

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
```
