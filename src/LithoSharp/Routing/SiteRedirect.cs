namespace LithoSharp.Routing;

public sealed record SiteRedirect
{
    /// <summary>Generation validates collisions, cycles, chains and missing targets.</summary>
    public SiteRedirect(SiteRoute source, SiteRoute target)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public SiteRoute Source { get; }
    public SiteRoute Target { get; }
}
