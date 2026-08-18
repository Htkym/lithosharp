namespace LithoSharp;

/// <summary>
/// Built-in template for documentation with directory navigation.
/// </summary>
public sealed class DocsSiteTemplate : ISiteTemplate
{
    /// <inheritdoc />
    public Task<SiteTemplateResult> RenderAsync(
        SiteTemplateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(context.RenderDocsTemplate());
    }
}
