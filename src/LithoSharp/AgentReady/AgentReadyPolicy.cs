using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Routing;

namespace LithoSharp.AgentReady;

public enum PublicMarkdownStatus
{
    NotRequested,
    Automatic,
    ExplicitReplacement,
    IntentionallyExcluded,
    Unsupported,
    Unavailable,
    Failed,
}

internal sealed record AgentReadyPolicyDecision(
    bool ShouldProvideMarkdown,
    bool RequiresMarkdown,
    bool ShouldIndex,
    PublicMarkdownStatus InitialStatus);

internal static class AgentReadyPolicy
{
    internal static AgentReadyPolicyDecision Evaluate(AgentReadyOptions options, PageMetadata metadata,
        bool isUnlisted, bool includedInLlms, DateTimeOffset buildTimestamp, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!PagePublicationPolicy.ShouldPublish(metadata, buildTimestamp, environmentName))
            return new(false, false, false, PublicMarkdownStatus.IntentionallyExcluded);
        if (!options.Enabled)
            return new(false, false, false, PublicMarkdownStatus.NotRequested);
        var page = metadata.AgentReady ?? throw new ArgumentException("Page Agent Ready options must not be null.", nameof(metadata));
        if (!Enum.IsDefined(page.MarkdownMode) || !Enum.IsDefined(page.Indexing))
            throw new ArgumentException("Agent Ready policy contains an undefined mode.", nameof(metadata));
        var automaticallySelected = !metadata.NoIndex && !isUnlisted;
        var shouldIndex = includedInLlms && page.Indexing != AgentIndexMode.Exclude
            && (page.Indexing == AgentIndexMode.Include || automaticallySelected);
        if (page.MarkdownMode == PublicMarkdownMode.Disabled)
            return new(false, false, shouldIndex, PublicMarkdownStatus.IntentionallyExcluded);
        var required = page.MarkdownMode == PublicMarkdownMode.Required;
        if (page.MarkdownReplacement is not null)
            return new(true, required, shouldIndex, PublicMarkdownStatus.ExplicitReplacement);
        if (required || automaticallySelected)
            return new(true, required, shouldIndex, PublicMarkdownStatus.NotRequested);
        return new(false, false, shouldIndex, PublicMarkdownStatus.IntentionallyExcluded);
    }

    internal static SiteRoute? MarkdownRoute(SiteRoute htmlRoute, AgentReadyPageOptions options, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(htmlRoute);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MarkdownRoute is { } supplied)
        {
            if (!supplied.RelativeOutputPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An explicit public Markdown route must end in .md.", nameof(options));
            return supplied.WithBaseUrl(baseUrl);
        }
        var path = htmlRoute.RelativeOutputPath;
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            return null;
        path = path[..^5] + ".md";
        // Output paths are decoded. Re-encode each segment once before using the route parser.
        return SiteRoute.ForFile(string.Join("/", path.Split('/').Select(Uri.EscapeDataString)), baseUrl);
    }
}
