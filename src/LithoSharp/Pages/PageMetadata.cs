using System.Collections.ObjectModel;

namespace LithoSharp.Pages;

public sealed class PageMetadata
{
    /// <summary>The stable document variant used by contextual search and navigation.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Documentation.DocumentKey? Document { get; init; }
    /// <summary>The page language; null uses the site language.</summary>
    public string? Language { get; init; }
    /// <summary>Whether the page uses right-to-left writing.</summary>
    public bool RightToLeft { get; init; }
    /// <summary>Whether search engines should omit this directly accessible page.</summary>
    public bool NoIndex { get; init; }
    /// <summary>Existing translated pages, keyed by language tag.</summary>
    public IReadOnlyDictionary<string, SiteUrl> Alternates { get; init; } = new Dictionary<string, SiteUrl>();
    public PageMetadata(
        string? title = null,
        string? description = null,
        bool draft = false,
        DateTimeOffset? publishFrom = null,
        DateTimeOffset? publishUntil = null,
        IEnumerable<string>? environments = null)
    {
        var normalizedFrom = publishFrom?.ToUniversalTime();
        var normalizedUntil = publishUntil?.ToUniversalTime();
        if (normalizedFrom >= normalizedUntil)
        {
            throw new ArgumentException(
                "The publish-until timestamp must be later than the publish-from timestamp.",
                nameof(publishUntil));
        }

        Title = title;
        Description = description;
        Draft = draft;
        PublishFrom = normalizedFrom;
        PublishUntil = normalizedUntil;
        Environments = CopyEnvironments(environments);
    }

    public string? Title { get; }

    public string? Description { get; }

    public bool Draft { get; }

    /// <summary>UTC-normalized inclusive start time.</summary>
    public DateTimeOffset? PublishFrom { get; }

    /// <summary>UTC-normalized exclusive end time.</summary>
    public DateTimeOffset? PublishUntil { get; }

    /// <summary>An empty list permits every environment.</summary>
    public IReadOnlyList<string> Environments { get; }

    private static ReadOnlyCollection<string> CopyEnvironments(IEnumerable<string>? environments)
    {
        if (environments is null)
        {
            return Array.AsReadOnly(Array.Empty<string>());
        }

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var environment in environments)
        {
            if (string.IsNullOrWhiteSpace(environment))
            {
                throw new ArgumentException("Environment names must not be null or empty.", nameof(environments));
            }

            values.Add(environment);
        }

        var result = values.ToArray();
        Array.Sort(result, StringComparer.OrdinalIgnoreCase);
        return Array.AsReadOnly(result);
    }
}
