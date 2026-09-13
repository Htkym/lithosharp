using System.Security.Cryptography;
using System.Text;
using LithoSharp.Diagnostics;

namespace LithoSharp.Documentation;

/// <summary>移行判定の区分を表します。</summary>
/// <remarks>C10の分類をそのまま公開します。CompatibleはAutomaticと同じ無修正区分です。</remarks>
public enum MigrationVerdict
{
    /// <summary>無修正でそのまま使えます。</summary>
    Automatic = 0,
    /// <summary>安全に書き換えられます。置換内容が記録されます。</summary>
    Convertible = 1,
    /// <summary>人の判断や編集が必要です。曖昧な変換は診断に留めます。</summary>
    ManualActionRequired = 2,
    /// <summary>変換できません。理由を報告します。</summary>
    Unsupported = 3,
}

/// <summary>安全な修正の適用形状を表します。</summary>
public enum MigrationActionKind
{
    /// <summary>StartLineからEndLineまでを行単位で置換します。空文字は削除です。</summary>
    ReplaceLines = 0,
    /// <summary>StartLineの後にReplacementTextの行を挿入します。</summary>
    InsertAfter = 1,
}

/// <summary>1件の移行所見を表します。</summary>
public sealed class MigrationIssueReport
{
    internal MigrationIssueReport(
        string id, SiteDiagnosticSeverity severity, string message,
        SiteSourceLocation location, string? replacement, string? manualStep)
    {
        Id = id;
        Severity = severity;
        Message = message;
        Location = location;
        Replacement = replacement;
        ManualStep = manualStep;
    }

    /// <summary>安定したLSMIG診断コードを取得します。</summary>
    public string Id { get; }

    /// <summary>所見の深刻度を取得します。</summary>
    public SiteDiagnosticSeverity Severity { get; }

    /// <summary>何を見つけたかと理由を取得します。</summary>
    public string Message { get; }

    /// <summary>元文書での位置を取得します。</summary>
    public SiteSourceLocation Location { get; }

    /// <summary>安全な書き換えの説明を取得します。ない場合は <see langword="null"/> です。</summary>
    public string? Replacement { get; }

    /// <summary>必要な手動操作を取得します。ない場合は <see langword="null"/> です。</summary>
    public string? ManualStep { get; }
}

/// <summary>1件の安全な修正候補を表します。</summary>
/// <remarks>
/// 行番号は解析時の元文書のものです。適用はfingerprintが一致する場合に限ります。
/// ReplaceLinesは行番号の降順に適用し、InsertAfterはその後に行います。
/// 曖昧なものと編集中に古くなったものは自動適用しません。
/// </remarks>
public sealed class MigrationSuggestedAction
{
    internal MigrationSuggestedAction(
        string sourcePath, MigrationActionKind kind, int startLine, int endLine,
        string replacementText, string sourceFingerprint, bool canApplyAutomatically, string applyCondition)
    {
        SourcePath = sourcePath;
        Kind = kind;
        StartLine = startLine;
        EndLine = endLine;
        ReplacementText = replacementText;
        SourceFingerprint = sourceFingerprint;
        CanApplyAutomatically = canApplyAutomatically;
        ApplyCondition = applyCondition;
    }

    /// <summary>対象のsource相対pathを取得します。</summary>
    public string SourcePath { get; }

    /// <summary>適用形状を取得します。</summary>
    public MigrationActionKind Kind { get; }

    /// <summary>1-basedの開始行を取得します。InsertAfterは挿入先の行で、0は先頭への挿入です。</summary>
    public int StartLine { get; }

    /// <summary>1-basedの終了行を取得します。InsertAfterは開始行と同じです。</summary>
    public int EndLine { get; }

    /// <summary>置換textを取得します。削除は空文字、複数行の挿入は <c>\n</c> 区切りです。</summary>
    public string ReplacementText { get; }

    /// <summary>解析時の元文書バイト列の小文字hex SHA-256を取得します。</summary>
    public string SourceFingerprint { get; }

    /// <summary>自動適用できるかどうかを取得します。曖昧なものは false です。</summary>
    public bool CanApplyAutomatically { get; }

    /// <summary>適用条件を取得します。</summary>
    public string ApplyCondition { get; }

    /// <summary>内容が解析時と一致するかどうかをバイト列で判定します。</summary>
    /// <param name="content">現在の文書バイト列。</param>
    /// <returns>一致する場合に <see langword="true"/> を返します。</returns>
    public bool MatchesSource(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), SourceFingerprint, StringComparison.Ordinal);
    }

    /// <summary>内容が解析時と一致するかどうかをtextで判定します。</summary>
    /// <param name="content">現在の文書text。BOMなしUTF-8として符号化して照合します。</param>
    /// <returns>一致する場合に <see langword="true"/> を返します。</returns>
    /// <remarks>BOM付き文書はバイト列で照合してください。</remarks>
    public bool MatchesSourceText(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return MatchesSource(Encoding.UTF8.GetBytes(content));
    }
}

/// <summary>1文書の移行結果を表します。</summary>
public sealed class MigrationFileReport
{
    internal MigrationFileReport(
        string sourcePath, MigrationVerdict verdict, string? convertedPath,
        IReadOnlyList<MigrationIssueReport> issues, IReadOnlyList<MigrationSuggestedAction> suggestedActions,
        string sourceFingerprint)
    {
        SourcePath = sourcePath;
        Verdict = verdict;
        ConvertedPath = convertedPath;
        Issues = issues;
        SuggestedActions = suggestedActions;
        SourceFingerprint = sourceFingerprint;
    }

    /// <summary>source相対pathを取得します。</summary>
    public string SourcePath { get; }

    /// <summary>文書の判定を取得します。</summary>
    public MigrationVerdict Verdict { get; }

    /// <summary>変換先の相対pathを取得します。参照専用は <see langword="null"/> です。</summary>
    public string? ConvertedPath { get; }

    /// <summary>所見を取得します。深刻度順・行順です。</summary>
    public IReadOnlyList<MigrationIssueReport> Issues { get; }

    /// <summary>安全な修正候補を取得します。将来のQuick Fixは移行判定を再実装せずに使えます。</summary>
    public IReadOnlyList<MigrationSuggestedAction> SuggestedActions { get; }

    /// <summary>解析時の元文書バイト列の小文字hex SHA-256を取得します。</summary>
    public string SourceFingerprint { get; }
}

/// <summary>1件のcollection variant配線の提案を表します。</summary>
public sealed class MigrationVariantReport
{
    internal MigrationVariantReport(string version, string locale, string inputDirectory, string suggestedRoutePrefix)
    {
        Version = version;
        Locale = locale;
        InputDirectory = inputDirectory;
        SuggestedRoutePrefix = suggestedRoutePrefix;
    }

    /// <summary>versionを取得します。</summary>
    public string Version { get; }

    /// <summary>localeを取得します。</summary>
    public string Locale { get; }

    /// <summary>入力ディレクトリを取得します。</summary>
    public string InputDirectory { get; }

    /// <summary>提案route prefixを取得します。所有者が検証します。</summary>
    public string SuggestedRoutePrefix { get; }
}

/// <summary>blog著者プロファイル登録の提案を表します。</summary>
public sealed class MigrationAuthorReport
{
    internal MigrationAuthorReport(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>著者IDを取得します。</summary>
    public string Id { get; }

    /// <summary>著者名を取得します。</summary>
    public string Name { get; }
}

/// <summary>サイト単位の変換案内を表します。</summary>
public sealed class MigrationManifestReport
{
    internal MigrationManifestReport(
        IReadOnlyList<MigrationVariantReport> variants,
        IReadOnlyDictionary<string, string> versionedSidebars,
        IReadOnlyList<MigrationAuthorReport> blogAuthors,
        IReadOnlyList<string> manualSteps,
        IReadOnlyList<string> unsupportedNotes)
    {
        Variants = variants;
        VersionedSidebars = versionedSidebars;
        BlogAuthors = blogAuthors;
        ManualSteps = manualSteps;
        UnsupportedNotes = unsupportedNotes;
    }

    /// <summary>variant配線の提案を取得します。</summary>
    public IReadOnlyList<MigrationVariantReport> Variants { get; }

    /// <summary>version付きsidebar対応を取得します。</summary>
    public IReadOnlyDictionary<string, string> VersionedSidebars { get; }

    /// <summary>blog著者の提案を取得します。</summary>
    public IReadOnlyList<MigrationAuthorReport> BlogAuthors { get; }

    /// <summary>手動手順を取得します。</summary>
    public IReadOnlyList<string> ManualSteps { get; }

    /// <summary>未対応の注記を取得します。</summary>
    public IReadOnlyList<string> UnsupportedNotes { get; }
}

/// <summary>1件の変換後公開routeを表します。</summary>
public sealed class MigrationRouteReport
{
    internal MigrationRouteReport(string route, string sourceFile, string kind)
    {
        Route = route;
        SourceFile = sourceFile;
        Kind = kind;
    }

    /// <summary>公開routeを取得します。</summary>
    public string Route { get; }

    /// <summary>由来文書を取得します。</summary>
    public string SourceFile { get; }

    /// <summary>文書種別を取得します。</summary>
    public string Kind { get; }
}

/// <summary>文書集計を表します。</summary>
public sealed class MigrationSummary
{
    internal MigrationSummary(int totalFiles, int automatic, int convertible, int manualActionRequired, int unsupported)
    {
        TotalFiles = totalFiles;
        Automatic = automatic;
        Convertible = convertible;
        ManualActionRequired = manualActionRequired;
        Unsupported = unsupported;
    }

    /// <summary>文書数を取得します。</summary>
    public int TotalFiles { get; }

    /// <summary>無修正で使える文書数を取得します。</summary>
    public int Automatic { get; }

    /// <summary>安全に書き換えられる文書数を取得します。</summary>
    public int Convertible { get; }

    /// <summary>人の判断が必要な文書数を取得します。</summary>
    public int ManualActionRequired { get; }

    /// <summary>変換できない文書数を取得します。</summary>
    public int Unsupported { get; }
}

/// <summary>Docusaurus移行の構造化reportを表します。</summary>
/// <remarks>
/// C10の判定・集計・修正候補をCLIと同じ処理から公開します。schema versionは <see cref="SchemaVersion"/> です。
/// </remarks>
public sealed class DocusaurusMigrationReport
{
    internal DocusaurusMigrationReport(
        string schemaVersion, bool dryRun, bool wroteOutput, string? destination,
        MigrationSummary summary, IReadOnlyList<MigrationFileReport> files,
        MigrationManifestReport manifest, IReadOnlyList<MigrationRouteReport> convertedRoutes,
        IReadOnlyList<string> missingRoutes, IReadOnlyList<string> extraRoutes, int exitCode)
    {
        SchemaVersion = schemaVersion;
        DryRun = dryRun;
        WroteOutput = wroteOutput;
        Destination = destination;
        Summary = summary;
        Files = files;
        Manifest = manifest;
        ConvertedRoutes = convertedRoutes;
        MissingRoutes = missingRoutes;
        ExtraRoutes = extraRoutes;
        ExitCode = exitCode;
    }

    /// <summary>reportのschema versionを取得します。</summary>
    public string SchemaVersion { get; }

    /// <summary>書き込まないdry-runかどうかを取得します。</summary>
    public bool DryRun { get; }

    /// <summary>別出力先へ書き込んだかどうかを取得します。</summary>
    public bool WroteOutput { get; }

    /// <summary>変換先を取得します。dry-runは <see langword="null"/> です。</summary>
    public string? Destination { get; }

    /// <summary>文書集計を取得します。</summary>
    public MigrationSummary Summary { get; }

    /// <summary>文書ごとの判定・所見・修正候補を取得します。</summary>
    public IReadOnlyList<MigrationFileReport> Files { get; }

    /// <summary>サイト単位の変換案内を取得します。</summary>
    public MigrationManifestReport Manifest { get; }

    /// <summary>変換後の公開routeを取得します。</summary>
    public IReadOnlyList<MigrationRouteReport> ConvertedRoutes { get; }

    /// <summary>期待されたが得られなかったrouteを取得します。</summary>
    public IReadOnlyList<string> MissingRoutes { get; }

    /// <summary>期待にない変換後routeを取得します。</summary>
    public IReadOnlyList<string> ExtraRoutes { get; }

    /// <summary>終了コードを取得します。0は完了、1は失敗、3は変換不能内容ありです。</summary>
    public int ExitCode { get; }

    /// <summary>書き込まずに解析します。入力先以外には触れません。</summary>
    /// <param name="sourceDirectory">移行元ディレクトリ。</param>
    /// <param name="expectedRoutes">期待route一覧。正規形のpublic-pathで指定します。ない場合は <see langword="null"/> です。</param>
    /// <param name="options">移行の前提。ない場合は既定値です。</param>
    /// <param name="cancellationToken">取消token。</param>
    /// <returns>CLIと同じ判定・集計・修正候補を持つreport。</returns>
    public static DocusaurusMigrationReport Analyze(
        string sourceDirectory,
        IReadOnlyList<string>? expectedRoutes = null,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        FromResult(DocusaurusMigration.Analyze(sourceDirectory, expectedRoutes, options, cancellationToken),
            dryRun: true, destination: null);

    /// <summary>明示した別出力先へ変換します。既存入力を上書きしません。</summary>
    /// <param name="sourceDirectory">移行元ディレクトリ。</param>
    /// <param name="destinationDirectory">存在しない変換先ディレクトリ。</param>
    /// <param name="expectedRoutes">期待route一覧。正規形のpublic-pathで指定します。ない場合は <see langword="null"/> です。</param>
    /// <param name="options">移行の前提。ない場合は既定値です。</param>
    /// <param name="cancellationToken">取消token。</param>
    /// <returns>CLIと同じ判定・集計・修正候補を持つreport。</returns>
    public static async Task<DocusaurusMigrationReport> ConvertAsync(
        string sourceDirectory,
        string destinationDirectory,
        IReadOnlyList<string>? expectedRoutes = null,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        FromResult(await DocusaurusMigration.ConvertAsync(sourceDirectory, Path.GetFullPath(destinationDirectory), expectedRoutes, options, cancellationToken).ConfigureAwait(false),
            dryRun: false, destination: Path.GetFullPath(destinationDirectory));

    internal static DocusaurusMigrationReport FromResult(DocusaurusMigrationResult result, bool dryRun, string? destination)
    {
        var files = result.Files.Select(file =>
        {
            var verdict = file.Verdict switch
            {
                DocusaurusMigrationVerdict.Automatic => MigrationVerdict.Automatic,
                DocusaurusMigrationVerdict.Convertible => MigrationVerdict.Convertible,
                DocusaurusMigrationVerdict.ManualActionRequired => MigrationVerdict.ManualActionRequired,
                _ => MigrationVerdict.Unsupported,
            };
            var issues = file.Issues.Select(issue => new MigrationIssueReport(
                issue.Id, issue.Severity, issue.Message,
                new SiteSourceLocation(file.SourcePath, issue.Line, null),
                issue.Replacement, issue.ManualStep)).ToArray();
            var automatic = verdict is MigrationVerdict.Automatic or MigrationVerdict.Convertible;
            var manual = issues.FirstOrDefault(issue => issue.ManualStep is not null)?.ManualStep;
            var actions = file.Edits.Select(edit => new MigrationSuggestedAction(
                file.SourcePath, edit.Kind, edit.StartLine, edit.EndLine, edit.Text, file.SourceFingerprint,
                automatic,
                automatic
                    ? $"Apply only when '{file.SourcePath}' still matches fingerprint {file.SourceFingerprint}; re-run the migration analysis after any edit."
                    : $"Not automatically applicable: '{file.SourcePath}' needs manual actions (verdict {verdict})."
                        + (manual is null ? "" : $" First step: {manual}"))).ToArray();
            return new MigrationFileReport(file.SourcePath, verdict, file.ConvertedPath, issues, actions, file.SourceFingerprint);
        }).ToArray();
        return new DocusaurusMigrationReport(
            ToolingContracts.MigrationReport.SchemaVersion, dryRun, result.WroteOutput, destination,
            new MigrationSummary(
                files.Length,
                files.Count(file => file.Verdict == MigrationVerdict.Automatic),
                files.Count(file => file.Verdict == MigrationVerdict.Convertible),
                files.Count(file => file.Verdict == MigrationVerdict.ManualActionRequired),
                files.Count(file => file.Verdict == MigrationVerdict.Unsupported)),
            files,
            new MigrationManifestReport(
                result.Manifest.Variants.Select(variant => new MigrationVariantReport(
                    variant.Version, variant.Locale, variant.InputDirectory, variant.SuggestedRoutePrefix)).ToArray(),
                new SortedDictionary<string, string>(
                    result.Manifest.VersionedSidebars.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal),
                result.Manifest.BlogAuthors.Select(author => new MigrationAuthorReport(author.Id, author.Name)).ToArray(),
                result.Manifest.ManualSteps.ToArray(),
                result.Manifest.UnsupportedNotes.ToArray()),
            result.ConvertedRoutes.Select(route => new MigrationRouteReport(route.Route, route.SourceFile, route.Kind)).ToArray(),
            result.MissingRoutes.ToArray(),
            result.ExtraRoutes.ToArray(),
            result.ExitCode);
    }
}
