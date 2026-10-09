using LithoSharp.HtmlParsing;
using LithoSharp.Pages;

namespace LithoSharp.Testing;

/// <summary>Provides framework-independent assertions over rendered HTML.</summary>
/// <remarks>Assertion mismatches throw <see cref="SiteTestException"/>. Disposed document access throws
/// <see cref="ObjectDisposedException"/>. Selectors use the declared bounded Testing grammar.</remarks>
public sealed class SiteTestDocument : IDisposable
{
    private readonly HtmlTestDocument snapshot;

    private SiteTestDocument(HtmlTestDocument snapshot) => this.snapshot = snapshot;

    /// <summary>Parses an HTML document without executing scripts or network requests.</summary>
    /// <param name="html">HTML source.</param>
    /// <returns>A disposable snapshot owner.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    public static SiteTestDocument Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return new SiteTestDocument(new HtmlTestDocument(HtmlFacts.Parse(html)));
    }

    /// <summary>Gets the read-only parsed snapshot, invalidated when this owner is disposed.</summary>
    public HtmlTestDocument Snapshot { get { EnsureNotDisposed(); return snapshot; } }

    /// <summary>Gets the optional route associated with this document.</summary>
    public LithoSharp.Routing.SiteRoute? Route { get; internal set; }

    /// <summary>Renders a component and parses its HTML output.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The component returns null.</exception>
    public static SiteTestDocument RenderComponent<T>(ISiteComponent<T> component, T props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(context);
        return Parse(context.Render(component, props).ToHtmlString());
    }

    /// <summary>Renders a typed layout and parses its HTML output.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The layout returns null.</exception>
    public static SiteTestDocument RenderLayout<T>(IPageLayout<T> layout, SitePage<T> page, PageRenderingContext context)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(context);
        return Parse(layout.Render(page, context)?.ToHtmlString() ?? throw new InvalidOperationException("The layout returned null."));
    }

    /// <summary>Asserts that at least one element matches a CSS selector.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="selector"/> is empty.</exception>
    public void AssertElement(string selector) => Find(selector);

    /// <summary>Asserts exact text content for the first matching element.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void AssertText(string selector, string expected) => AssertValue(selector, expected, element => element.TextContent);

    /// <summary>Asserts an attribute value for the first matching element.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void AssertAttribute(string selector, string name, string expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        AssertValue(selector, expected, element => element.GetAttribute(name));
    }

    /// <summary>Asserts a meta element's content value.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public void AssertMeta(string name, string expected, bool property = false)
    {
        EnsureNotDisposed(); ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(expected);
        if (!Snapshot.QueryAll("meta").Any(element => string.Equals(element.GetAttribute(property ? "property" : "name"), name, StringComparison.Ordinal) && string.Equals(element.GetAttribute("content"), expected, StringComparison.Ordinal))) throw new SiteTestException($"Expected meta {name} to have content '{expected}'.");
    }

    /// <summary>Asserts that a link with the exact href exists.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="href"/> is null.</exception>
    public void AssertLink(string href)
    {
        EnsureNotDisposed(); ArgumentNullException.ThrowIfNull(href);
        if (!Snapshot.QueryAll("a").Any(element => string.Equals(element.GetAttribute("href"), href, StringComparison.Ordinal))) throw new SiteTestException($"Expected a link with href '{href}'.");
    }

    /// <summary>Asserts that an image with the exact src exists.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="src"/> is null.</exception>
    public void AssertImage(string src)
    {
        EnsureNotDisposed(); ArgumentNullException.ThrowIfNull(src);
        if (!Snapshot.QueryAll("img").Any(element => string.Equals(element.GetAttribute("src"), src, StringComparison.Ordinal))) throw new SiteTestException($"Expected an image with src '{src}'.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        snapshot.Invalidate();
    }

    private HtmlTestElement Find(string selector)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(selector);
        if (string.IsNullOrWhiteSpace(selector)) throw new ArgumentException("A selector must not be empty.", nameof(selector));
        return Snapshot.Query(selector) ?? throw new SiteTestException($"Expected an element matching '{selector}'.");
    }

    private void AssertValue(string selector, string expected, Func<HtmlTestElement, string?> value)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var element = Find(selector);
        var actual = value(element);
        if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new SiteTestException($"Expected '{selector}' to have '{expected}', but found '{actual ?? "<missing>"}'.");
    }

    private void EnsureNotDisposed() => snapshot.EnsureAlive();
}
