using LithoSharp.Diagnostics;

namespace LithoSharp.Inspection;

/// <summary>文書見出しの検査情報を表します。</summary>
public sealed class DocumentHeadingInfo
{
    internal DocumentHeadingInfo(string text, string? id, int rawLevel, int outputLevel, SiteSourceLocation? location)
    {
        Text = text;
        Id = id;
        RawLevel = rawLevel;
        OutputLevel = outputLevel;
        Location = location;
    }

    /// <summary>見出しの本文を取得します。</summary>
    public string Text { get; }

    /// <summary>自動採番した見出し識別子を取得します。</summary>
    public string? Id { get; }

    /// <summary>記述した見出しlevelを取得します。</summary>
    public int RawLevel { get; }

    /// <summary>描画する見出しlevelを取得します。</summary>
    public int OutputLevel { get; }

    /// <summary>元文書での位置を取得します。</summary>
    public SiteSourceLocation? Location { get; }
}

/// <summary>文書リンクの検査情報を表します。</summary>
public sealed class DocumentLinkInfo
{
    internal DocumentLinkInfo(string rawText, string url, string? title, bool isImage, SiteSourceLocation? location)
    {
        RawText = rawText;
        Url = url;
        Title = title;
        IsImage = isImage;
        Location = location;
    }

    /// <summary>記述した原文を取得します。</summary>
    public string RawText { get; }

    /// <summary>解決したURLを取得します。</summary>
    public string Url { get; }

    /// <summary>リンク題を取得します。</summary>
    public string? Title { get; }

    /// <summary>画像かどうかを取得します。</summary>
    public bool IsImage { get; }

    /// <summary>元文書での位置を取得します。</summary>
    public SiteSourceLocation? Location { get; }
}

/// <summary>文書asset参照の検査情報を表します。</summary>
public sealed class DocumentAssetInfo
{
    internal DocumentAssetInfo(string url, SiteSourceLocation? location)
    {
        Url = url;
        Location = location;
    }

    /// <summary>記述したasset URLを取得します。</summary>
    public string Url { get; }

    /// <summary>元文書での位置を取得します。</summary>
    public SiteSourceLocation? Location { get; }
}

/// <summary>文書component参照の検査情報を表します。</summary>
public sealed class DocumentComponentInfo
{
    internal DocumentComponentInfo(string name, SiteSourceLocation? location)
    {
        Name = name;
        Location = location;
    }

    /// <summary>component名を取得します。</summary>
    public string Name { get; }

    /// <summary>元文書での位置を取得します。</summary>
    public SiteSourceLocation? Location { get; }
}

/// <summary>文書検査へ渡す追加情報を表します。</summary>
public sealed class DocumentInspectionOptions
{
    /// <summary>文書識別子を取得または設定します。未設定時はsource pathを使います。</summary>
    public string? DocumentId { get; init; }

    /// <summary>公開routeを取得または設定します。不明な場合は <see langword="null"/> です。</summary>
    public string? Route { get; init; }

    /// <summary>文書versionを取得または設定します。不明な場合は <see langword="null"/> です。</summary>
    public string? Version { get; init; }

    /// <summary>文書localeを取得または設定します。不明な場合は <see langword="null"/> です。</summary>
    public string? Locale { get; init; }

    /// <summary>
    /// 未対応構文の互換性advisory（LIT003 定義リスト、LIT004 汎用属性、LIT005 grid table）を
    /// 付けるかどうかを取得または設定します。既定は <see langword="false"/> で、既存の既定動作
    /// （LIT001 脚注、LIT002 ブラウザー資産のみ）は変わりません。文書本文の解釈や描画には影響しません。
    /// </summary>
    public bool EnableCompatibilityAdvisory { get; init; }

    /// <summary>
    /// 明示操作で取得したproject contextを取得または設定します。ない場合は <see langword="null"/> で、
    /// 構文解析のみを行います。context不足を成功として隠さず、未保存解析で公開出力も元ファイルも更新しません。
    /// </summary>
    public ProjectInspectionSnapshot? Project { get; init; }
}

/// <summary><c>--advisory</c> optionの契約を表します。</summary>
/// <remarks>CLIの明示検査と検査optionはこの解析に接続し、未知値はusage errorにします。</remarks>
public static class CompatibilityAdvisoryOption
{
    /// <summary>advisoryを有効にする値を取得します。</summary>
    public const string On = "on";

    /// <summary>advisoryを無効にする値を取得します。</summary>
    public const string Off = "off";

    /// <summary>option値を解釈します。</summary>
    /// <param name="value">利用者が指定した値。</param>
    /// <param name="enabled">advisoryを有効にするかどうか。</param>
    /// <returns>既知の値の場合に <see langword="true"/> を返します。</returns>
    public static bool TryParse(string? value, out bool enabled)
    {
        if (string.Equals(value, On, StringComparison.Ordinal))
        {
            enabled = true;
            return true;
        }

        if (string.Equals(value, Off, StringComparison.Ordinal))
        {
            enabled = false;
            return true;
        }

        enabled = false;
        return false;
    }
}

/// <summary>一度の解析から得た文書情報の不変snapshotを表します。</summary>
public sealed class DocumentInfo
{
    internal DocumentInfo(
        string documentId,
        string sourcePath,
        string? title,
        IReadOnlyList<DocumentHeadingInfo> headings,
        IReadOnlyList<DocumentLinkInfo> links,
        IReadOnlyList<DocumentAssetInfo> assets,
        string? route,
        IReadOnlyList<DocumentComponentInfo> components,
        IReadOnlyDictionary<string, object?> frontMatter,
        string? version,
        string? locale,
        IReadOnlyList<SiteDiagnostic> diagnostics,
        IReadOnlyList<ProjectRouteCandidate>? routeCandidates = null,
        DocumentProjectStatus projectStatus = DocumentProjectStatus.NoContext,
        string? schemaName = null)
    {
        DocumentId = documentId;
        SourcePath = sourcePath;
        Title = title;
        Headings = Array.AsReadOnly(headings.ToArray());
        Links = Array.AsReadOnly(links.ToArray());
        Assets = Array.AsReadOnly(assets.ToArray());
        Route = route;
        Components = Array.AsReadOnly(components.ToArray());
        FrontMatter = frontMatter;
        Version = version;
        Locale = locale;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        RouteCandidates = Array.AsReadOnly((routeCandidates ?? []).ToArray());
        ProjectStatus = projectStatus;
        SchemaName = schemaName;
    }

    /// <summary>文書識別子を取得します。</summary>
    public string DocumentId { get; }

    /// <summary>読み取ったsource pathを取得します。</summary>
    public string SourcePath { get; }

    /// <summary>文書題を取得します。ない場合は <see langword="null"/> です。</summary>
    public string? Title { get; }

    /// <summary>文書順の見出しを取得します。</summary>
    public IReadOnlyList<DocumentHeadingInfo> Headings { get; }

    /// <summary>文書順のリンクと画像を取得します。</summary>
    public IReadOnlyList<DocumentLinkInfo> Links { get; }

    /// <summary>文書順のasset参照を取得します。</summary>
    public IReadOnlyList<DocumentAssetInfo> Assets { get; }

    /// <summary>公開routeを取得します。不明な場合は <see langword="null"/> です。</summary>
    public string? Route { get; }

    /// <summary>文書順のcomponent参照を取得します。</summary>
    public IReadOnlyList<DocumentComponentInfo> Components { get; }

    /// <summary>front matterの対応表を取得します。</summary>
    public IReadOnlyDictionary<string, object?> FrontMatter { get; }

    /// <summary>文書versionを取得します。不明な場合は <see langword="null"/> です。</summary>
    public string? Version { get; }

    /// <summary>文書localeを取得します。不明な場合は <see langword="null"/> です。</summary>
    public string? Locale { get; }

    /// <summary>解析で得た診断を取得します。</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }

    /// <summary>project context内のsource→route候補を取得します。contextなしでは空です。</summary>
    public IReadOnlyList<ProjectRouteCandidate> RouteCandidates { get; }

    /// <summary>project context付き検査のroute解決状態を取得します。</summary>
    public DocumentProjectStatus ProjectStatus { get; }

    /// <summary>front matter検証に使ったschema名を取得します。contextなしでは <see langword="null"/> です。</summary>
    public string? SchemaName { get; }
}
