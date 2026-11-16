using LithoSharp;
using LithoSharp.AgentReady;
using LithoSharp.Content;

public static class DocsSampleComponents
{
    public static MarkdownComponentRegistry Registry { get; } = new([
        MarkdownComponentRegistration.Create("version-badge", new VersionBadgeComponent(),
            GeneratedVersionBadges.Binder, "version-badge:v1")
    ]);

    private sealed class VersionBadgeComponent : ISiteComponent<VersionBadgeProps>, IPublicMarkdownComponent<VersionBadgeProps>
    {
        public IHtmlContent Render(VersionBadgeProps props, ComponentRenderingContext context) =>
            Html.UnsafeRaw("<span class=\"version-badge\" data-stability=\"" + Html.Encode(props.Stability.ToString())
                + "\">Version " + Html.Encode(props.Version) + "</span>");
        public string RenderPublicMarkdown(VersionBadgeProps props, ComponentRenderingContext context) =>
            "Version " + DocsSampleFactory.MarkdownText(props.Version);
    }
}
