using LithoSharp.Content;

public sealed class GuideFrontMatter
{
    public required string Title { get; init; }
    public string Description { get; init; } = string.Empty;
    public int Order { get; init; }
}

[StaticContentCollection(typeof(GuideFrontMatter), typeof(ContentEntry<GuideFrontMatter, string>), "docs", InputRoot = "content", EmitStaticSiteManifest = true)]
public static partial class Guides
{
}
