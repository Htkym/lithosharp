using LithoSharp.Content;
using LithoSharp.Pages;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    internal string RenderTypedDocsDocument(
        RenderContext configuration,
        DocsContentPage page,
        string bodyHtml,
        IReadOnlyList<SiteTemplateHeading> headings,
        PageMetadata metadata,
        string? socialImageRelativePath)
    {
        ArgumentNullException.ThrowIfNull(bodyHtml);
        var snapshot = configuration.TypedDocsNavigation.GetOrAdd(
            (page.CollectionId.Value, page.Presentation.Version, page.Presentation.Locale),
            _ => new Lazy<TypedDocsSnapshot>(() =>
            {
                var navigation = BuildTypedDocsNavigation(configuration.ContentPages
                    .Select(candidate => candidate.Docs).OfType<DocsContentPage>()
                    .Where(candidate => SameDocsScope(page, candidate)));
                var orderedPages = FlattenDocsPresentation(navigation).ToArray();
                return new TypedDocsSnapshot(navigation, orderedPages,
                    orderedPages.Select((candidate, index) => (candidate.Url!, index))
                        .ToDictionary(candidate => candidate.Item1, candidate => candidate.index, StringComparer.Ordinal));
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        var root = snapshot.Root;
        var ordered = snapshot.Ordered;
        var current = snapshot.Positions.GetValueOrDefault(page.Route.PublicPath, -1);
        var previous = current > 0 ? ordered[current - 1] : null;
        var next = current >= 0 && current + 1 < ordered.Length ? ordered[current + 1] : null;
        var body = $"<h1>{Html.Encode(page.Presentation.Title)}</h1>\n"
            + (string.IsNullOrWhiteSpace(page.Presentation.Description) ? string.Empty
                : $"<p class=\"docs-description\">{Html.Encode(page.Presentation.Description)}</p>\n")
            + NormalizePostBodyHeadings(bodyHtml)
            + PreviousNextComponent.RenderCore(previous?.Label, previous?.Url, next?.Label, next?.Url);
        var sidebar = DocsNavigationComponent.RenderCore(root,
            DocsAdditionalLinks(configuration), page.Route.PublicPath, configuration.DocsTemplate?.CollapseTopics == true);
        var socialImageUrl = socialImageRelativePath is null ? null
            : configuration.Routes.AbsoluteUrl(configuration.Routes.File(socialImageRelativePath));
        return DocsPageLayout.RenderCore(configuration, page.Presentation.Title, body,
            page.Route.RelativeOutputPath, page.Presentation.Description ?? metadata.Description,
            "article", metadata.PublishFrom, socialImageUrl, sidebar,
            RenderDocsTableOfContents(configuration, headings), metadata);
    }

    private static bool SameDocsScope(DocsContentPage left, DocsContentPage right) =>
        left.CollectionId.Equals(right.CollectionId)
        && left.Presentation.Version == right.Presentation.Version
        && left.Presentation.Locale == right.Presentation.Locale;

    private static IEnumerable<(string Label, string Url, bool IsCurrent)> DocsAdditionalLinks(RenderContext configuration) =>
        configuration.ExtraPages
            .Where(page => !string.IsNullOrWhiteSpace(page.NavLabel))
            .Select(page => (page.NavLabel!, configuration.Routes.PublicPath(configuration.Routes.ExtraPage(page)), false))
            .Concat(configuration.ContentPages
                .Where(page => page.Docs is null && !page.IsUnlisted
                    && page.IsIncludedIn(GeneratedPageDerivedSurfaces.Navigation)
                    && !string.IsNullOrWhiteSpace(page.Metadata.Title))
                .Select(page => (page.Metadata.Title!, configuration.Routes.PublicPath(page.Route), false)));

    private static DocsPresentationNode BuildDocsPresentationNavigation(
        RenderContext configuration, SiteTemplateNavigationNode legacy)
        => configuration.DocsPresentationNavigation.GetOrAdd(legacy,
            root => new Lazy<DocsPresentationNode>(() => CreateDocsPresentationNavigation(configuration, root),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static DocsPresentationNode CreateDocsPresentationNavigation(
        RenderContext configuration, SiteTemplateNavigationNode legacy)
    {
        var root = DocsPresentationNode.FromLegacy(legacy);
        var groups = configuration.ContentPages.Select(page => page.Docs).OfType<DocsContentPage>()
            .Where(page => page.IsInNavigation)
            .GroupBy(page => (page.CollectionId.Value, page.Presentation.Version, page.Presentation.Locale))
            .OrderBy(group => group.Key.Value, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Version, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Locale, StringComparer.Ordinal).ToArray();
        var typed = groups.Select(group =>
        {
            var navigation = BuildTypedDocsNavigation(group);
            return groups.Length == 1 ? navigation.Children :
                [new DocsPresentationNode(string.Join(" / ", new[] { group.Key.Value, group.Key.Version, group.Key.Locale }
                    .Where(value => !string.IsNullOrEmpty(value))), null, null, navigation.Children)];
        }).SelectMany(nodes => nodes).ToArray();
        return root with { Children = [.. root.Children, .. typed] };
    }

    private static DocsPresentationNode BuildTypedDocsNavigation(IEnumerable<DocsContentPage> pages)
    {
        var visible = pages.Where(page => page.IsInNavigation).ToArray();
        var byId = visible.ToDictionary(page => page.EntryId);
        var nodes = visible.ToDictionary(page => page.EntryId,
            page => new MutableDocsNode(page.EntryId.Value, page.Presentation.Title, page));
        var root = new MutableDocsNode(string.Empty, string.Empty);
        foreach (var page in visible)
        {
            var node = nodes[page.EntryId];
            var ancestors = new HashSet<ContentEntryId> { page.EntryId };
            var parentId = page.Presentation.ParentId;
            while (parentId is not null && byId.TryGetValue(parentId, out var parent))
            {
                if (!ancestors.Add(parentId))
                    throw new InvalidOperationException($"Docs parent cycle in collection '{page.CollectionId}' at entry '{page.EntryId}'.");
                parentId = parent.Presentation.ParentId;
            }
            if (page.Presentation.ParentId is { } explicitParent && nodes.TryGetValue(explicitParent, out var parentNode))
            {
                parentNode.Children.Add(node);
                continue;
            }
            var folder = root;
            var segments = page.SourcePath.Split('/');
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var key = string.Join('/', segments.Take(index + 1));
                var child = folder.Children.FirstOrDefault(candidate => candidate.Page is null && candidate.Key == key);
                if (child is null)
                {
                    child = new MutableDocsNode(key, FormatDocsFolderLabel(segments[index]));
                    folder.Children.Add(child);
                }
                folder = child;
            }
            folder.Children.Add(node);
        }
        return Snapshot(root);

        static int Order(MutableDocsNode node) => node.Page?.Presentation.Order
            ?? node.Children.Select(Order).DefaultIfEmpty(int.MaxValue).Min();
        static DocsPresentationNode Snapshot(MutableDocsNode node) =>
            new(node.Label, node.Page?.Route.PublicPath, node.Page?.Presentation.Description,
                node.Children.OrderBy(Order).ThenBy(child => child.Label, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(child => child.Key, StringComparer.Ordinal).Select(Snapshot).ToArray());
    }

    private static IEnumerable<DocsPresentationNode> FlattenDocsPresentation(DocsPresentationNode root)
    {
        foreach (var node in root.Children)
        {
            if (node.Url is not null)
                yield return node;
            foreach (var child in FlattenDocsPresentation(node))
                yield return child;
        }
    }

    private sealed class MutableDocsNode(string key, string label, DocsContentPage? page = null)
    {
        internal string Key { get; } = key;
        internal string Label { get; } = label;
        internal DocsContentPage? Page { get; } = page;
        internal List<MutableDocsNode> Children { get; } = [];
    }

    internal sealed record TypedDocsSnapshot(DocsPresentationNode Root, DocsPresentationNode[] Ordered,
        IReadOnlyDictionary<string, int> Positions);
}
