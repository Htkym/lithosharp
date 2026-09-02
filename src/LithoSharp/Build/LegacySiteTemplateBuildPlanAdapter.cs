using System.Globalization;
using System.Text.Json;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Routing;

namespace LithoSharp.Build;

internal static class LegacySiteTemplateBuildPlanAdapter
{
    internal static readonly BuildNodeId TemplateNodeId = new("template:legacy-opaque");
    internal static readonly BuildNodeId CommonNodeId = new("generator:common");

    internal static SiteBuildPlan Create(
        ISiteTemplate template,
        SiteSettings site,
        SiteText text,
        SiteThemeOptions theme,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<SiteExtraPage> extraPages,
        IReadOnlyList<IntegratedContentPage> contentPages,
        SiteRouteCatalog routes,
        IReadOnlyList<SiteTemplateFile> templateFiles,
        IReadOnlyList<SiteRoute> commonArtifacts,
        DateTimeOffset buildTimestamp,
        string environmentName,
        string faviconSourceDirectory,
        bool hasFaviconAssets,
        bool hasSocialImage,
        bool generateLlmsTxt)
    {
        var inputs = new List<BuildInput>
        {
            BuildInput.FromValue("template.mode", "opaque-whole-site"),
            BuildInput.FromValue("template.cachePolicy", "always-rebuild"),
            BuildInput.FromValue(
                "template.identity",
                template.GetType().AssemblyQualifiedName ?? template.GetType().FullName ?? template.GetType().Name),
            BuildInput.FromValue(
                "template.moduleVersionId",
                template.GetType().Module.ModuleVersionId.ToString("D")),
            BuildInput.FromConfiguration("site.settings", JsonSerializer.Serialize(site)),
            BuildInput.FromConfiguration("customization.text", JsonSerializer.Serialize(text)),
            BuildInput.FromConfiguration("customization.theme", JsonSerializer.Serialize(theme)),
            BuildInput.FromConfiguration(
                "build.timestamp",
                buildTimestamp.ToString("O", CultureInfo.InvariantCulture)),
            BuildInput.FromConfiguration("build.environment", environmentName),
            BuildInput.FromConfiguration(
                "assets.faviconSourceDirectory",
                Path.GetFullPath(faviconSourceDirectory)),
            BuildInput.FromConfiguration("assets.hasFaviconSet", hasFaviconAssets.ToString()),
            BuildInput.FromConfiguration("assets.hasSocialImage", hasSocialImage.ToString()),
            BuildInput.FromConfiguration("assets.generateLlmsTxt", generateLlmsTxt.ToString()),
            BuildInput.FromCollection("pages.markdown.published"),
            BuildInput.FromCollection("pages.extra.published"),
            BuildInput.FromCollection(
                "pages.content.published",
                ContentCollectionBuildPlanAdapter.SurfaceValue(
                    contentPages,
                    "legacy-template",
                    includeContent: true)),
        };

        for (var index = 0; index < posts.Count; index++)
        {
            var post = posts[index];
            inputs.Add(BuildInput.FromValue(
                $"page.markdown:{index:D8}",
                JsonSerializer.Serialize(new
                {
                    post.FilePath,
                    post.Slug,
                    post.FrontMatter,
                    post.MarkdownBody,
                    Route = routes.Post(post),
                })));
        }

        for (var index = 0; index < extraPages.Count; index++)
        {
            var page = extraPages[index];
            inputs.Add(BuildInput.FromValue(
                $"page.extra:{index:D8}",
                JsonSerializer.Serialize(new
                {
                    Page = page,
                    Route = routes.ExtraPage(page),
                })));
        }

        if (hasFaviconAssets)
        {
            foreach (var fileName in SiteGenerator.BundledFaviconAssetNames
                         .Distinct(StringComparer.Ordinal))
            {
                inputs.Add(BuildInput.FromFile($"favicon/{fileName}"));
            }
        }
        else if (hasSocialImage)
        {
            inputs.Add(BuildInput.FromFile($"favicon/{SiteGenerator.SocialImageSourceFileName}"));
        }

        var templateNode = new BuildNode(
            TemplateNodeId,
            inputs,
            artifacts: templateFiles.Select(file => Artifact(TemplateNodeId, file.RelativePath)));
        if (commonArtifacts.Count == 0)
        {
            return SiteBuildPlan.Create([templateNode]);
        }

        var commonNode = new BuildNode(
            CommonNodeId,
            dependencies: [TemplateNodeId],
            artifacts: commonArtifacts.Select(route => Artifact(CommonNodeId, route.RelativeOutputPath)));
        return SiteBuildPlan.Create([templateNode, commonNode]);
    }

    private static BuildArtifact Artifact(BuildNodeId owner, string relativeOutputPath) =>
        new(
            new BuildArtifactId($"{owner.Value}:{relativeOutputPath}"),
            owner,
            relativeOutputPath);
}
