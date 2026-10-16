using LithoSharp.Pages;

namespace LithoSharp.Publishing;

public static class PagePublicationPolicy
{
    /// <summary>Compares UTC time in the inclusive-start/exclusive-end interval and environment names case-insensitively.</summary>
    public static bool ShouldPublish(
        PageMetadata metadata,
        DateTimeOffset buildTimestamp,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(environmentName);

        if (string.IsNullOrWhiteSpace(environmentName))
        {
            throw new ArgumentException("The environment name must not be empty.", nameof(environmentName));
        }

        if (metadata.Draft)
        {
            return false;
        }

        var timestampUtc = buildTimestamp.ToUniversalTime();
        if (metadata.PublishFrom is { } publishFrom && timestampUtc < publishFrom)
        {
            return false;
        }

        if (metadata.PublishUntil is { } publishUntil && timestampUtc >= publishUntil)
        {
            return false;
        }

        return metadata.Environments.Count == 0
            || metadata.Environments.Contains(environmentName, StringComparer.OrdinalIgnoreCase);
    }
}
