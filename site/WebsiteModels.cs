using LithoSharp;
using LithoSharp.Content;

[StaticContentCollection(typeof(ExampleFrontMatter), typeof(ContentEntry<ExampleFrontMatter, string>),
    "website-examples", InputRoot = "examples", EmitJsonSchema = true, EmitStaticSiteManifest = true)]
public static partial class GeneratedExamples { }

public enum ExampleStatus { Draft, Published }

public sealed class ExampleFrontMatter
{
    public required string Title { get; init; }
    public required string Locale { get; init; }
    public ExampleStatus Status { get; init; }
    [ContentRequiredWhen("status", "Published")]
    public string? SupportedVersion { get; init; }
}

[StaticContentCollection(typeof(VersionBadgeProps), typeof(string), "website-badges", EmitJsonSchema = true)]
public static partial class GeneratedBadges { }

public sealed class VersionBadgeProps
{
    public required string Version { get; init; }
}

public sealed class VersionBadge : ISiteComponent<VersionBadgeProps>
{
    public IHtmlContent Render(VersionBadgeProps props, ComponentRenderingContext context) =>
        Html.UnsafeRaw("<span class=\"version-badge\">" + Html.Encode(props.Version) + "</span>");
}
