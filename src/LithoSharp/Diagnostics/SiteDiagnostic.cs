using System.Collections.ObjectModel;

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
        : this(filePath, line, column, null, null)
    {
    }

    /// <summary>終了位置付きのソース位置を作成します。</summary>
    /// <param name="filePath">関連するソースファイルのパス。</param>
    /// <param name="line">1 から始まる開始行番号。特定できない場合は <see langword="null"/>。</param>
    /// <param name="column">1 から始まる開始列番号。特定できない場合は <see langword="null"/>。</param>
    /// <param name="endLine">1 から始まる終了行番号。特定できない場合は <see langword="null"/>。</param>
    /// <param name="endColumn">1 から始まる終了列番号。特定できない場合は <see langword="null"/>。</param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> が空白です。</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 行または列が 1 未満か、終了位置が開始位置より前です。
    /// </exception>
    /// <exception cref="ArgumentException">開始位置なしに終了位置が指定されています。</exception>
    public SiteSourceLocation(string filePath, int? line, int? column, int? endLine, int? endColumn)
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

        if (endLine is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(endLine), "A source end line must be one-based.");
        }

        if (endColumn is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(endColumn), "A source end column must be one-based.");
        }

        if (endColumn is not null && endLine is null)
        {
            throw new ArgumentException("A source end column requires a source end line.", nameof(endColumn));
        }

        if (endLine is not null && line is null)
        {
            throw new ArgumentException("A source end position requires a source start position.", nameof(endLine));
        }

        if (endLine < line || (endLine == line && endColumn < column))
        {
            throw new ArgumentOutOfRangeException(nameof(endLine), "A source end position must not precede its start position.");
        }

        FilePath = filePath;
        Line = line;
        Column = column;
        EndLine = endLine;
        EndColumn = endColumn;
    }

    /// <summary>関連するソースファイルのパスを取得します。</summary>
    public string FilePath { get; }

    /// <summary>1 から始まる行番号を取得します。</summary>
    public int? Line { get; }

    /// <summary>1 から始まる列番号を取得します。</summary>
    public int? Column { get; }

    /// <summary>1 から始まる終了行番号を取得します。</summary>
    public int? EndLine { get; }

    /// <summary>1 から始まる終了列番号を取得します。</summary>
    public int? EndColumn { get; }
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
        : this(id, severity, message, location, null, null)
    {
    }

    /// <summary>分類と関連位置付きの診断を作成します。</summary>
    /// <param name="id">バージョン間で安定した診断識別子。</param>
    /// <param name="severity">診断の重大度。</param>
    /// <param name="message">診断メッセージ。</param>
    /// <param name="location">関連するソース位置。文書外の診断では <see langword="null"/>。</param>
    /// <param name="category">診断の分類。ない場合は <see langword="null"/>。</param>
    /// <param name="relatedLocations">関連する追加のソース位置。ない場合は <see langword="null"/>。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/> または <paramref name="message"/> が <see langword="null"/> です。
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/>、<paramref name="message"/>、<paramref name="category"/> が空白か、
    /// <paramref name="relatedLocations"/> に null が含まれています。
    /// </exception>
    public SiteDiagnostic(
        string id,
        SiteDiagnosticSeverity severity,
        string message,
        SiteSourceLocation? location,
        string? category,
        IEnumerable<SiteSourceLocation>? relatedLocations)
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

        if (category is not null && string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("A diagnostic category must not be empty.", nameof(category));
        }

        var related = (relatedLocations ?? []).ToArray();
        if (related.Any(static value => value is null))
        {
            throw new ArgumentException("Related locations must not contain null entries.", nameof(relatedLocations));
        }

        Id = id;
        Severity = severity;
        Message = message;
        Location = location;
        Category = category;
        RelatedLocations = new ReadOnlyCollection<SiteSourceLocation>(related);
    }

    /// <summary>バージョン間で安定した診断識別子を取得します。</summary>
    public string Id { get; }

    /// <summary>診断の重大度を取得します。</summary>
    public SiteDiagnosticSeverity Severity { get; }

    /// <summary>診断メッセージを取得します。</summary>
    public string Message { get; }

    /// <summary>関連するソース位置を取得します。</summary>
    public SiteSourceLocation? Location { get; }

    /// <summary>診断の分類を取得します。</summary>
    public string? Category { get; }

    /// <summary>関連する追加のソース位置を取得します。</summary>
    public IReadOnlyList<SiteSourceLocation> RelatedLocations { get; }
}