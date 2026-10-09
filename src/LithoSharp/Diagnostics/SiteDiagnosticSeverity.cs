namespace LithoSharp.Diagnostics;

/// <summary>サイト生成診断の重大度を表します。</summary>
#if NETSTANDARD2_0
internal enum SiteDiagnosticSeverity
#else
public enum SiteDiagnosticSeverity
#endif
{
    /// <summary>処理を妨げない補足情報です。</summary>
    Info,
    /// <summary>処理は継続できますが、確認が必要な問題です。</summary>
    Warning,
    /// <summary>安全な処理の継続を妨げる問題です。</summary>
    Error,
}
