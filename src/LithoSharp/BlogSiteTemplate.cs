namespace LithoSharp;

/// <summary>
/// Built-in template for the legacy blog layout.
/// </summary>
public sealed class BlogSiteTemplate : ISiteTemplate
{
    /// <inheritdoc />
    public Task<SiteTemplateResult> RenderAsync(
        SiteTemplateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(context.RenderBlogTemplate());
    }
}
