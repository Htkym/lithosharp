namespace LithoSharp.AgentReady;

/// <summary>Supplies a complete public alternative for an explicitly registered static component.</summary>
/// <remarks>The HTML and Markdown renderers receive the same bound props. The registration's configuration
/// fingerprint and declared dependencies must describe both renderers. Returning null means unsupported;
/// an empty string explicitly means an empty public alternative. This provider never runs during inspection.</remarks>
public interface IPublicMarkdownComponent<in TProps>
{
    /// <summary>Renders public Markdown without private configuration, execution details, or omitted trust statements.</summary>
    string? RenderPublicMarkdown(TProps props, ComponentRenderingContext context);
}
