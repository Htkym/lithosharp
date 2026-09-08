using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;

namespace LithoSharp;

/// <summary>Renders a reusable site component.</summary>
public interface ISiteComponent<TProps>
{
    /// <summary>Renders the component.</summary>
    IHtmlContent Render(TProps props, ComponentRenderingContext context);
}

/// <summary>Renders a typed page layout.</summary>
public interface IPageLayout<TPage> where TPage : notnull
{
    /// <summary>Renders the complete page.</summary>
    IHtmlContent Render(SitePage<TPage> page, PageRenderingContext context);
}

/// <summary>Context shared by components and page layouts.</summary>
public class ComponentRenderingContext
{
    private SiteGenerator? generator;

    internal ComponentRenderingContext(SiteGenerator? generator, SiteGenerator.RenderContext configuration)
    {
        this.generator = generator;
        Configuration = configuration;
    }

    internal SiteGenerator Generator => generator ??= new SiteGenerator();
    internal SiteGenerator.RenderContext Configuration { get; }
    /// <summary>Site settings.</summary>
    public SiteSettings Site => Configuration.Site;
    /// <summary>Localized text.</summary>
    public SiteText Text => Configuration.Text;
    /// <summary>Theme options.</summary>
    public SiteThemeOptions Theme => Configuration.Theme;
    /// <summary>Build timestamp.</summary>
    public DateTimeOffset BuildTimestamp => Configuration.BuildTimestamp;
    /// <summary>Build environment.</summary>
    public string EnvironmentName { get; internal init; } = "Production";
    /// <summary>Registered assets.</summary>
    public AssetRegistry Assets { get; internal init; } = AssetRegistry.Empty;
    /// <summary>Creates a standalone component context.</summary>
    /// <exception cref="ArgumentNullException">The site is null.</exception>
    /// <remarks>Uses English text, the default theme, current UTC time, Production, and an empty asset registry.</remarks>
    public static ComponentRenderingContext Create(SiteSettings site)
    {
        ArgumentNullException.ThrowIfNull(site);
        var configuration = new SiteGenerator.RenderContext(site, SiteText.English, new SiteThemeOptions(), [], [], string.Empty, false, false, DateTimeOffset.UtcNow, new Routing.SiteRouteCatalog(site.BaseUrl, [], []));
        return new ComponentRenderingContext(null, configuration);
    }
    /// <summary>Renders a component with this context.</summary>
    /// <exception cref="ArgumentNullException">The component is null.</exception>
    /// <exception cref="InvalidOperationException">The component returns null.</exception>
    public IHtmlContent Render<TProps>(ISiteComponent<TProps> component, TProps props)
    {
        ArgumentNullException.ThrowIfNull(component);
        return component.Render(props, this)
            ?? throw new InvalidOperationException($"Site component '{component.GetType().FullName}' returned null.");
    }
}

/// <summary>Context used by a page layout.</summary>
public sealed class PageRenderingContext : ComponentRenderingContext
{
    private readonly ContentPageRenderingContext? legacyContext;
    internal PageRenderingContext(SiteGenerator? generator, SiteGenerator.RenderContext configuration, ContentPageRenderingContext? legacyContext = null) : base(generator, configuration)
    {
        this.legacyContext = legacyContext;
        EnvironmentName = legacyContext?.EnvironmentName ?? "Production";
        Assets = legacyContext?.Assets ?? AssetRegistry.Empty;
    }
    /// <summary>Creates a standalone page context.</summary>
    /// <exception cref="ArgumentNullException">The site is null.</exception>
    public new static PageRenderingContext Create(SiteSettings site)
    {
        var context = ComponentRenderingContext.Create(site);
        return new PageRenderingContext(null, context.Configuration);
    }
    /// <summary>Renders Markdown using the generator's safe pipeline.</summary>
    /// <exception cref="ArgumentNullException">The Markdown is null.</exception>
    public IHtmlContent RenderMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return Html.UnsafeRaw(Generator.RenderMarkdown(markdown));
    }
    /// <summary>Renders a typed page using the existing document renderer.</summary>
    /// <exception cref="ArgumentNullException">The page or body is null.</exception>
    public IHtmlContent RenderDocument<TPage>(SitePage<TPage> page, IHtmlContent body) where TPage : notnull
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(body);
        if (legacyContext is not null)
        {
            return Html.UnsafeRaw(legacyContext.RenderDocument(body.ToHtmlString()));
        }
        return Html.UnsafeRaw(Generator.RenderTemplateDocument(Configuration, new SiteTemplateDocument { Title = page.Metadata.Title ?? page.Route.PublicPath, RelativePath = page.Route.RelativeOutputPath, BodyHtml = body.ToHtmlString(), Description = page.Metadata.Description, OpenGraphType = "article", PublishedAt = page.Metadata.PublishFrom }, page.Metadata));
    }
}

/// <summary>Renders breadcrumb links.</summary>
public sealed class Breadcrumbs : ISiteComponent<IReadOnlyList<(string Label, SiteUrl Url)>>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The list or a URL is null.</exception>
    public IHtmlContent Render(IReadOnlyList<(string Label, SiteUrl Url)> props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(props);
        foreach (var item in props)
        {
            ArgumentNullException.ThrowIfNull(item.Url);
        }
        return Html.UnsafeRaw("<nav aria-label=\"Breadcrumb\"><ol>" + string.Concat(props.Select(item => $"<li><a href=\"{item.Url.ToAttributeValue()}\">{Html.Encode(item.Label)}</a></li>")) + "</ol></nav>");
    }
}
