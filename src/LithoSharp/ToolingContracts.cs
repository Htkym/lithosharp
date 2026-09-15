namespace LithoSharp;

/// <summary>Tooling契約の成熟度を表します。</summary>
public enum ToolingContractMaturity
{
    /// <summary>1.xで凍結します。加算的追加のみ行い、破壊的変更は次期majorへ送ります。</summary>
    Stable = 0,
    /// <summary>1.xのminorで変更される場合があります。利用側は存在を仮定しません。</summary>
    Experimental = 1,
}

/// <summary>1つのTooling契約を表します。</summary>
public sealed class ToolingContract
{
    internal ToolingContract(string name, ToolingContractMaturity maturity, string schemaVersion, string description)
    {
        Name = name;
        Maturity = maturity;
        SchemaVersion = schemaVersion;
        Description = description;
    }

    /// <summary>契約名を取得します。</summary>
    public string Name { get; }

    /// <summary>成熟度を取得します。</summary>
    public ToolingContractMaturity Maturity { get; }

    /// <summary>契約のschema versionを取得します。</summary>
    public string SchemaVersion { get; }

    /// <summary>契約の説明を取得します。</summary>
    public string Description { get; }
}

/// <summary>1.xのTooling互換方針を表します。</summary>
/// <remarks>
/// 診断コード、JSON、inspection、dev server、migration reportを分類します。
/// 規則は次のとおりです。未知fieldは無視し、追加は加算的に行います。
/// schema versionはmajor一致で互換とし、minorの追加は許容します。
/// Stableの廃止は代替付きのObsolete告知を経て、削除は次期majorで行います。
/// 非互換versionは <see cref="ToolingCompatibility.CheckSchemaVersion"/> で明示したエラーにします。
/// </remarks>
public static class ToolingContracts
{
    /// <summary>All 1.x tooling contracts share this schema version today.</summary>
    public const string CurrentSchemaVersion = "1.0";

    /// <summary>診断コードと位置の契約を取得します。</summary>
    public static ToolingContract DiagnosticCodes { get; } = new(
        "DiagnosticCodes", ToolingContractMaturity.Stable, CurrentSchemaVersion,
        "Diagnostic codes and source positions from T01. Codes keep their meaning in 1.x.");

    /// <summary>構造化出力の契約を取得します。</summary>
    public static ToolingContract JsonEnvelopes { get; } = new(
        "JsonEnvelopes", ToolingContractMaturity.Stable, CurrentSchemaVersion,
        "Machine-readable CLI envelopes and exit codes from T04. Fields grow additively.");

    /// <summary>文書inspectionの契約を取得します。</summary>
    public static ToolingContract Inspection { get; } = new(
        "Inspection", ToolingContractMaturity.Stable, CurrentSchemaVersion,
        "Document snapshots, workspace lifetime, front matter schemas and navigation from T02, T03 and T07, T08.");

    /// <summary>dev server制御の契約を取得します。</summary>
    public static ToolingContract DevServer { get; } = new(
        "DevServer", ToolingContractMaturity.Stable, CurrentSchemaVersion,
        "Machine control events with startup, rebuild and shutdown from T05. Event types grow additively.");

    /// <summary>移行reportの契約を取得します。</summary>
    public static ToolingContract MigrationReport { get; } = new(
        "MigrationReport", ToolingContractMaturity.Stable, CurrentSchemaVersion,
        "Migration verdicts, aggregates and fix candidates from T09. Fingerprint rules stay stable.");

    /// <summary>全Tooling契約を取得します。</summary>
    public static IReadOnlyList<ToolingContract> All { get; } =
        [DiagnosticCodes, JsonEnvelopes, Inspection, DevServer, MigrationReport];
}

/// <summary>schema versionの交渉を表します。</summary>
public static class ToolingCompatibility
{
    /// <summary>schema versionが互換かどうかを判定します。</summary>
    /// <param name="actual">相手のschema version。</param>
    /// <param name="supported">自前のschema version。既定は <see cref="ToolingContracts.CurrentSchemaVersion"/> です。</param>
    /// <returns>major一致の場合に <see langword="true"/> を返します。</returns>
    public static bool IsCompatible(string actual, string supported = ToolingContracts.CurrentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(supported);
        return TryMajor(actual, out var actualMajor) && TryMajor(supported, out var supportedMajor)
            && actualMajor == supportedMajor;
    }

    /// <summary>schema versionを検証します。</summary>
    /// <param name="actual">相手のschema version。</param>
    /// <param name="supported">自前のschema version。既定は <see cref="ToolingContracts.CurrentSchemaVersion"/> です。</param>
    /// <returns>互換の場合に相手のversionをそのまま返します。</returns>
    /// <exception cref="InvalidOperationException">majorが異なるか形式が不正です。</exception>
    public static string CheckSchemaVersion(string actual, string supported = ToolingContracts.CurrentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(supported);
        if (!IsCompatible(actual, supported))
        {
            throw new InvalidOperationException(
                $"Unsupported tooling schema version '{actual}'; this client supports schema version '{supported}'.");
        }
        return actual;
    }

    private static bool TryMajor(string version, out int major)
    {
        major = 0;
        if (version.Length == 0 || version.Any(character => !char.IsAsciiDigit(character) && character != '.'))
            return false;
        var dot = version.IndexOf('.');
        if (dot < 0) return int.TryParse(version, out major);
        if (!Version.TryParse(version, out var parsed)) return false;
        major = parsed.Major;
        return true;
    }
}
