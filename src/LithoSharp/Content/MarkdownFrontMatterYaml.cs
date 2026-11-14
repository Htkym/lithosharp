using System.Collections;
using System.Globalization;
using System.Text;

namespace LithoSharp.Content;

/// <summary>Reads typed front matter and writes new post metadata using native YAML.</summary>
/// <remarks>Serialization creates deterministic YAML; it does not preserve an existing document's formatting or bytes.</remarks>
public static class MarkdownFrontMatterYaml
{
    /// <summary>Converts post metadata into deterministic YAML with LF line endings.</summary>
    /// <param name="frontMatter">The front matter to convert.</param>
    /// <returns>The YAML string.</returns>
    public static string Serialize(PostFrontMatter frontMatter)
    {
        ArgumentNullException.ThrowIfNull(frontMatter);
        var text = new StringBuilder();
        String(text, "title", frontMatter.Title);
        String(text, "date", frontMatter.Date.ToString("O", CultureInfo.InvariantCulture));
        String(text, "summary", frontMatter.Summary);
        Strings(text, "tags", frontMatter.Tags);
        ArgumentNullException.ThrowIfNull(frontMatter.Sources);
        if (frontMatter.Sources.Count == 0)
            text.Append("sources: []\n");
        else
        {
            text.Append("sources:\n");
            foreach (var source in frontMatter.Sources)
            {
                ArgumentNullException.ThrowIfNull(source);
                String(text, "  - type", source.Type);
                String(text, "    name", source.Name);
                if (source.Url is not null)
                    String(text, "    url", source.Url);
            }
        }
        text.Append("draft: ").Append(frontMatter.Draft ? "true" : "false").Append('\n');
        Strings(text, "environments", frontMatter.Environments);
        if (frontMatter.SidebarPosition is { } sidebarPosition)
            text.Append("sidebar_position: ").Append(sidebarPosition.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (frontMatter.SidebarLabel is { } sidebarLabel)
            String(text, "sidebar_label", sidebarLabel);
        if (frontMatter.PublishFrom is { } publishFrom)
            String(text, "publish_from", publishFrom.ToString("O", CultureInfo.InvariantCulture));
        if (frontMatter.PublishUntil is { } publishUntil)
            String(text, "publish_until", publishUntil.ToString("O", CultureInfo.InvariantCulture));
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>Reads a post metadata mapping. Unknown fields are ignored for legacy post compatibility.</summary>
    /// <param name="yaml">The YAML string to read.</param>
    /// <returns>The parsed front matter.</returns>
    /// <exception cref="FormatException">The YAML or typed values are invalid or unsupported.</exception>
    public static PostFrontMatter? Deserialize(string yaml) => ReadMapping<PostFrontMatter>(yaml, rejectUnknownFields: false);

    /// <summary>Reads a DTO mapping using the strict reflection binder, snake_case names, and ContentName/ContentIgnore.</summary>
    /// <typeparam name="T">A DTO supported by ReflectionContentFrontMatterBinder with a public parameterless constructor.</typeparam>
    /// <param name="yaml">The YAML string to read.</param>
    /// <returns>The parsed front matter.</returns>
    /// <exception cref="FormatException">The YAML or typed values are invalid or unsupported.</exception>
    /// <exception cref="NotSupportedException">T is a scalar, sequence, dictionary, or untyped object.</exception>
    public static T? Deserialize<T>(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        var type = typeof(T);
        if (type == typeof(object) || Type.GetTypeCode(type) != TypeCode.Object || Nullable.GetUnderlyingType(type) is not null
            || typeof(IEnumerable).IsAssignableFrom(type)
            || type == typeof(Guid) || type == typeof(DateTimeOffset) || type == typeof(DateOnly)
            || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(Uri))
            throw new NotSupportedException("Front matter deserialization requires a DTO mapping supported by ReflectionContentFrontMatterBinder.");
        // Nullable reference annotations erase to the same DTO type; nullable value roots were rejected above.
#pragma warning disable CS8714
        return ReadMapping<T>(yaml, rejectUnknownFields: true);
#pragma warning restore CS8714
    }

    private static T? ReadMapping<T>(string yaml, bool rejectUnknownFields) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        var parsed = MarkdownContentCollectionLoader<T>.ParseYaml(yaml, "front-matter.yaml", 1, CancellationToken.None);
        if (!parsed.IsSuccess)
            throw Invalid(parsed.Diagnostics);
        var bound = new ReflectionContentFrontMatterBinder<T>(rejectUnknownFields).Bind(parsed.Value!);
        if (!bound.IsSuccess)
            throw Invalid(bound.Diagnostics);
        return bound.Value;
    }

    private static FormatException Invalid(IEnumerable<Diagnostics.SiteDiagnostic> diagnostics) =>
        new(string.Join("\n", diagnostics.Select(diagnostic =>
            $"{diagnostic.Id} ({diagnostic.Location?.Line}:{diagnostic.Location?.Column}): {diagnostic.Message}")));

    private static void String(StringBuilder text, string name, string value) =>
        text.Append(name).Append(": ").Append(Quote(value)).Append('\n');

    private static void Strings(StringBuilder text, string name, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        text.Append(name).Append(": [");
        var separator = string.Empty;
        foreach (var value in values)
        {
            text.Append(separator).Append(Quote(value));
            separator = ", ";
        }
        text.Append("]\n");
    }

    private static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = new StringBuilder(value.Length + 2).Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"':
                    text.Append("\\\"");
                    break;
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                default:
                    if (character < ' ' || character is '\u0085' or '\u2028' or '\u2029')
                        text.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                        text.Append(character).Append(value[++index]);
                    else if (char.IsSurrogate(character))
                        throw new ArgumentException("YAML strings must contain valid Unicode.", nameof(value));
                    else
                        text.Append(character);
                    break;
            }
        }
        return text.Append('"').ToString();
    }
}
