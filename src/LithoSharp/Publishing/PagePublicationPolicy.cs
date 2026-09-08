using LithoSharp.Pages;

namespace LithoSharp.Publishing;

/// <summary>
/// 明示されたビルド時刻と環境名を使って、ページの公開可否を決定します。
/// </summary>
public static class PagePublicationPolicy
{
    /// <summary>指定したメタデータがビルド条件で公開対象になるかどうかを判定します。</summary>
    /// <param name="metadata">評価するページメタデータ。</param>
    /// <param name="buildTimestamp">
    /// 評価に使用するビルド時刻。UTC に正規化して、開始時刻を含み終了時刻を含まない範囲と比較します。
    /// </param>
    /// <param name="environmentName">評価する環境名。大文字と小文字を区別せずに比較します。</param>
    /// <returns>すべての公開条件を満たす場合は <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="metadata"/> または <paramref name="environmentName"/> が <see langword="null"/> です。
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="environmentName"/> が空です。</exception>
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
