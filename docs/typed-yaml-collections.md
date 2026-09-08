# Typed YAML collections

`YamlContentCollectionLoader<TFrontMatter, TBody>` discovers `.yaml` and `.yml`
files recursively in deterministic, case-insensitive extension order. Each file
contains one object or a sequence of objects. An `id` string is used as the
stable entry ID; otherwise the normalized relative path (and sequence index) is
used.

```csharp
var loader = new YamlContentCollectionLoader<ArticleFrontMatter, string>(
    "content/articles",
    new ContentCollectionId("articles"),
    new ReflectionContentFrontMatterBinder<ArticleFrontMatter>(),
    values => (string)values["body"]!,
    entry => SiteRoute.ForFile($"articles/{entry.Id.Value}.html"),
    entry => new PageMetadata(entry.FrontMatter.Title));
var result = await loader.LoadAsync(cancellationToken);
```

The loader uses the same strict YAML parser as Markdown front matter. Duplicate
keys, anchors, aliases, non-string keys, invalid roots, and invalid UTF-8 are
reported with source locations. Fingerprints are SHA-256 hashes of the exact
source bytes.
