# Incremental builds

[日本語](incremental-builds.ja.md)

`SiteGenerationOptions.BuildCacheDirectory` controls the build cache. When it is null, LithoSharp uses a sibling `.lithosharp` directory next to the output and partitions entries by output identity. Keep this cache outside the output tree.

`MaxDegreeOfParallelism` defaults to `1`. Extensions are serial by default; a content collection must set `IsThreadSafe` before its pages can run in parallel. A renderer must also provide an explicit `RendererFingerprint` to be cacheable. LithoSharp combines that fingerprint with the renderer's actual assembly and method identity. Captured settings, templates, and other behavior that affect output must be included in the fingerprint or declared as build inputs.

Both the source collection's `IsCacheable` declaration and the renderer fingerprint are required. Use a stable `BuildTimestamp`, or `SOURCE_DATE_EPOCH`, for reproducible output: typed renderers can read the build time, so a different build time invalidates their pages even when their source has not changed. Do not declare a renderer cacheable if it reads untracked values such as `DateTime.UtcNow`. `clean: true` always reexecutes the build.

With `clean: false`, the cache is reusable only after node keys, artifact declarations, staged bytes, and any required rendered body pass verification. Corrupt, incomplete, or mismatched entries fall back to regeneration. The cache never authorizes deletion. An immutable cache record is saved first; its digest is published in the output manifest by the existing output transaction. A failed build leaves the previous output and its pointer intact. Non-no-op builds copy existing output into staging and verify files; a verified full no-op may verify the published tree in place and skip the staging copy.

`SiteBuildReport.Nodes` exposes each node's `CacheHit` and `CacheMissReason`; the report provides `CacheHitCount` and `CacheMissCount`. Generated and skipped artifact lists distinguish execution from reuse. `GeneratedFiles` still lists all published artifacts for compatibility. Legacy opaque templates execute eagerly without page caching. Social images fingerprint the selected fonts and SkiaSharp runtime; if that identity cannot be obtained, they are regenerated.

Asset transforms resolve fingerprinted routes before the page plan. Their own cache remains controlled by `AssetCacheDirectory`; null disables it. A transform that ran is reported as a miss even if its bytes match the previous output. Input validation still runs on transform cache hits. Optional cache write failures fall back to the current build's in-memory results; required output writes still fail the build and retain the old output. Cache records accumulate outside output; report them with `SiteGenerator.MeasureCache` (`lithosharp cache info -o <directory>`) and reclaim one output's partition with `SiteGenerator.ClearCache` (`lithosharp cache clean -o <directory>`). Both commands share the per-output build lock, so they do not inspect or remove a live partition. The MDX bridge cache also removes worker scratch and partial writes older than a day on the next preparation, so a killed host cannot leave them behind forever.

## Markdown analysis reuse

Persistent body-cache identity includes the Syntamark parser stamp, loaded
runtime module identity, `lithosharp-markdown/1` profile, effective body options,
and LithoSharp's compiler/rendering policy. Updating the parser cannot silently
reuse a body result from the previous implementation.

Within one runtime load/build, original body analysis can be retained only for
the identical text, source identity, parser/profile/options and requested parts.
Code inclusion or other body transformations require analysis of the resulting
body. A larger-budget original-body warning result is not a strict final-body
result. Input/resource failures remain error evidence and stop publication;
they are not successful empty parses. The compiler, editor and build are
separate hosts and do not share a cross-process AST.

Selecting only needed analysis facts reduces derived work; it is not incremental
syntax parsing. A header-only scan proves header boundaries, not body validity.
Likewise a reusable final-HTML facts record is not a cached site-quality verdict:
site-wide links and routes are checked against the current build. See
[typed Markdown loading](typed-markdown-collections.md#code-inclusions-and-body-analysis)
and [site quality checks](site-quality.md).

## Cacheable renderer example

Given a loaded `ContentCollection<ArticleFrontMatter, string>` named `articles` with `isCacheable: true`, and an `ArticleLayout` implementing `IPageLayout<ContentEntry<ArticleFrontMatter, string>>`:

```csharp
var collection = new SiteContentCollection<ArticleFrontMatter, string>(articles, new ArticleLayout())
{
    RendererFingerprint = "article-layout:v1", // Include captured configuration here.
    IsThreadSafe = true
};
var options = new SiteGenerationOptions
{
    ContentCollections = [collection],
    BuildTimestamp = DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
    MaxDegreeOfParallelism = 4
};
var result = await new SiteGenerator().GenerateWithOptionsAsync(
    site, posts, output, clean: false, customization, options, cancellationToken);
```
