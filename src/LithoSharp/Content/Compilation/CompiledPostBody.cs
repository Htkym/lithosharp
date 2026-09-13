using System.Collections.Concurrent;
using LithoSharp.Build;

namespace LithoSharp.Content.Compilation;

/// <summary>One-parse result for a legacy Markdown post body.</summary>
/// <param name="Html">Fully post-processed HTML (headings demoted, external links attributed).</param>
/// <param name="TocHeadings">Table-of-contents headings from the semantic model.</param>
/// <param name="PlainText">Plain text from the same parse, for search indexing.</param>
/// <param name="Links">Links and images from the same parse.</param>
/// <param name="Assets">Asset references from the same parse.</param>
internal sealed record CompiledPostBody(
    string Html,
    IReadOnlyList<SiteTemplateHeading> TocHeadings,
    string PlainText,
    IReadOnlyList<DocumentLink> Links,
    IReadOnlyList<DocumentAsset> Assets);

/// <summary>Per-build cache so post HTML, TOC, search text, and links share one parse.</summary>
internal sealed class CompiledPostBodyCache
{
    private readonly ConcurrentDictionary<MarkdownPost, Lazy<CompiledPostBody>> _cache =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _persistentLock = new();
    private (SiteBuildCache Cache, string CompilerFingerprint)? _persistent;

    /// <summary>Gets the compiled body, analyzing the post once per build (even in parallel).</summary>
    public CompiledPostBody GetOrAdd(
        Func<MarkdownPost, CompiledPostBody> compile,
        MarkdownPost post)
    {
        ArgumentNullException.ThrowIfNull(compile);
        ArgumentNullException.ThrowIfNull(post);
        return _cache.GetOrAdd(
            post,
            static (candidate, factory) => new Lazy<CompiledPostBody>(
                () => factory(candidate), LazyThreadSafetyMode.ExecutionAndPublication),
            compile).Value;
    }

    /// <summary>
    /// Attaches the cross-build parse cache. Reads and writes are keyed by compiler
    /// fingerprint plus source hash, so reuse is sound on every build kind.
    /// </summary>
    public void AttachPersistentCache(SiteBuildCache cache, string compilerFingerprint)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerFingerprint);
        lock (_persistentLock)
        {
            _persistent = (cache, compilerFingerprint);
        }
    }

    /// <summary>The attached cross-build parse cache, if any.</summary>
    internal (SiteBuildCache Cache, string CompilerFingerprint)? PersistentParseCache
    {
        get
        {
            lock (_persistentLock)
            {
                return _persistent;
            }
        }
    }
}
