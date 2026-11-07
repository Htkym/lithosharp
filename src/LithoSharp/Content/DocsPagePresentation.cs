namespace LithoSharp.Content;

/// <summary>Schema-independent display information for a published documentation entry.</summary>
public sealed record DocsPagePresentation
{
    /// <summary>The page heading and navigation label.</summary>
    public required string Title { get; init; }

    /// <summary>An optional page introduction and topic-card summary.</summary>
    public string? Description { get; init; }

    /// <summary>Display order within a topic; unspecified entries follow ordered entries.</summary>
    public int? Order { get; init; }

    /// <summary>An optional parent entry in the same collection, version, and locale. Otherwise source directories form topics.</summary>
    public ContentEntryId? ParentId { get; init; }

    /// <summary>A version partition when no publication document key exists. A supplied value must agree with that key.</summary>
    public string? Version { get; init; }

    /// <summary>A locale partition when no publication document key exists. A supplied value must agree with that key.</summary>
    public string? Locale { get; init; }

    /// <summary>Excludes the published page from documentation discovery without removing its direct route.</summary>
    public bool Unlisted { get; init; }
}

internal sealed record DocsContentPage(
    ContentCollectionId CollectionId,
    ContentEntryId EntryId,
    string SourcePath,
    Routing.SiteRoute Route,
    DocsPagePresentation Presentation,
    bool IsInNavigation);

// Navigation retains only labels, canonical routes, and summaries, never document bodies.
internal sealed record DocsPresentationNode(
    string Label,
    string? Url,
    string? Description,
    IReadOnlyList<DocsPresentationNode> Children)
{
    internal static DocsPresentationNode FromLegacy(SiteTemplateNavigationNode node) =>
        new(node.Label, node.Page?.Url, node.Page?.Post.FrontMatter.Summary,
            node.Children.Select(FromLegacy).ToArray());
}
