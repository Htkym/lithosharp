using System.Collections.ObjectModel;
using LithoSharp.Diagnostics;

namespace LithoSharp.Content;

public interface IContentCollectionLoader<TFrontMatter, TBody>
    where TFrontMatter : notnull
    where TBody : notnull
{
    /// <summary>Returns diagnostics for ordinary input errors; configuration or implementation errors may throw.</summary>
    ValueTask<ContentLoadResult<TFrontMatter, TBody>> LoadAsync(
        CancellationToken cancellationToken = default);
}

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

    public bool IsSuccess =>
        Collection is not null
        && !Diagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error);

    public ContentCollection<TFrontMatter, TBody>? Collection { get; }

    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }

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
