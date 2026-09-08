using LithoSharp.Configuration;
using LithoSharp.Content;

namespace LithoSharp;

/// <summary>Creates a site definition shared by library callers and the command-line tool.</summary>
public interface ISiteFactory
{
    /// <summary>Loads the site's settings, content, and rendering options.</summary>
    /// <param name="context">The directory containing the site project.</param>
    /// <param name="cancellationToken">Cancels content loading.</param>
    /// <returns>The site to generate.</returns>
    Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default);
}

/// <summary>Identifies the source directory of a site project.</summary>
public sealed record SiteFactoryContext
{
    /// <summary>Creates a context with an absolute project directory.</summary>
    /// <param name="ProjectDirectory">The project directory, independent of the tool's executable directory.</param>
    /// <exception cref="ArgumentException">The path is blank or invalid.</exception>
    public SiteFactoryContext(string ProjectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectDirectory);
        this.ProjectDirectory = Path.GetFullPath(ProjectDirectory);
    }

    /// <summary>Gets the absolute project directory.</summary>
    public string ProjectDirectory { get; }
}

/// <summary>Describes a site without requiring a command-line host.</summary>
public sealed record SiteDefinition
{
    /// <summary>Creates a definition with a snapshot of the posts.</summary>
    /// <param name="Site">The site settings.</param>
    /// <param name="Posts">The Markdown posts supplied to the generator.</param>
    /// <exception cref="ArgumentNullException">Settings or posts are null.</exception>
    /// <exception cref="ArgumentException">A post is null.</exception>
    public SiteDefinition(SiteSettings Site, IReadOnlyList<MarkdownPost> Posts)
    {
        ArgumentNullException.ThrowIfNull(Site);
        ArgumentNullException.ThrowIfNull(Posts);
        var snapshot = Posts.ToArray();
        if (snapshot.Any(static post => post is null))
            throw new ArgumentException("Posts must not contain null entries.", nameof(Posts));
        this.Site = Site;
        this.Posts = Array.AsReadOnly(snapshot);
    }

    /// <summary>Gets the site settings.</summary>
    public SiteSettings Site { get; }

    /// <summary>Gets a snapshot of the Markdown posts.</summary>
    public IReadOnlyList<MarkdownPost> Posts { get; }

    /// <summary>Gets the output directory, resolved against the project directory when relative.</summary>
    public string OutputDirectory { get; init; } = "dist";

    /// <summary>Gets optional site templates, text, and theme customization.</summary>
    public SiteCustomization? Customization { get; init; }

    /// <summary>Gets the same generation options used by library callers.</summary>
    public SiteGenerationOptions Options { get; init; } = new();
}
