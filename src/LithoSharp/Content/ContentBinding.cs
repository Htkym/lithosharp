using LithoSharp.Diagnostics;

namespace LithoSharp.Content;

/// <summary>形式に依存しないキーと値から型付きフロントマターへ変換する契約を表します。</summary>
/// <typeparam name="TFrontMatter">変換後のフロントマターの型。</typeparam>
public interface IContentFrontMatterBinder<TFrontMatter>
    where TFrontMatter : notnull
{
    /// <summary>解析済みのキーと値を型付きフロントマターへ変換します。</summary>
    /// <param name="values">入力形式から解析された、読み取り専用のキーと値。</param>
    /// <param name="sourceLocation">入力の開始位置。特定できない場合は <see langword="null"/>。</param>
    /// <returns>型付き値または入力診断を含む変換結果。</returns>
    /// <remarks>
    /// 通常の入力検証エラーは例外ではなく診断として返します。
    /// バインダーの構成や実装の誤りには、対応する例外を使用します。
    /// </remarks>
    ContentParseResult<TFrontMatter> Bind(
        IReadOnlyDictionary<string, object?> values,
        SiteSourceLocation? sourceLocation = null);
}

/// <summary>解析またはバインドされた型付き値と診断を表します。</summary>
/// <typeparam name="T">解析後の値の型。</typeparam>
public sealed class ContentParseResult<T>
    where T : notnull
{
    private ContentParseResult(T? value, IEnumerable<SiteDiagnostic> diagnostics)
    {
        Value = value;
        Diagnostics = ContentDiagnostics.Snapshot(diagnostics);
    }

    /// <summary>型付き値があり、エラー診断を含まないかどうかを取得します。</summary>
    public bool IsSuccess =>
        !Diagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error);

    /// <summary>解析またはバインドされた値を取得します。失敗時は型の既定値です。</summary>
    public T? Value { get; }

    /// <summary>解析またはバインド中に生成され、ソース位置と識別子で決定的に並べられた診断を取得します。</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }

    /// <summary>成功した解析結果を作成します。</summary>
    /// <param name="value">解析またはバインドされた値。</param>
    /// <param name="diagnostics">情報または警告の診断。</param>
    /// <returns>成功した解析結果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="diagnostics"/> にエラー診断が含まれます。</exception>
    public static ContentParseResult<T> Success(
        T value,
        IEnumerable<SiteDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var snapshot = ContentDiagnostics.Snapshot(diagnostics);
        if (snapshot.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error))
        {
            throw new ArgumentException(
                "A successful content parse result must not contain error diagnostics.",
                nameof(diagnostics));
        }

        return new ContentParseResult<T>(value, snapshot);
    }

    /// <summary>入力エラーで失敗した解析結果を作成します。</summary>
    /// <param name="diagnostics">少なくとも 1 件のエラーを含む診断。</param>
    /// <returns>失敗した解析結果。</returns>
    /// <exception cref="ArgumentException">エラー診断が含まれていません。</exception>
    public static ContentParseResult<T> Failure(IEnumerable<SiteDiagnostic> diagnostics)
    {
        var snapshot = ContentDiagnostics.Snapshot(diagnostics);
        if (!snapshot.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error))
        {
            throw new ArgumentException(
                "A failed content parse result must contain an error diagnostic.",
                nameof(diagnostics));
        }

        return new ContentParseResult<T>(value: default, snapshot);
    }
}
