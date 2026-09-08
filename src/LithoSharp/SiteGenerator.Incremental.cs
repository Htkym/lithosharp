using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Content;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    private sealed record BuildExecutionResult(string CacheKey, IReadOnlyList<SiteBuildReportNode> Nodes);

    private Dictionary<string, Func<string?, string>> CreatePlannedTemplateRenders(SiteTemplateContext context, ISiteTemplate template)
    {
        var configuration = context.Configuration;
        var routes = configuration.Routes;
        var posts = context.Posts;
        var renders = new Dictionary<string, Func<string?, string>>(StringComparer.Ordinal);
        if (template is DocsSiteTemplate)
        {
            var orderedPosts = FlattenDocsNavigation(BuildDocsNavigation(posts)).ToArray();
            renders.Add(routes.SiteCss.RelativeOutputPath, _ => BuildDocsCss(configuration));
            renders.Add(routes.SiteScript.RelativeOutputPath, _ => BuildDocsScript());
            renders.Add(routes.Home.RelativeOutputPath, _ => RenderDocsIndex(configuration, context.Navigation, orderedPosts));
            foreach (var post in orderedPosts)
                renders.Add(routes.Post(post).RelativeOutputPath, _ => RenderDocsPost(configuration, context.Navigation, orderedPosts, post));
            foreach (var page in configuration.ExtraPages)
                renders.Add(routes.ExtraPage(page).RelativeOutputPath, _ => RenderDocsExtraPage(configuration, context.Navigation, page));
            if (template is DocsSiteTemplate { EnableSearch: true })
            {
                renders.Add(routes.SearchScript.RelativeOutputPath, _ => BuildSearchScript(configuration.Text, contextual: true));
                renders.Add(routes.SearchIndex.RelativeOutputPath, _ => BuildSearchIndex(configuration, posts, configuration.ContentPages));
                renders.Add(routes.SearchPage.RelativeOutputPath, hash => RenderSearch(configuration, hash!));
                renders.Add(routes.Sitemap.RelativeOutputPath, _ => RenderSitemap(configuration, posts, configuration.ContentPages, docs: true));
            }
        }
        else
        {
            renders.Add(routes.SiteCss.RelativeOutputPath, _ => BuildCss(configuration));
            renders.Add(routes.SiteScript.RelativeOutputPath, _ => BuildSiteScript());
            renders.Add(routes.SearchScript.RelativeOutputPath, _ => BuildSearchScript(configuration.Text));
            renders.Add(routes.SearchIndex.RelativeOutputPath, _ => BuildSearchIndex(configuration, posts, configuration.ContentPages));
            renders.Add(routes.Home.RelativeOutputPath, _ => RenderIndex(configuration, posts));
            renders.Add(routes.Archives.RelativeOutputPath, _ => RenderArchives(configuration, posts));
            renders.Add(routes.Tags.RelativeOutputPath, _ => RenderTags(configuration, posts));
            renders.Add(routes.SearchPage.RelativeOutputPath, hash => RenderSearch(configuration, hash!));
            foreach (var post in posts)
                renders.Add(routes.Post(post).RelativeOutputPath, _ => RenderPost(configuration, post));
            foreach (var page in configuration.ExtraPages)
                renders.Add(routes.ExtraPage(page).RelativeOutputPath, _ => RenderExtraPage(configuration, page));
            renders.Add(routes.Feed.RelativeOutputPath, _ => RenderFeed(configuration, posts, configuration.ContentPages));
            renders.Add(routes.Sitemap.RelativeOutputPath, _ => RenderSitemap(configuration, posts, configuration.ContentPages));
        }
        return renders;
    }

    private async Task<BuildExecutionResult> ExecuteBuildAsync(
        SiteBuildPlan plan, RenderContext configuration, SiteTemplateContext context,
        IReadOnlyDictionary<string, Func<string?, string>> textRenders,
        IReadOnlyList<IntegratedContentPage> pages, AssetRegistry assets, IReadOnlyList<SiteTemplateFile> redirects,
        OutputTransaction transaction, string cacheRoot, bool clean, int parallelism, bool subset, CancellationToken cancellationToken)
    {
        var cache = new SiteBuildCache(cacheRoot, transaction.OutputIdentity);
        var previous = await cache.LoadAsync(clean ? null : await transaction.ReadPreviousBuildCacheKeyAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        var completed = new Dictionary<string, CachedBuildNode>(StringComparer.Ordinal);
        var reports = new Dictionary<string, SiteBuildReportNode>(StringComparer.Ordinal);
        var pagesByNode = pages.ToDictionary(page => page.OwnerId, StringComparer.Ordinal);
        var renders = new Dictionary<string, Func<CancellationToken, Task<byte[]>>>(StringComparer.Ordinal);
        foreach (var pair in textRenders)
            renders.Add(pair.Key, _ => Task.FromResult(GetTextContentBytes(pair.Value(
                completed.TryGetValue("index:search", out var search) ? search.Artifacts.SingleOrDefault()?.Sha256 : null))));
        foreach (var page in pages)
            renders.Add(page.Route.RelativeOutputPath, token => Task.FromResult(GetTextContentBytes(
                page.Render(this, configuration, context.EnvironmentName, assets, token).Content)));
        foreach (var file in redirects)
            renders.Add(file.RelativePath, _ => Task.FromResult(GetTextContentBytes(file.Content)));
        foreach (var file in assets.Files)
            renders.Add(file.RelativePath, _ => Task.FromResult(file.Bytes));
        var routes = configuration.Routes;
        if (configuration.HasFaviconAssets)
        {
            foreach (var fileName in BundledFaviconAssets)
                renders.Add(routes.Favicon(fileName).RelativeOutputPath, async token =>
                {
                    await using var source = BuildInputFingerprint.OpenVerifiedContainedRead(configuration.FaviconSourceDirectory,
                        Path.Combine(configuration.FaviconSourceDirectory, fileName), asynchronous: true);
                    using var buffer = new MemoryStream();
                    await source.CopyToAsync(buffer, token).ConfigureAwait(false);
                    return buffer.ToArray();
                });
            renders.Add(routes.WebManifest.RelativeOutputPath, _ => Task.FromResult(GetTextContentBytes(BuildWebManifest(configuration))));
        }
        if (configuration.HasSocialImage)
        {
            renders.Add(routes.DefaultSocialImage.RelativeOutputPath, token => BuildDefaultSocialImageAsync(configuration, token));
            foreach (var post in context.Posts)
                renders.Add(routes.PostSocialImage(post).RelativeOutputPath, token => BuildPostSocialImageAsync(configuration, post, token));
            foreach (var page in pages.Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.SocialImage)))
                renders.Add(routes.ContentSocialImage(page.Route).RelativeOutputPath, token => BuildContentSocialImageAsync(configuration, page, token));
        }
        if (plan.Artifacts.Any(artifact => artifact.RelativeOutputPath == routes.Llms.RelativeOutputPath))
            renders.Add(routes.Llms.RelativeOutputPath, _ => Task.FromResult(GetTextContentBytes(BuildLlmsTxt(configuration, context.Posts, pages))));
        var declaredPaths = plan.Artifacts.Select(artifact => artifact.RelativeOutputPath).ToHashSet(StringComparer.Ordinal);
        if (subset)
            foreach (var path in renders.Keys.Where(path => !declaredPaths.Contains(path)).ToArray()) renders.Remove(path);
        if (!declaredPaths.SetEquals(renders.Keys))
            throw new InvalidOperationException("Render actions must exactly match the validated build artifacts.");
        var textPaths = textRenders.Keys.Concat(pages.Select(page => page.Route.RelativeOutputPath))
            .Concat(redirects.Select(file => file.RelativePath)).Append(routes.Llms.RelativeOutputPath)
            .Append(routes.WebManifest.RelativeOutputPath).ToHashSet(StringComparer.Ordinal);

        var remaining = plan.Nodes.ToDictionary(node => node.Id.Value, StringComparer.Ordinal);
        while (remaining.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = remaining.Values.Where(node => node.Dependencies.All(id => completed.ContainsKey(id.Value))).ToArray();
            if (ready.Length == 0) throw new InvalidOperationException("The build plan cannot make progress.");
            var results = new System.Collections.Concurrent.ConcurrentBag<(CachedBuildNode Cache, SiteBuildReportNode Report)>();
            await Parallel.ForEachAsync(ready.Where(node => !pagesByNode.TryGetValue(node.Id.Value, out var page) || page.IsThreadSafe),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                async (node, token) => results.Add(await ExecuteNodeAsync(node, token).ConfigureAwait(false))).ConfigureAwait(false);
            foreach (var node in ready.Where(node => pagesByNode.TryGetValue(node.Id.Value, out var page) && !page.IsThreadSafe))
                results.Add(await ExecuteNodeAsync(node, cancellationToken).ConfigureAwait(false));
            foreach (var result in results)
            {
                completed.Add(result.Cache.NodeId, result.Cache);
                reports.Add(result.Report.NodeId, result.Report);
                remaining.Remove(result.Cache.NodeId);
            }
        }
        var digest = await cache.SaveAsync(completed.Values, cancellationToken).ConfigureAwait(false);
        return new BuildExecutionResult(digest, reports.Values.OrderBy(report => report.NodeId, StringComparer.Ordinal).ToArray());

        async Task<(CachedBuildNode Cache, SiteBuildReportNode Report)> ExecuteNodeAsync(BuildNode node, CancellationToken token)
        {
            var inputs = node.Inputs.Select(input => new CachedBuildInput(input.Kind, input.Key, input.Value)).ToArray();
            var dependencies = node.Dependencies.Select(id => id.Value).ToArray();
            var keyBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Implementation = typeof(SiteGenerator).Module.ModuleVersionId,
                Markdown = typeof(Markdig.Markdown).Module.ModuleVersionId,
                node.Id.Value, Inputs = inputs,
                Dependencies = dependencies.Select(id => new { Id = id, completed[id].NodeKey }),
                Artifacts = node.Artifacts.Select(artifact => new { artifact.Id.Value, artifact.RelativeOutputPath }),
            });
            var key = Convert.ToHexStringLower(SHA256.HashData(keyBytes));
            pagesByNode.TryGetValue(node.Id.Value, out var page);
            var needsBody = page?.IsIncludedIn(GeneratedPageDerivedSurfaces.Search) == true;
            var reason = clean ? "Clean build." : assets.ExecutedTransformNodes.Contains(node.Id.Value) ? "The asset transform was executed."
                : node.Inputs.Any(input => input.Key.EndsWith("cachePolicy", StringComparison.Ordinal) && input.Value == "always-rebuild")
                ? "Renderer has undeclared inputs." : dependencies.Any(id => !reports[id].CacheHit) ? "A dependency was regenerated."
                : !previous.TryGetValue(node.Id.Value, out var candidate) ? "No cached node."
                : candidate.NodeKey != key ? "Inputs or implementation changed." : null;
            previous.TryGetValue(node.Id.Value, out var old);
            if (reason is null && old is not null)
            {
                if (old.Artifacts.Count != node.Artifacts.Count || !old.Artifacts.Zip(node.Artifacts).All(pair =>
                        pair.First.ArtifactId == pair.Second.Id.Value && pair.First.RelativePath == pair.Second.RelativeOutputPath))
                    reason = "Artifact declarations changed.";
                else
                    foreach (var artifact in old.Artifacts)
                        if (!await VerifyCachedArtifactAsync(transaction.StagingRoot, artifact, token).ConfigureAwait(false))
                        { reason = "An artifact is missing or corrupt."; break; }
                if (reason is null && needsBody && (old.DerivedBodyHash is null || await cache.ReadBodyAsync(old.DerivedBodyHash, token).ConfigureAwait(false) is null))
                    reason = "The rendered body is missing or corrupt.";
            }
            if (reason is null && old is not null)
            {
                SetBodyProvider(page, old.DerivedBodyHash);
                return (old, new SiteBuildReportNode(node.Id.Value, node.Artifacts.Select(artifact => artifact.RelativeOutputPath).ToArray()) { CacheHit = true });
            }

            var artifacts = new List<CachedBuildArtifact>();
            foreach (var artifact in node.Artifacts)
            {
                var bytes = await renders[artifact.RelativeOutputPath](token).ConfigureAwait(false);
                if (textPaths.Contains(artifact.RelativeOutputPath))
                {
                    var path = SafeCombine(transaction.StagingRoot, artifact.RelativeOutputPath);
                    CreateSafeDirectory(transaction.StagingRoot, Path.GetDirectoryName(path)!);
                    await WriteNewFileAsync(path, bytes, token).ConfigureAwait(false);
                }
                else
                    await WriteBinaryAssetAsync(transaction.StagingRoot, artifact.RelativeOutputPath, bytes, [], token).ConfigureAwait(false);
                artifacts.Add(new CachedBuildArtifact(artifact.Id.Value, artifact.RelativeOutputPath, bytes.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
            var bodyHash = needsBody ? await cache.StoreBodyAsync(page!.DerivedContent ?? string.Empty, token).ConfigureAwait(false) : null;
            SetBodyProvider(page, bodyHash);
            page?.ClearRenderedContent();
            return (new CachedBuildNode(node.Id.Value, key, artifacts, bodyHash) { Inputs = inputs, Dependencies = dependencies },
                new SiteBuildReportNode(node.Id.Value, node.Artifacts.Select(artifact => artifact.RelativeOutputPath).ToArray()) { CacheMissReason = reason ?? "No reusable cache." });
        }

        void SetBodyProvider(IntegratedContentPage? page, string? hash)
        {
            if (page is not null && hash is not null)
                page.SetDerivedContentProvider(() => cache.ReadBodyAsync(hash, cancellationToken).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("A verified rendered body became unavailable during generation."));
        }
    }

    private static async Task<bool> VerifyCachedArtifactAsync(string root, CachedBuildArtifact artifact, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(root, SafeCombine(root, artifact.RelativePath), asynchronous: true);
            return stream.Length == artifact.Length && Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) == artifact.Sha256;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return false; }
    }

    private static async Task<string> ReadStagedTextAsync(string root, string path, CancellationToken cancellationToken)
    {
        await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(root, SafeCombine(root, path), asynchronous: true);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
