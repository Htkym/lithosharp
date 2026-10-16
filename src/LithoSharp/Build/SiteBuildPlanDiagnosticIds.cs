namespace LithoSharp.Build;

public static class SiteBuildPlanDiagnosticIds
{
    public const string DuplicateNodeId = "LSB001";

    public const string DuplicateArtifactId = "LSB002";

    public const string InvalidArtifactPath = "LSB003";

    public const string ArtifactOwnerMismatch = "LSB004";

    public const string MissingDependency = "LSB005";

    public const string DependencyCycle = "LSB006";

    public const string DuplicateOutputPath = "LSB007";

    public const string OutputPathCaseCollision = "LSB008";

    public const string OutputPathAncestorConflict = "LSB009";
}
