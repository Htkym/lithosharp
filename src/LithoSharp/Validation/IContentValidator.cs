using LithoSharp.Content;
using LithoSharp.Configuration;

namespace LithoSharp.Validation;

/// <summary>
/// An extension point that validates a Markdown post. Use it to plug in site-specific
/// rules (such as required headings or summary quality). Throw an
/// <see cref="InvalidOperationException"/> when validation fails.
/// </summary>
public interface IContentValidator
{
    /// <summary>Validates a single post.</summary>
    /// <param name="post">The post to validate.</param>
    /// <param name="context">The validation context.</param>
    void Validate(MarkdownPost post, ContentValidationContext context);
}

/// <summary>
/// The context passed to a content validator.
/// </summary>
/// <param name="Site">Site settings.</param>
/// <param name="ContentDirectory">Content directory, used for example to validate post paths.</param>
public sealed record ContentValidationContext(SiteSettings Site, string ContentDirectory);

/// <summary>
/// The default, lenient validator. It only checks that the summary (the front matter
/// <c>summary</c>) is not empty.
/// </summary>
public sealed class RequiredSummaryValidator : IContentValidator
{
    /// <inheritdoc />
    public void Validate(MarkdownPost post, ContentValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (string.IsNullOrWhiteSpace(post.FrontMatter.Summary))
        {
            throw new InvalidOperationException($"Post '{post.FilePath}' is missing summary.");
        }
    }
}
