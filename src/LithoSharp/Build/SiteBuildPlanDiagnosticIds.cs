namespace LithoSharp.Build;

/// <summary>ビルド計画の検証で使用する安定した診断識別子を提供します。</summary>
public static class SiteBuildPlanDiagnosticIds
{
    /// <summary>ビルドノード識別子が重複しています。</summary>
    public const string DuplicateNodeId = "LSB001";

    /// <summary>成果物識別子が重複しています。</summary>
    public const string DuplicateArtifactId = "LSB002";

    /// <summary>成果物の相対出力パスが無効です。</summary>
    public const string InvalidArtifactPath = "LSB003";

    /// <summary>成果物の所有者と格納先ノードが一致しません。</summary>
    public const string ArtifactOwnerMismatch = "LSB004";

    /// <summary>依存先のビルドノードが存在しません。</summary>
    public const string MissingDependency = "LSB005";

    /// <summary>ビルドノード依存関係に循環があります。</summary>
    public const string DependencyCycle = "LSB006";

    /// <summary>同じ出力パスを複数の成果物が使用しています。</summary>
    public const string DuplicateOutputPath = "LSB007";

    /// <summary>大文字と小文字だけが異なる出力パスを複数の成果物が使用しています。</summary>
    public const string OutputPathCaseCollision = "LSB008";

    /// <summary>成果物の出力パスにファイルとディレクトリの祖先競合があります。</summary>
    public const string OutputPathAncestorConflict = "LSB009";
}
