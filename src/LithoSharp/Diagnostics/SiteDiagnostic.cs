namespace LithoSharp.Diagnostics;

/// <summary>サイト生成診断の重大度を表します。</summary>
public enum SiteDiagnosticSeverity
{
    /// <summary>処理を妨げない補足情報です。</summary>
    Info,

    /// <summary>処理は継続できますが、確認が必要な問題です。</summary>
    Warning,

    /// <summary>安全な処理の継続を妨げる問題です。</summary>
    Error,
}

/// <summary>診断に関連するソースファイル上の位置を表します。</summary>
public sealed class SiteSourceLocation
{
    /// <summary>ソース位置を作成します。</summary>
    /// <param name="filePath">関連するソースファイルのパス。</param>
    /// <param name="line">1 から始まる行番号。特定できない場合は <see langword="null"/>。</param>
    /// <param name="column">1 から始まる列番号。特定できない場合は <see langword="null"/>。</param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> が空白です。</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="line"/> または <paramref name="column"/> が 1 未満です。
    /// </exception>
    public SiteSourceLocation(string filePath, int? line = null, int? column = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A source file path must not be empty.", nameof(filePath));
        }

        if (line is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(line), "A source line must be one-based.");
        }

        if (column is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(column), "A source column must be one-based.");
        }

        if (column is not null && line is null)
        {
            throw new ArgumentException("A source column requires a source line.", nameof(column));
        }

        FilePath = filePath;
        Line = line;
        Column = column;
    }

    /// <summary>関連するソースファイルのパスを取得します。</summary>
    public string FilePath { get; }

    /// <summary>1 から始まる行番号を取得します。</summary>
    public int? Line { get; }

    /// <summary>1 から始まる列番号を取得します。</summary>
    public int? Column { get; }
}

/// <summary>安定した識別子、重大度、メッセージ、任意のソース位置を持つ診断を表します。</summary>
public sealed class SiteDiagnostic
{
    /// <summary>診断を作成します。</summary>
    /// <param name="id">バージョン間で安定した診断識別子。</param>
    /// <param name="severity">診断の重大度。</param>
    /// <param name="message">診断メッセージ。</param>
    /// <param name="location">関連するソース位置。特定できない場合は <see langword="null"/>。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/> または <paramref name="message"/> が <see langword="null"/> です。
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> または <paramref name="message"/> が空白です。
    /// </exception>
    public SiteDiagnostic(
        string id,
        SiteDiagnosticSeverity severity,
        string message,
        SiteSourceLocation? location = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A diagnostic identifier must not be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A diagnostic message must not be empty.", nameof(message));
        }

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        Id = id;
        Severity = severity;
        Message = message;
        Location = location;
    }

    /// <summary>バージョン間で安定した診断識別子を取得します。</summary>
    public string Id { get; }

    /// <summary>診断の重大度を取得します。</summary>
    public SiteDiagnosticSeverity Severity { get; }

    /// <summary>診断メッセージを取得します。</summary>
    public string Message { get; }

    /// <summary>関連するソース位置を取得します。</summary>
    public SiteSourceLocation? Location { get; }
}
