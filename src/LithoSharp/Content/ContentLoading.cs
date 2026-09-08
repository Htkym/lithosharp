using System.Collections.ObjectModel;
using LithoSharp.Diagnostics;

namespace LithoSharp.Content;

/// <summary>型付きコンテンツコレクションを非同期に読み込む契約を表します。</summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <typeparam name="TBody">本文の型。</typeparam>
public interface IContentCollectionLoader<TFrontMatter, TBody>
    where TFrontMatter : notnull
    where TBody : notnull
{
    /// <summary>コンテンツコレクションを読み込みます。</summary>
    /// <param name="cancellationToken">読み込みを取り消すトークン。</param>
    /// <returns>コレクションまたは入力診断を含む読み込み結果。</returns>
    /// <remarks>
    /// 通常の入力検証エラーは例外ではなく診断として返します。
    /// 構成や実装の誤りには、対応する <see cref="ArgumentException"/> または
    /// <see cref="InvalidOperationException"/> を使用します。
    /// </remarks>
    ValueTask<ContentLoadResult<TFrontMatter, TBody>> LoadAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>コンテンツコレクションの読み込み結果を表します。</summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <typeparam name="TBody">本文の型。</typeparam>
public sealed class ContentLoadResult<TFrontMatter, TBody>
    where TFrontMatter : notnull
    where TBody : notnull
{
    private ContentLoadResult(
        ContentCollection<TFrontMatter, TBody>? collection,
        IEnumerable<SiteDiagnostic> diagnostics)
    {
        Collection = collection;
        Diagnostics = ContentDiagnostics.Snapshot(diagnostics);
    }

    /// <summary>読み込みが成功し、エラー診断を含まないかどうかを取得します。</summary>
    public bool IsSuccess =>
        Collection is not null
        && !Diagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error);

    /// <summary>読み込んだコレクションを取得します。失敗時は <see langword="null"/> です。</summary>
    public ContentCollection<TFrontMatter, TBody>? Collection { get; }

    /// <summary>読み込み中に生成され、ソース位置と識別子で決定的に並べられた診断を取得します。</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }

    /// <summary>成功した読み込み結果を作成します。</summary>
    /// <param name="collection">読み込んだコレクション。</param>
    /// <param name="diagnostics">情報または警告の診断。</param>
    /// <returns>成功した読み込み結果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="collection"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException"><paramref name="diagnostics"/> にエラー診断が含まれます。</exception>
    public static ContentLoadResult<TFrontMatter, TBody> Success(
        ContentCollection<TFrontMatter, TBody> collection,
        IEnumerable<SiteDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var snapshot = ContentDiagnostics.Snapshot(diagnostics);
        if (snapshot.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error))
        {
            throw new ArgumentException(
                "A successful content load result must not contain error diagnostics.",
                nameof(diagnostics));
        }

        return new ContentLoadResult<TFrontMatter, TBody>(collection, snapshot);
    }

    /// <summary>入力エラーで失敗した読み込み結果を作成します。</summary>
    /// <param name="diagnostics">少なくとも 1 件のエラーを含む診断。</param>
    /// <returns>失敗した読み込み結果。</returns>
    /// <exception cref="ArgumentException">エラー診断が含まれていません。</exception>
    public static ContentLoadResult<TFrontMatter, TBody> Failure(
        IEnumerable<SiteDiagnostic> diagnostics)
    {
        var snapshot = ContentDiagnostics.Snapshot(diagnostics);
        if (!snapshot.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error))
        {
            throw new ArgumentException(
                "A failed content load result must contain an error diagnostic.",
                nameof(diagnostics));
        }

        return new ContentLoadResult<TFrontMatter, TBody>(collection: null, snapshot);
    }
}

internal static class ContentDiagnostics
{
    public static ReadOnlyCollection<SiteDiagnostic> Snapshot(
        IEnumerable<SiteDiagnostic>? diagnostics)
    {
        var values = diagnostics?.ToArray() ?? [];
        if (values.Any(static diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Diagnostics must not contain null.", nameof(diagnostics));
        }

        return Array.AsReadOnly(values
            .OrderBy(static diagnostic => diagnostic.Location?.FilePath, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Location?.Line)
            .ThenBy(static diagnostic => diagnostic.Location?.Column)
            .ThenBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Severity)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray());
    }
}
