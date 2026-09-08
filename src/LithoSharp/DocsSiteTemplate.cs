namespace LithoSharp;

/// <summary>
/// Built-in template for documentation with directory navigation.
/// </summary>
public sealed class DocsSiteTemplate : ISiteTemplate
{
    /// <summary>Emits the shared local search and sitemap. False preserves the original Docs outputs.</summary>
    public bool EnableSearch { get; init; }
    /// <inheritdoc />
    public Task<SiteTemplateResult> RenderAsync(
        SiteTemplateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(context.RenderDocsTemplate(EnableSearch));
    }
}
