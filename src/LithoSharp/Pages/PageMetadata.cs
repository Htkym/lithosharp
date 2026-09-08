using System.Collections.ObjectModel;

namespace LithoSharp.Pages;

/// <summary>
/// ページの表示情報と公開条件を表します。
/// </summary>
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
    /// <summary>ページの表示情報と公開条件を作成します。</summary>
    /// <param name="title">ページタイトル。</param>
    /// <param name="description">ページの説明。</param>
    /// <param name="draft">下書きとして常に公開対象外にするかどうか。</param>
    /// <param name="publishFrom">公開を開始する時刻。この時刻は公開範囲に含まれます。</param>
    /// <param name="publishUntil">公開を終了する時刻。この時刻は公開範囲に含まれません。</param>
    /// <param name="environments">
    /// 公開を許可する環境名。指定しない場合または空の場合は、すべての環境を許可します。
    /// </param>
    /// <exception cref="ArgumentException">
    /// 公開終了時刻が公開開始時刻以前、または環境名が空です。
    /// </exception>
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

    /// <summary>ページタイトルを取得します。</summary>
    public string? Title { get; }

    /// <summary>ページの説明を取得します。</summary>
    public string? Description { get; }

    /// <summary>下書きとして常に公開対象外にするかどうかを取得します。</summary>
    public bool Draft { get; }

    /// <summary>UTC に正規化された、公開範囲に含まれる開始時刻を取得します。</summary>
    public DateTimeOffset? PublishFrom { get; }

    /// <summary>UTC に正規化された、公開範囲に含まれない終了時刻を取得します。</summary>
    public DateTimeOffset? PublishUntil { get; }

    /// <summary>
    /// 公開を許可する環境名を取得します。空の場合は、すべての環境を許可します。
    /// </summary>
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
