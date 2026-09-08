using System.Globalization;
using System.Text.Json;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Routing;

namespace LithoSharp.Build;

internal static class ContentCollectionBuildPlanAdapter
{
    internal static IReadOnlyList<BuildNode> Create(
        IReadOnlyList<IntegratedContentPage> pages,
        SiteSettings site,
        SiteText text,
        SiteThemeOptions theme,
        DateTimeOffset buildTimestamp,
        string environmentName,
        string faviconSourceDirectory,
        bool hasFaviconAssets,
        bool hasSocialImage,
        SiteRouteCatalog routes)
    {
        var nodes = new List<BuildNode>(pages.Count * (hasSocialImage ? 2 : 1));
        var navigationValue = Json(pages
            .Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.Navigation))
            .Select(page => new
        {
            Collection = page.CollectionId.Value,
            Page = page.PageId.Value,
            page.Route.PublicPath,
            page.Route.RelativeOutputPath,
            page.Metadata.Title,
            Layout = page.LayoutId?.Value,
        }));
        navigationValue = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(navigationValue)));

        foreach (var page in pages)
        {
            var nodeId = new BuildNodeId(page.OwnerId);
            var inputs = PageInputs(
                page,
                site,
                text,
                theme,
                buildTimestamp,
                environmentName,
                hasFaviconAssets,
                hasSocialImage,
                navigationValue);
            nodes.Add(new BuildNode(
                nodeId,
                inputs,
                page.DeclaredDependencies.Where(dependency => dependency.Kind == ContentDependencyKind.Asset)
                    .Select(dependency => new BuildNodeId("asset:" + dependency.Key)),
                artifacts:
                [
                    new BuildArtifact(
                        new BuildArtifactId($"artifact:{page.OwnerId}"),
                        nodeId,
                        page.Route.RelativeOutputPath),
                ]));

            if (hasSocialImage
                && page.IsIncludedIn(GeneratedPageDerivedSurfaces.SocialImage))
            {
                var socialNodeId = SocialNodeId(page);
                nodes.Add(new BuildNode(
                    socialNodeId,
                    [
                        BuildInput.FromValue("content.collection", page.CollectionId.Value),
                        BuildInput.FromValue("content.page", page.PageId.Value),
                        BuildInput.FromValue("content.route", page.Route.RelativeOutputPath),
                        BuildInput.FromValue("content.title", page.Metadata.Title ?? string.Empty),
                        BuildInput.FromConfiguration("site.title", site.Title),
                        BuildInput.FromFile(
                            $"favicon/{SiteGenerator.SocialImageSourceFileName}",
                            BuildInputFingerprint.FromFile(Path.Combine(
                                faviconSourceDirectory,
                                SiteGenerator.SocialImageSourceFileName))),
                        .. DependencyInputs(page),
                    ],
                    artifacts:
                    [
                        new BuildArtifact(
                            new BuildArtifactId($"artifact:{socialNodeId.Value}"),
                            socialNodeId,
                            routes.ContentSocialImage(page.Route).RelativeOutputPath),
                    ]));
            }
        }

        return nodes;
    }

    internal static BuildNodeId SocialNodeId(IntegratedContentPage page) =>
        new($"social:collection:{page.OwnerId["page:collection:".Length..]}");

    internal static string SurfaceValue(
        IReadOnlyList<IntegratedContentPage> pages,
        string surface,
        bool includeContent,
        GeneratedPageDerivedSurfaces? requiredSurface = null) =>
        Json(pages
            .Where(page => requiredSurface is null || page.IsIncludedIn(requiredSurface.Value))
            .Select(page => new
        {
            Surface = surface,
            Collection = page.CollectionId.Value,
            Page = page.PageId.Value,
            page.SourceFingerprint,
            Transformation = page.TransformationId?.Value,
            SourceTransformation = page.SourceTransformationId?.Value,
            Sources = SourceValues(page),
            Dependencies = DependencyValues(page),
            page.Route.PublicPath,
            page.Route.RelativeOutputPath,
            Metadata = page.Metadata,
            Layout = page.LayoutId?.Value,
            Renderer = includeContent ? page.RendererFingerprint : null,
        }));

    private static IEnumerable<BuildInput> PageInputs(
        IntegratedContentPage page,
        SiteSettings site,
        SiteText text,
        SiteThemeOptions theme,
        DateTimeOffset buildTimestamp,
        string environmentName,
        bool hasFaviconAssets,
        bool hasSocialImage,
        string navigationValue)
    {
        yield return BuildInput.FromCollection(page.CollectionId.Value, page.SourceFingerprint);
        yield return BuildInput.FromValue("content.entry", page.EntryId.Value);
        yield return BuildInput.FromValue("content.page", page.PageId.Value);
        yield return BuildInput.FromValue("content.inputRoot", page.InputRoot);
        yield return BuildInput.FromValue("content.sourcePath", page.SourcePath);
        yield return BuildInput.FromValue("content.sourceFingerprint", page.SourceFingerprint);
        yield return BuildInput.FromValue(
            "content.transformation",
            page.TransformationId?.Value ?? string.Empty);
        yield return BuildInput.FromValue(
            "content.sourceTransformation",
            page.SourceTransformationId?.Value ?? string.Empty);
        for (var index = 0; index < page.Sources.Count; index++)
        {
            var source = page.Sources[index];
            yield return BuildInput.FromValue(
                $"content.source:{index:D8}",
                Json(new
                {
                    Collection = source.CollectionId.Value,
                    Entry = source.EntryId.Value,
                    source.SourcePath,
                    source.SourceFingerprint,
                }));
        }

        yield return BuildInput.FromValue("content.route.publicPath", page.Route.PublicPath);
        yield return BuildInput.FromValue(
            "content.route.outputPath",
            page.Route.RelativeOutputPath);
        yield return BuildInput.FromValue("content.metadata", Json(page.Metadata));
        yield return BuildInput.FromValue("content.layout", page.LayoutId?.Value ?? string.Empty);
        yield return BuildInput.FromValue("content.renderer", page.RendererFingerprint ?? string.Empty);
        yield return BuildInput.FromValue(
            "content.derivedSurfaces",
            ((int)page.DerivedSurfaces).ToString(CultureInfo.InvariantCulture));
        yield return BuildInput.FromConfiguration("content.navigation", navigationValue);
        yield return BuildInput.FromConfiguration("site.settings", Json(site));
        yield return BuildInput.FromConfiguration("customization.text", Json(text));
        yield return BuildInput.FromConfiguration("customization.theme", Json(theme));
        yield return BuildInput.FromConfiguration(
            "build.timestamp",
            buildTimestamp.ToString("O", CultureInfo.InvariantCulture));
        yield return BuildInput.FromConfiguration("build.environment", environmentName);
        yield return BuildInput.FromConfiguration(
            "assets.hasFaviconSet",
            hasFaviconAssets.ToString(CultureInfo.InvariantCulture));
        yield return BuildInput.FromConfiguration(
            "assets.hasSocialImage",
            hasSocialImage.ToString(CultureInfo.InvariantCulture));
        foreach (var input in DependencyInputs(page))
        {
            yield return input;
        }

        if (!page.CanCacheRendering)
        {
            yield return BuildInput.FromValue("content.cachePolicy", "always-rebuild");
        }
    }

    private static IEnumerable<BuildInput> DependencyInputs(IntegratedContentPage page)
    {
        foreach (var dependency in page.DeclaredDependencies)
        {
            if (dependency.Kind == ContentDependencyKind.Asset)
            {
                yield return BuildInput.FromValue("content.asset:" + dependency.Key, dependency.Key);
                continue;
            }
            if (dependency.Kind == ContentDependencyKind.Value)
            {
                yield return BuildInput.FromValue(
                    $"content.dependency:{dependency.Key}",
                    dependency.Value!);
                continue;
            }

            string fingerprint;
            try
            {
                fingerprint = BuildInputFingerprint.FromContainedFile(
                    page.InputRoot,
                    dependency.Key);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new InvalidOperationException(
                    $"Declared file dependency '{dependency.Key}' for collection '{page.CollectionId}' does not exist under '{Path.GetFullPath(page.InputRoot)}'.",
                    exception);
            }

            yield return BuildInput.FromFile(
                dependency.Key,
                fingerprint);
        }
    }

    private static object[] DependencyValues(IntegratedContentPage page) =>
        DependencyInputs(page)
            .Select(input => new { input.Kind, input.Key, input.Value })
            .Cast<object>()
            .ToArray();

    private static object[] SourceValues(IntegratedContentPage page) =>
        page.Sources
            .Select(source => new
            {
                Collection = source.CollectionId.Value,
                Entry = source.EntryId.Value,
                source.SourcePath,
                source.SourceFingerprint,
            })
            .Cast<object>()
            .ToArray();

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
}
