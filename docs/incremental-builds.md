# Incremental builds

`SiteGenerationOptions.BuildCacheDirectory` controls the build cache. When it is null, LithoSharp uses a sibling `.lithosharp` directory next to the output and partitions entries by output identity. Keep this cache outside the output tree.

`MaxDegreeOfParallelism` defaults to `1`. Extensions are serial by default; a content collection must set `IsThreadSafe` before its pages can run in parallel. A renderer must also provide an explicit `RendererFingerprint` to be cacheable. LithoSharp combines that fingerprint with the renderer's actual assembly and method identity. Captured settings, templates, and other behavior that affect output must be included in the fingerprint or declared as build inputs.

Both the source collection's `IsCacheable` declaration and the renderer fingerprint are required. Use a stable `BuildTimestamp`, or `SOURCE_DATE_EPOCH`, for reproducible output: typed renderers can read the build time, so a different build time invalidates their pages even when their source has not changed. Do not declare a renderer cacheable if it reads untracked values such as `DateTime.UtcNow`. `clean: true` always reexecutes the build.

With `clean: false`, the cache is reusable only after node keys, artifact declarations, staged bytes, and any required rendered body pass verification. Corrupt, incomplete, or mismatched entries fall back to regeneration. The cache never authorizes deletion. An immutable cache record is saved first; its digest is published in the output manifest by the existing output transaction. A failed build leaves the previous output and its pointer intact. The transaction still copies existing output into staging and verifies files, even on a cache hit.

`SiteBuildReport.Nodes` exposes each node's `CacheHit` and `CacheMissReason`; the report provides `CacheHitCount` and `CacheMissCount`. Generated and skipped artifact lists distinguish execution from reuse. `GeneratedFiles` still lists all published artifacts for compatibility. Legacy opaque templates execute eagerly without page caching. Social images fingerprint the selected fonts and SkiaSharp runtime; if that identity cannot be obtained, they are regenerated.

Asset transforms resolve fingerprinted routes before the page plan. Their own cache remains controlled by `AssetCacheDirectory`; null disables it. A transform that ran is reported as a miss even if its bytes match the previous output. Input validation still runs on transform cache hits. Cache write failures abort the build and retain the old output. Cache records accumulate outside output; remove that output's cache partition when no build is running if disk reclamation is needed.

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
