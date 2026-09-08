namespace LithoSharp.Routing;

/// <summary>旧ルートから新ルートへのリダイレクトを表します。</summary>
public sealed record SiteRedirect
{
    /// <summary>リダイレクトを作成します。</summary>
    /// <param name="source">旧ルート。</param>
    /// <param name="target">新ルート。</param>
    /// <exception cref="ArgumentNullException">ルートが null です。</exception>
    /// <remarks>生成時にルート競合、循環、チェーン、宛先欠落を検証します。</remarks>
    public SiteRedirect(SiteRoute source, SiteRoute target)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    /// <summary>旧ルートを取得します。</summary>
    public SiteRoute Source { get; }
    /// <summary>新ルートを取得します。</summary>
    public SiteRoute Target { get; }
}
