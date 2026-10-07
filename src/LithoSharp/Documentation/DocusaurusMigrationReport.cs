using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;

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

/// <summary>手動で置き換える部品が与える機能差を表します。</summary>
public enum MigrationComponentChangeKind
{
    /// <summary>見た目が変わります。</summary>
    AppearanceChanged,
    /// <summary>操作方法や操作可能な機能が変わります。</summary>
    InteractionChanged,
    /// <summary>動的な部品を静的な内容に置き換えます。</summary>
    Staticized,
    /// <summary>元の部品や機能を削除します。</summary>
    Deleted,
    /// <summary>動作を確認できず、同等性を判断していません。</summary>
    Unverified,
}

/// <summary>手動置換後の機能同等性を表します。</summary>
public enum MigrationFunctionalEquivalence
{
    /// <summary>元と同じ機能を保つと確認済みです。</summary>
    Equivalent,
    /// <summary>元と異なる機能になります。</summary>
    NotEquivalent,
    /// <summary>確認できていません。</summary>
    Unverified,
}

/// <summary>原本routeの分類を表します。非Document分類と明示除外routeは文書page set比較から外します。</summary>
public enum MigrationRouteCategory
{
    /// <summary>種類が宣言されていないrouteです。</summary>
    Unclassified,
    /// <summary>文書またはblog記事のpage routeです。</summary>
    Document,
    /// <summary>category indexです。</summary>
    CategoryIndex,
    /// <summary>blog indexです。</summary>
    BlogIndex,
    /// <summary>blog author indexです。</summary>
    BlogAuthor,
    /// <summary>blog tag indexです。</summary>
    BlogTag,
    /// <summary>blog archiveです。</summary>
    BlogArchive,
    /// <summary>blog pagination routeです。</summary>
    BlogPagination,
    /// <summary>その他のrouteです。</summary>
    Other,
}

/// <summary>原本route oracleの1件を表します。</summary>
public sealed class MigrationRouteOracleEntry
{
    /// <summary>oracle routeを作成します。</summary>
    /// <param name="path">原本の公開pathを、比較元の表記のまま指定します。</param>
    /// <param name="category">相互排他的なroute分類です。</param>
    /// <param name="locale">locale facetです。route分母には重ねて加算しません。</param>
    /// <param name="exclusionReason">指定するとpage setから除外する理由です。文書以外のrouteは既定で除外されます。</param>
    public MigrationRouteOracleEntry(
        string path,
        MigrationRouteCategory category,
        string? locale = null,
        string? exclusionReason = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category));
        if (exclusionReason is not null && string.IsNullOrWhiteSpace(exclusionReason))
            throw new ArgumentException("An explicit exclusion reason cannot be empty.", nameof(exclusionReason));
        Path = path;
        Category = category;
        Locale = locale;
        ExclusionReason = exclusionReason;
    }

    /// <summary>原本の公開pathを取得します。</summary>
    public string Path { get; }

    /// <summary>routeの分類を取得します。</summary>
    public MigrationRouteCategory Category { get; }

    /// <summary>locale facetを取得します。指定がない場合は <see langword="null"/> です。</summary>
    public string? Locale { get; }

    /// <summary>page setからの除外理由を取得します。文書routeに設定した場合も除外されます。</summary>
    public string? ExclusionReason { get; }
}

/// <summary>比較対象の原本route集合と、その出典情報を表します。</summary>
public sealed class MigrationRouteOracle
{
    /// <summary>route oracleを作成します。</summary>
    /// <param name="routes">原本routeと分類。</param>
    /// <param name="sourceVersion">原本サイトのversion。未指定なら検出できた場合だけreportに載せます。</param>
    /// <param name="basePath">原本の公開base path。正規化比較でのみ明示的に除去します。</param>
    public MigrationRouteOracle(
        IReadOnlyList<MigrationRouteOracleEntry> routes,
        string? sourceVersion = null,
        string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var snapshot = routes.ToArray();
        if (snapshot.Any(route => route is null)) throw new ArgumentException("A route oracle cannot contain null entries.", nameof(routes));
        foreach (var group in snapshot.GroupBy(route => route.Path, StringComparer.Ordinal))
        {
            var classifications = group.Select(route => (route.Category, route.Locale, route.ExclusionReason)).Distinct().Take(2).Count();
            if (classifications > 1)
                throw new ArgumentException($"Route '{group.Key}' has conflicting oracle classifications.", nameof(routes));
        }

        Routes = Array.AsReadOnly(snapshot);
        SourceVersion = sourceVersion;
        BasePath = basePath;
    }

    /// <summary>oracle routeを取得します。</summary>
    public IReadOnlyList<MigrationRouteOracleEntry> Routes { get; }

    /// <summary>原本サイトのversionを取得します。</summary>
    public string? SourceVersion { get; }

    /// <summary>原本の公開base pathを取得します。</summary>
    public string? BasePath { get; }

    /// <summary>従来のpath配列をroute kind未分類のoracleへ変換します。</summary>
    /// <param name="paths">公開path一覧。</param>
    /// <returns>page setの範囲を推測しないoracle。</returns>
    public static MigrationRouteOracle FromPaths(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new(paths.Select(path => new MigrationRouteOracleEntry(path, MigrationRouteCategory.Unclassified)).ToArray());
    }
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

/// <summary>文書内で検出した手動component置換と、その機能差を表します。</summary>
public sealed class MigrationComponentChangeReport
{
    internal MigrationComponentChangeReport(
        string sourcePath,
        string component,
        MigrationComponentChangeKind kind,
        MigrationFunctionalEquivalence functionalEquivalence,
        string note)
    {
        SourcePath = sourcePath;
        Component = component;
        Kind = kind;
        FunctionalEquivalence = functionalEquivalence;
        Note = note;
    }

    /// <summary>検出元のsource相対pathを取得します。</summary>
    public string SourcePath { get; }

    /// <summary>部品名またはimport名を取得します。</summary>
    public string Component { get; }

    /// <summary>置換後に起きる主な変化を取得します。</summary>
    public MigrationComponentChangeKind Kind { get; }

    /// <summary>機能同等性の判定を取得します。</summary>
    public MigrationFunctionalEquivalence FunctionalEquivalence { get; }

    /// <summary>必要な手動確認や既知の機能差を取得します。</summary>
    public string Note { get; }

    /// <summary>migrationが自動適用できるかどうかを取得します。手動component置換は常に <see langword="false"/> です。</summary>
    public bool CanApplyAutomatically => false;
}

/// <summary>1文書の移行結果を表します。</summary>
public sealed class MigrationFileReport
{
    internal MigrationFileReport(
        string sourcePath, MigrationVerdict verdict, string? convertedPath,
        IReadOnlyList<MigrationIssueReport> issues, IReadOnlyList<MigrationSuggestedAction> suggestedActions,
        string sourceFingerprint, IReadOnlyList<MigrationComponentChangeReport> componentChanges)
    {
        SourcePath = sourcePath;
        Verdict = verdict;
        ConvertedPath = convertedPath;
        Issues = issues;
        SuggestedActions = suggestedActions;
        SourceFingerprint = sourceFingerprint;
        ComponentChanges = componentChanges;
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

    /// <summary>手動component置換の分類と機能差を取得します。</summary>
    public IReadOnlyList<MigrationComponentChangeReport> ComponentChanges { get; }
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

/// <summary>route comparisonの状態を表します。</summary>
public enum MigrationRouteComparisonStatus
{
    /// <summary>原本route oracleがなく、比較していません。</summary>
    NotCompared,
    /// <summary>指定scopeの比較対象が一致しました。</summary>
    Match,
    /// <summary>指定scopeにmissingまたはextra routeがあります。</summary>
    Differences,
}

/// <summary>正規化page set比較の状態を表します。</summary>
public enum MigrationPageSetComparisonStatus
{
    /// <summary>正規化比較を明示的に要求していません。</summary>
    NotRequested,
    /// <summary>分類済み文書routeがないか、oracleがなく比較できません。</summary>
    NotCompared,
    /// <summary>宣言した文書page setが一致しました。</summary>
    Match,
    /// <summary>宣言した文書page setに差があります。</summary>
    Differences,
}

/// <summary>page set比較から除外した原本route群を表します。</summary>
public sealed class MigrationRouteExclusionReport
{
    internal MigrationRouteExclusionReport(MigrationRouteCategory category, int count, string reason)
    {
        Category = category;
        Count = count;
        Reason = reason;
    }

    /// <summary>除外したroute分類を取得します。</summary>
    public MigrationRouteCategory Category { get; }

    /// <summary>重複を除いたroute数を取得します。</summary>
    public int Count { get; }

    /// <summary>page setから除外する理由を取得します。</summary>
    public string Reason { get; }
}

/// <summary>locale別の原本route数を表します。全体分母へ重複加算しないfacetです。</summary>
public sealed class MigrationLocaleCountReport
{
    internal MigrationLocaleCountReport(string locale, int routeCount)
    {
        Locale = locale;
        RouteCount = routeCount;
    }

    /// <summary>locale名を取得します。</summary>
    public string Locale { get; }

    /// <summary>このlocaleに属する重複を除いたroute数を取得します。</summary>
    public int RouteCount { get; }
}

/// <summary>明示的な正規化を適用した文書page set比較を表します。</summary>
public sealed class MigrationPageSetComparisonReport
{
    internal MigrationPageSetComparisonReport(
        MigrationPageSetComparisonStatus status,
        int? sourcePageCount,
        int? normalizedSourcePageCount,
        int? targetPageCount,
        int? normalizedTargetPageCount,
        IReadOnlyList<string> missingRoutes,
        IReadOnlyList<string> extraRoutes,
        IReadOnlyList<string> rules,
        string? reason)
    {
        Status = status;
        SourcePageCount = sourcePageCount;
        NormalizedSourcePageCount = normalizedSourcePageCount;
        TargetPageCount = targetPageCount;
        NormalizedTargetPageCount = normalizedTargetPageCount;
        MissingRoutes = missingRoutes;
        ExtraRoutes = extraRoutes;
        Rules = rules;
        Reason = reason;
    }

    /// <summary>正規化page set比較の状態を取得します。</summary>
    public MigrationPageSetComparisonStatus Status { get; }

    /// <summary>分類済み原本文書route数を取得します。比較しない場合は <see langword="null"/> です。</summary>
    public int? SourcePageCount { get; }

    /// <summary>正規化後の異なる原本page数を取得します。</summary>
    public int? NormalizedSourcePageCount { get; }

    /// <summary>比較対象の変換後route数を取得します。</summary>
    public int? TargetPageCount { get; }

    /// <summary>正規化後の異なる変換後page数を取得します。</summary>
    public int? NormalizedTargetPageCount { get; }

    /// <summary>正規化後に不足したrouteを取得します。</summary>
    public IReadOnlyList<string> MissingRoutes { get; }

    /// <summary>正規化後に余分なrouteを取得します。</summary>
    public IReadOnlyList<string> ExtraRoutes { get; }

    /// <summary>この比較で実際に使った正規化規則を取得します。</summary>
    public IReadOnlyList<string> Rules { get; }

    /// <summary>比較できなかった理由を取得します。比較した場合は <see langword="null"/> です。</summary>
    public string? Reason { get; }
}

/// <summary>原本oracle、正確比較、対象分母、除外、出典をまとめたroute reportです。</summary>
public sealed class MigrationRouteComparisonReport
{
    internal MigrationRouteComparisonReport(
        MigrationRouteComparisonStatus rawStatus,
        int? sourceRouteCount,
        int targetRouteCount,
        int? excludedRouteCount,
        int? duplicateSourceRouteCount,
        string? sourceVersion,
        string sourceHash,
        string? routeOracleHash,
        IReadOnlyList<MigrationRouteExclusionReport> exclusionRules,
        IReadOnlyList<MigrationLocaleCountReport> locales,
        MigrationPageSetComparisonReport normalizedPageSet)
    {
        RawStatus = rawStatus;
        SourceRouteCount = sourceRouteCount;
        TargetRouteCount = targetRouteCount;
        ExcludedRouteCount = excludedRouteCount;
        DuplicateSourceRouteCount = duplicateSourceRouteCount;
        SourceVersion = sourceVersion;
        SourceHash = sourceHash;
        RouteOracleHash = routeOracleHash;
        ExclusionRules = exclusionRules;
        Locales = locales;
        NormalizedPageSet = normalizedPageSet;
    }

    /// <summary>既存のraw public-path exact比較の状態を取得します。</summary>
    public MigrationRouteComparisonStatus RawStatus { get; }

    /// <summary>原本oracleの重複を除いたroute数を取得します。oracle未指定時は <see langword="null"/> です。</summary>
    public int? SourceRouteCount { get; }

    /// <summary>変換後の異なるraw route数を取得します。</summary>
    public int TargetRouteCount { get; }

    /// <summary>文書page set比較から除外したroute数を取得します。oracle未指定時は <see langword="null"/> です。</summary>
    public int? ExcludedRouteCount { get; }

    /// <summary>oracleに重複して書かれたroute entry数を取得します。</summary>
    public int? DuplicateSourceRouteCount { get; }

    /// <summary>明示またはpackage.jsonから得た原本Docusaurus versionを取得します。</summary>
    public string? SourceVersion { get; }

    /// <summary>解析対象source treeの相対pathとfile fingerprintから算出したSHA-256を取得します。</summary>
    public string SourceHash { get; }

    /// <summary>route oracleの内容から算出したSHA-256を取得します。oracle未指定時は <see langword="null"/> です。</summary>
    public string? RouteOracleHash { get; }

    /// <summary>分類ごとの除外数と理由を取得します。</summary>
    public IReadOnlyList<MigrationRouteExclusionReport> ExclusionRules { get; }

    /// <summary>locale別route数を取得します。各routeは全体分母に一度だけ含まれます。</summary>
    public IReadOnlyList<MigrationLocaleCountReport> Locales { get; }

    /// <summary>明示的に要求した文書page set比較を取得します。</summary>
    public MigrationPageSetComparisonReport NormalizedPageSet { get; }
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
        IReadOnlyList<string> missingRoutes, IReadOnlyList<string> extraRoutes, int exitCode,
        MigrationRouteComparisonReport routeComparison,
        IReadOnlyList<MigrationComponentChangeReport> componentChanges)
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
        RouteComparison = routeComparison;
        ComponentChanges = componentChanges;
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

    /// <summary>raw exactと明示的なpage set比較、分母、除外理由、出典情報を取得します。</summary>
    public MigrationRouteComparisonReport RouteComparison { get; }

    /// <summary>全source文書から検出した手動component置換を取得します。</summary>
    public IReadOnlyList<MigrationComponentChangeReport> ComponentChanges { get; }

    /// <summary>書き込まずに解析します。入力先以外には触れません。</summary>
    /// <param name="sourceDirectory">移行元ディレクトリ。</param>
    /// <param name="expectedRoutes">従来形式のraw exact比較用path一覧です。route種別は未分類になり、正規化page set比較には使いません。ない場合は <see langword="null"/> です。</param>
    /// <param name="options">移行の前提。ない場合は既定値です。</param>
    /// <param name="cancellationToken">取消token。</param>
    /// <returns>CLIと同じ判定・集計・修正候補を持つreport。</returns>
    public static DocusaurusMigrationReport Analyze(
        string sourceDirectory,
        IReadOnlyList<string>? expectedRoutes = null,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        AnalyzeWithOracle(sourceDirectory,
            expectedRoutes is null ? null : MigrationRouteOracle.FromPaths(expectedRoutes),
            options, cancellationToken);

    /// <summary>分類済みroute oracleを使い、書き込まずに解析します。</summary>
    /// <param name="sourceDirectory">移行元ディレクトリ。</param>
    /// <param name="routeOracle">原本routeと分類。ない場合は比較状態をnot-comparedとして記録します。</param>
    /// <param name="options">移行と比較の前提。正規化page set比較は明示的に有効化します。</param>
    /// <param name="cancellationToken">取消token。</param>
    /// <returns>判定、集計、route比較、機能差を含むreport。</returns>
    public static DocusaurusMigrationReport AnalyzeWithOracle(
        string sourceDirectory,
        MigrationRouteOracle? routeOracle,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? new();
        ValidateNormalizedInputs(routeOracle, effectiveOptions);
        return FromResult(DocusaurusMigration.AnalyzeWithOracle(sourceDirectory, routeOracle, effectiveOptions, cancellationToken),
            dryRun: true, destination: null, routeOracle, effectiveOptions);
    }

    /// <summary>明示した別出力先へ変換します。既存入力を上書きしません。</summary>
    /// <param name="sourceDirectory">移行元ディレクトリ。</param>
    /// <param name="destinationDirectory">存在しない変換先ディレクトリ。</param>
    /// <param name="expectedRoutes">従来形式のraw exact比較用path一覧です。route種別は未分類になり、正規化page set比較には使いません。ない場合は <see langword="null"/> です。</param>
    /// <param name="options">移行の前提。ない場合は既定値です。</param>
    /// <param name="cancellationToken">取消token。</param>
    /// <returns>CLIと同じ判定・集計・修正候補を持つreport。</returns>
    public static async Task<DocusaurusMigrationReport> ConvertAsync(
        string sourceDirectory,
        string destinationDirectory,
        IReadOnlyList<string>? expectedRoutes = null,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await ConvertWithOracleAsync(sourceDirectory, destinationDirectory,
            expectedRoutes is null ? null : MigrationRouteOracle.FromPaths(expectedRoutes),
            options, cancellationToken).ConfigureAwait(false);

    /// <summary>分類済みroute oracleを使い、明示した別出力先へ変換します。</summary>
    /// <param name="sourceDirectory">移行元ディレクトリ。</param>
    /// <param name="destinationDirectory">新しい変換先ディレクトリ。</param>
    /// <param name="routeOracle">原本routeと分類。ない場合は比較状態をnot-comparedとして記録します。</param>
    /// <param name="options">移行と比較の前提。正規化page set比較は明示的に有効化します。</param>
    /// <param name="cancellationToken">取消token。</param>
    /// <returns>判定、集計、route比較、機能差を含むreport。</returns>
    public static async Task<DocusaurusMigrationReport> ConvertWithOracleAsync(
        string sourceDirectory,
        string destinationDirectory,
        MigrationRouteOracle? routeOracle,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? new();
        ValidateNormalizedInputs(routeOracle, effectiveOptions);
        var fullDestination = Path.GetFullPath(destinationDirectory);
        return FromResult(await DocusaurusMigration.ConvertWithOracleAsync(
            sourceDirectory, fullDestination, routeOracle, effectiveOptions, cancellationToken).ConfigureAwait(false),
            dryRun: false, destination: fullDestination, routeOracle, effectiveOptions);
    }

    internal static DocusaurusMigrationReport FromResult(
        DocusaurusMigrationResult result,
        bool dryRun,
        string? destination,
        MigrationRouteOracle? routeOracle,
        DocusaurusMigrationOptions options)
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
            var componentChanges = (file.ComponentChanges ?? []).Select(change => new MigrationComponentChangeReport(
                file.SourcePath, change.Component, change.Kind, change.FunctionalEquivalence, change.Note)).ToArray();
            return new MigrationFileReport(file.SourcePath, verdict, file.ConvertedPath, issues, actions, file.SourceFingerprint, componentChanges);
        }).ToArray();
        var componentReport = files.SelectMany(file => file.ComponentChanges).ToArray();
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
            result.ExitCode,
            CreateRouteComparison(result, routeOracle, options),
            componentReport);
    }

    private static MigrationRouteComparisonReport CreateRouteComparison(
        DocusaurusMigrationResult result,
        MigrationRouteOracle? routeOracle,
        DocusaurusMigrationOptions options)
    {
        var targetRoutes = result.ConvertedRoutes.Select(route => route.Route)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (routeOracle is null)
        {
            var pageStatus = options.CompareNormalizedPageSet
                ? MigrationPageSetComparisonStatus.NotCompared
                : MigrationPageSetComparisonStatus.NotRequested;
            return new(
                MigrationRouteComparisonStatus.NotCompared,
                sourceRouteCount: null,
                targetRoutes.Length,
                excludedRouteCount: null,
                duplicateSourceRouteCount: null,
                result.SourceVersion,
                result.SourceHash,
                routeOracleHash: null,
                [],
                [],
                new MigrationPageSetComparisonReport(pageStatus, null, null, null, null, [], [], [],
                    options.CompareNormalizedPageSet ? "No source route oracle was supplied; empty raw differences do not indicate a pass." : null));
        }

        var uniqueOracleRoutes = routeOracle.Routes.GroupBy(route => route.Path, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        var exclusions = uniqueOracleRoutes.Where(route => route.Category != MigrationRouteCategory.Document || route.ExclusionReason is not null)
            .GroupBy(route => (route.Category, Reason: route.ExclusionReason ?? DefaultExclusionReason(route.Category)))
            .OrderBy(group => group.Key.Category)
            .ThenBy(group => group.Key.Reason, StringComparer.Ordinal)
            .Select(group => new MigrationRouteExclusionReport(group.Key.Category, group.Count(), group.Key.Reason)).ToArray();
        var locales = uniqueOracleRoutes.Where(route => !string.IsNullOrWhiteSpace(route.Locale))
            .GroupBy(route => route.Locale!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new MigrationLocaleCountReport(group.Key, group.Count())).ToArray();
        var normalized = CreatePageSetComparison(routeOracle, options, uniqueOracleRoutes, targetRoutes);
        var rawStatus = result.MissingRoutes.Count == 0 && result.ExtraRoutes.Count == 0
            ? MigrationRouteComparisonStatus.Match
            : MigrationRouteComparisonStatus.Differences;
        return new(
            rawStatus,
            uniqueOracleRoutes.Length,
            targetRoutes.Length,
            exclusions.Sum(item => item.Count),
            routeOracle.Routes.Count - uniqueOracleRoutes.Length,
            routeOracle.SourceVersion ?? result.SourceVersion,
            result.SourceHash,
            FingerprintOracle(routeOracle),
            exclusions,
            locales,
            normalized);
    }

    private static MigrationPageSetComparisonReport CreatePageSetComparison(
        MigrationRouteOracle routeOracle,
        DocusaurusMigrationOptions options,
        IReadOnlyList<MigrationRouteOracleEntry> uniqueOracleRoutes,
        IReadOnlyList<string> targetRoutes)
    {
        if (!options.CompareNormalizedPageSet)
            return new(MigrationPageSetComparisonStatus.NotRequested, null, null, null, null, [], [], [], null);

        var sourceRoutes = uniqueOracleRoutes.Where(route => route.Category == MigrationRouteCategory.Document
            && route.ExclusionReason is null).ToArray();
        if (sourceRoutes.Length == 0)
        {
            return new(MigrationPageSetComparisonStatus.NotCompared, null, null, null, null, [], [], [],
                "The oracle contains no non-excluded routes classified as Document; derived and unclassified routes are not used as a page-set denominator.");
        }

        var sourceBasePath = routeOracle.BasePath;
        var targetBasePath = SiteRoute.ForDirectoryIndex(string.Empty, options.BaseUrl).PublicPath;
        var normalizedSource = sourceRoutes.Select(route => NormalizePublicPagePath(route.Path, sourceBasePath))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var normalizedTarget = targetRoutes.Select(route => NormalizePublicPagePath(route, targetBasePath))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var missing = normalizedSource.Except(normalizedTarget, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extra = normalizedTarget.Except(normalizedSource, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var rules = new List<string>();
        if (sourceBasePath is not null && CanonicalBasePath(sourceBasePath) != "/")
            rules.Add("Strip the explicitly declared source base path when it is an exact segment prefix.");
        if (targetBasePath != "/")
            rules.Add("Strip the target --base-url path when it is an exact segment prefix.");
        rules.Add("Treat a page route with or without its trailing slash as the same directory page.");
        rules.Add("Canonicalize each path segment once through SiteRoute (strict UTF-8 percent decoding, NFC, then canonical percent encoding).");
        rules.Add("Compare with ordinal, case-sensitive semantics; do not case-fold or recursively decode percent escapes.");
        return new(
            missing.Length == 0 && extra.Length == 0 ? MigrationPageSetComparisonStatus.Match : MigrationPageSetComparisonStatus.Differences,
            sourceRoutes.Length,
            normalizedSource.Length,
            targetRoutes.Count,
            normalizedTarget.Length,
            missing,
            extra,
            rules,
            null);
    }

    private static void ValidateNormalizedInputs(MigrationRouteOracle? routeOracle, DocusaurusMigrationOptions options)
    {
        if (!options.CompareNormalizedPageSet || routeOracle is null) return;
        _ = SiteRoute.ForDirectoryIndex(string.Empty, options.BaseUrl);
        if (routeOracle.BasePath is not null) _ = CanonicalBasePath(routeOracle.BasePath);
        foreach (var route in routeOracle.Routes.Where(route => route.Category == MigrationRouteCategory.Document
            && route.ExclusionReason is null))
            _ = NormalizePublicPagePath(route.Path, routeOracle.BasePath);
    }

    private static string NormalizePublicPagePath(string path, string? basePath)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\') || path.IndexOfAny(['?', '#']) >= 0 || path.Contains("//", StringComparison.Ordinal))
            throw new ArgumentException($"Route '{path}' must be an unambiguous absolute public path for normalized comparison.", nameof(path));

        var canonical = SiteRoute.ForDirectoryIndex(path[1..].TrimEnd('/')).PublicPath;
        var prefix = basePath is null ? "/" : CanonicalBasePath(basePath);
        if (prefix != "/" && canonical.StartsWith(prefix, StringComparison.Ordinal))
        {
            var remainder = canonical[prefix.Length..];
            canonical = remainder.Length == 0 ? "/" : "/" + remainder;
        }
        return canonical;
    }

    private static string CanonicalBasePath(string basePath)
    {
        if (!basePath.StartsWith("/", StringComparison.Ordinal) || basePath.StartsWith("//", StringComparison.Ordinal)
            || basePath.Contains('\\') || basePath.IndexOfAny(['?', '#']) >= 0 || basePath.Contains("//", StringComparison.Ordinal))
            throw new ArgumentException($"Base path '{basePath}' must be an absolute public path.", nameof(basePath));
        return SiteRoute.ForDirectoryIndex(basePath[1..].Trim('/')).PublicPath;
    }

    private static string DefaultExclusionReason(MigrationRouteCategory category) => category switch
    {
        MigrationRouteCategory.CategoryIndex => "Generated category indexes do not have a one-to-one source document.",
        MigrationRouteCategory.BlogIndex => "Generated blog indexes are outside the migrated article page set.",
        MigrationRouteCategory.BlogAuthor => "Generated author indexes are outside the migrated article page set.",
        MigrationRouteCategory.BlogTag => "Generated tag indexes are outside the migrated article page set.",
        MigrationRouteCategory.BlogArchive => "Generated archive indexes are outside the migrated article page set.",
        MigrationRouteCategory.BlogPagination => "Generated pagination routes are outside the migrated article page set.",
        MigrationRouteCategory.Unclassified => "A path-only oracle does not declare route kinds, so the route is not assumed to be a document page.",
        _ => "The route is outside the declared document page-set scope.",
    };

    private static string FingerprintOracle(MigrationRouteOracle routeOracle)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sourceVersion = routeOracle.SourceVersion,
            basePath = routeOracle.BasePath,
            routes = routeOracle.Routes
                .OrderBy(route => route.Path, StringComparer.Ordinal)
                .ThenBy(route => route.Category)
                .ThenBy(route => route.Locale, StringComparer.Ordinal)
                .ThenBy(route => route.ExclusionReason, StringComparer.Ordinal)
                .Select(route => new
                {
                    path = route.Path,
                    category = route.Category.ToString(),
                    route.Locale,
                    reason = route.ExclusionReason,
                }).ToArray(),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
}
