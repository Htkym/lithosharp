using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Content;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    private IEnumerable<BuildNode> CreateScopedSearchNodes(RenderContext configuration)
    {
        foreach (var projection in configuration.ScopedSearch)
        {
            var pages = configuration.ContentPages.Where(page => projection.Includes(page.Metadata.Document)
                && page.IsIncludedIn(GeneratedPageDerivedSurfaces.Search)).ToArray();
            yield return new BuildNode(projection.NodeId,
                [BuildInput.FromConfiguration("search.scope", JsonSerializer.Serialize(new
                {
                    projection.Collection, projection.Version, projection.Locale, projection.RelativeOutputPath,
                    bodyPolicy = "static-body-overrides/1",
                    bodyTextOverrides = projection.BodyTextOverrides.OrderBy(static item => item.Key, StringComparer.Ordinal)
                        .Select(static item => new { id = item.Key, hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(item.Value))) }),
                })), BuildInput.FromCollection("search.members", JsonSerializer.Serialize(pages.Select(page => new
                {
                    page.OwnerId, page.Metadata, page.Route.PublicPath,
                })))],
                pages.Select(static page => new BuildNodeId(page.OwnerId)),
                [new BuildArtifact(new BuildArtifactId("scoped-search:" + projection.Id), projection.NodeId, projection.RelativeOutputPath)]);
        }
    }

    private string BuildScopedSearchIndex(RenderContext configuration, ScopedSearchProjection projection)
    {
        var pages = configuration.ContentPages.Where(page => projection.Includes(page.Metadata.Document)
            && page.IsIncludedIn(GeneratedPageDerivedSurfaces.Search)).ToArray();
        var documents = BuildSearchDocuments(configuration, [], pages);
        if (projection.BodyTextOverrides.Count != 0)
        {
            var overridesByUrl = pages.Where(page => projection.BodyTextOverrides.ContainsKey(page.Metadata.Document!.Id))
                .ToDictionary(page => configuration.Routes.PublicPath(page.Route),
                    page => NormalizeForIndex(projection.BodyTextOverrides[page.Metadata.Document!.Id]), StringComparer.Ordinal);
            documents = documents.Select(document => overridesByUrl.TryGetValue(document.Url, out var body)
                ? document with { Body = body } : document).ToArray();
        }
        return JsonSerializer.Serialize(documents, SearchSerializerOptions);
    }
}
