using LithoSharp.Build;
using LithoSharp.Configuration;
using System.Text;

namespace LithoSharp.Content;

/// <summary>Opts a static partial class into a generated fixed HTML plan.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class FixedHtmlSiteAttribute(string baseUrl) : Attribute
{
    /// <summary>The absolute site base used by both the compiler and the runtime owner.</summary>
    public string BaseUrl { get; } = baseUrl;
}

/// <summary>Declares a complete HTML file produced by a parameterless static IHtmlContent method.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class FixedHtmlPageAttribute(string relativeOutputPath) : Attribute
{
    /// <summary>The relative .html or .htm output file.</summary>
    public string RelativeOutputPath { get; } = relativeOutputPath;
}

/// <summary>An immutable generated-asset plan with no template or extension rewriting its HTML files.</summary>
/// <remarks>The Generator emits FixedHtmlPlan on a FixedHtmlSite class. Only compiler-proven literal
/// pages receive Closed anchor diagnostics. Other pages remain runtime content.</remarks>
public sealed class FixedHtmlSitePlan
{
    private readonly SiteSettings site;
    private readonly IReadOnlyList<SiteGeneratedAsset> pages;

    /// <summary>Encodes and snapshots full HTML inside Core, without consumer-supplied encoders or collection factories.</summary>
    public FixedHtmlSitePlan(string baseUrl, string[] relativeOutputPaths, string[] html)
        : this(baseUrl, PrepareHtml(relativeOutputPaths, html)) { }

    private static IReadOnlyList<SiteGeneratedAsset> PrepareHtml(string[] relativeOutputPaths, string[] html)
    {
        ArgumentNullException.ThrowIfNull(relativeOutputPaths);
        ArgumentNullException.ThrowIfNull(html);
        var paths = relativeOutputPaths.ToArray();
        var sources = html.ToArray();
        if (paths.Length != sources.Length) throw new ArgumentException("Each output path requires one full HTML source.", nameof(html));
        var encoding = new UTF8Encoding(false, true);
        return Array.AsReadOnly(paths.Select((path, index) => new SiteGeneratedAsset(
            "fixed-html:" + path, path, encoding.GetBytes(sources[index]))).ToArray());
    }

    /// <summary>Snapshots already prepared HTML assets. Prefer the generated FixedHtmlPlan property.</summary>
    public FixedHtmlSitePlan(string baseUrl, IReadOnlyList<SiteGeneratedAsset> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _ = SiteUrl.FromAbsolute(baseUrl);
        site = new SiteSettings { BaseUrl = baseUrl };
        this.pages = Array.AsReadOnly(pages.ToArray());
        if (this.pages.Any(page => page is null)) throw new ArgumentException("A fixed HTML page must not be null.", nameof(pages));
    }

    /// <summary>Publishes the exact prepared bytes through the common ownership and output transaction.</summary>
    /// <remarks>Default site support files are generated normally. Conflicting owners fail the build;
    /// no caller-supplied template, extension, transform or post-build hook is accepted by this entry point.</remarks>
    public Task<SiteGenerationResult> GenerateAsync(string outputDirectory, CancellationToken cancellationToken = default) =>
        new SiteGenerator().GenerateWithOptionsAsync(site, [], outputDirectory, clean: true, customization: null,
            new SiteGenerationOptions { GeneratedAssets = pages }, cancellationToken);
}
