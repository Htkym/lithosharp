# Typed YAML collections

`YamlContentCollectionLoader<TFrontMatter, TBody>` discovers `.yaml` and `.yml`
files recursively. Extension matching is case-insensitive; files are processed
in ordinal order by NFC-normalized relative path. Each file contains one mapping
or a sequence of mappings.

## Define and load a collection

The binder receives the whole mapping, including `id` and `body`. Declare these
fields in a strict schema even when the body factory also reads `body`:

```yaml
id: welcome
title: Welcome
body: |
  # Welcome
  This article is loaded from YAML.
```

```csharp
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

var loader = new YamlContentCollectionLoader<ArticleFrontMatter, string>(
    "content/articles",
    new ContentCollectionId("articles"),
    new ReflectionContentFrontMatterBinder<ArticleFrontMatter>(),
    values => (string)values["body"]!,
    entry => SiteRoute.ForFile($"articles/{entry.Id.Value}.html"),
    entry => new PageMetadata(entry.FrontMatter.Title));
var result = await loader.LoadAsync(cancellationToken);
if (!result.IsSuccess)
    throw new InvalidOperationException(string.Join(Environment.NewLine,
        result.Diagnostics.Select(diagnostic => $"{diagnostic.Id}: {diagnostic.Message}")));

public sealed class ArticleFrontMatter
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
}
```

The example assumes a `cancellationToken` supplied by the caller and YAML files
under `content/articles`. Register `result.Collection!` through
`SiteContentCollection<ArticleFrontMatter, string>` and
`SiteGenerationOptions.ContentCollections` to emit pages. See the
[typed Markdown integration example](typed-markdown-collections.md#site-generation-integration)
for the shared renderer API. This loader does not render or analyze `Body` as
Markdown automatically; the chosen renderer determines how the body is used.

## Identity, validation and failures

A non-empty `id` string is used as the stable entry ID after NFC normalization.
Without one, a single entry uses its normalized relative filename. Multiple
entries use that filename plus `#1`, `#2`, and so on. Use explicit IDs when entries
may be reordered or moved. Duplicate normalized entry IDs report `LSY003`.

The loader uses the same strict YAML parser as Markdown front matter. Duplicate
keys, anchors, aliases, null or complex keys, invalid roots, and invalid UTF-8 are
reported with source locations where available. Unknown fields and type or
required-member failures follow the [binder rules](typed-markdown-collections.md#yaml-rules).
Standalone YAML does not use Markdown's `---` front-matter envelope or its
header-only scan. It is not an MDX input.

The native syntax profile and explicit typed-input limits are shared with
[typed Markdown](typed-markdown-collections.md#yaml-rules). Both reflection and
generated binders receive decoded scalar values with nested source locations;
the body factory receives the same values. Standalone YAML roots must be a
mapping or a sequence of mappings. A scalar root or a non-mapping sequence item
is rejected by the collection loader.

Source fingerprints are `sha256:` followed by the lowercase SHA-256 of the exact
file bytes. Entries from one YAML sequence share the same file fingerprint.
Links/reparse points and normalized path collisions are rejected. Read failures
report `LSY002`, invalid UTF-8 reports `LSY001`, and discovery/path failures use
the shared `LSC` diagnostics. Check `IsSuccess` before accessing the collection;
content errors fail the collection load rather than publishing only valid entries.
Invalid API configuration and cancellation still throw.

For incremental generation, supply a stable `transformationId` before opting
into `isCacheable`, declare captured dependencies, and provide the renderer's
fingerprint. The same [cache rules](incremental-builds.md) apply to YAML and
Markdown collections.
