namespace LithoSharp.Routing;

/// <summary>ルート検証で使用する安定した診断識別子を提供します。</summary>
public static class SiteRouteDiagnosticIds
{
    /// <summary>公開パスが複数のルートに登録されています。</summary>
    public const string DuplicatePublicPath = "LSR001";

    /// <summary>出力パスが複数のルートに登録されています。</summary>
    public const string DuplicateOutputPath = "LSR002";

    /// <summary>大文字と小文字だけが異なる出力パスが登録されています。</summary>
    public const string OutputPathCaseCollision = "LSR003";

    /// <summary>予約済み成果物の出力パスを別の所有者が使用しています。</summary>
    public const string ReservedOutputPathCollision = "LSR004";

    /// <summary>出力ルート外を指す、または安全でない出力パスが登録されています。</summary>
    public const string InvalidRoute = "LSR005";

    /// <summary>ある出力ファイルのパスが、別の出力パスの祖先として登録されています。</summary>
    public const string OutputPathAncestorConflict = "LSR006";
}
