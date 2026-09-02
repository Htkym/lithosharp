using System.Globalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LithoSharp.Content;

/// <summary>
/// A utility for reading and writing the YAML front matter of a Markdown post.
/// </summary>
public static class MarkdownFrontMatterYaml
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithQuotingNecessaryStrings()
        .DisableAliases()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly IDeserializer PostFrontMatterDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Converts the front matter into a YAML string.</summary>
    /// <param name="frontMatter">The front matter to convert.</param>
    /// <returns>The YAML string.</returns>
    public static string Serialize(PostFrontMatter frontMatter)
    {
        ArgumentNullException.ThrowIfNull(frontMatter);

        var serializableFrontMatter = new Dictionary<string, object?>
        {
            ["title"] = frontMatter.Title,
            ["date"] = frontMatter.Date.ToString("O", CultureInfo.InvariantCulture),
            ["summary"] = frontMatter.Summary,
            ["tags"] = frontMatter.Tags,
            ["sources"] = frontMatter.Sources,
            ["draft"] = frontMatter.Draft,
            ["environments"] = frontMatter.Environments
        };

        if (frontMatter.SidebarPosition is { } sidebarPosition)
        {
            serializableFrontMatter["sidebar_position"] = sidebarPosition;
        }

        if (frontMatter.SidebarLabel is not null)
        {
            serializableFrontMatter["sidebar_label"] = frontMatter.SidebarLabel;
        }

        if (frontMatter.PublishFrom is { } publishFrom)
        {
            serializableFrontMatter["publish_from"] = publishFrom.ToString("O", CultureInfo.InvariantCulture);
        }

        if (frontMatter.PublishUntil is { } publishUntil)
        {
            serializableFrontMatter["publish_until"] = publishUntil.ToString("O", CultureInfo.InvariantCulture);
        }

        return Serializer.Serialize(serializableFrontMatter).TrimEnd();
    }

    /// <summary>Reads front matter from a YAML string.</summary>
    /// <param name="yaml">The YAML string to read.</param>
    /// <returns>The parsed front matter.</returns>
    public static PostFrontMatter? Deserialize(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);

        return PostFrontMatterDeserializer.Deserialize<PostFrontMatter>(yaml);
    }

    /// <summary>Reads an arbitrary front matter type from a YAML string.</summary>
    /// <typeparam name="T">The type to read.</typeparam>
    /// <param name="yaml">The YAML string to read.</param>
    /// <returns>The parsed front matter.</returns>
    public static T? Deserialize<T>(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);

        return Deserializer.Deserialize<T>(yaml);
    }
}
