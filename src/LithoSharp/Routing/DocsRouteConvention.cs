namespace LithoSharp.Routing;

/// <summary>
/// 既存の Docs 出力パスを共通ルートへ変換する互換規約です。
/// </summary>
internal static class DocsRouteConvention
{
    /// <summary>既存の Markdown 出力パスからファイル形式のルートを作成します。</summary>
    /// <param name="relativeOutputPath">既存の相対出力パス。</param>
    /// <param name="baseUrl">サイトの絶対 HTTP(S) ベース URL。</param>
    /// <returns>現在の <c>.html</c> URL を保持したルート。</returns>
    public static SiteRoute ForMarkdownPost(string relativeOutputPath, string? baseUrl = null) =>
        SiteRoute.ForFile(relativeOutputPath, baseUrl);

    /// <summary>既存の追加ページ出力パスからファイル形式のルートを作成します。</summary>
    /// <param name="relativeOutputPath">既存の相対出力パス。</param>
    /// <param name="baseUrl">サイトの絶対 HTTP(S) ベース URL。</param>
    /// <returns>現在の <c>.html</c> URL を保持したルート。</returns>
    public static SiteRoute ForExtraPage(string relativeOutputPath, string? baseUrl = null) =>
        SiteRoute.ForFile(relativeOutputPath, baseUrl);
}
