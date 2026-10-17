using LithoSharp.Diagnostics;

namespace LithoSharp.Content;

public interface IContentFrontMatterBinder<TFrontMatter>
    where TFrontMatter : notnull
{
    /// <summary>Returns diagnostics for ordinary input errors; configuration or implementation errors may throw.</summary>
    ContentParseResult<TFrontMatter> Bind(
        IReadOnlyDictionary<string, object?> values,
        SiteSourceLocation? sourceLocation = null);
}

public sealed class ContentParseResult<T>
    where T : notnull
{
    private ContentParseResult(T? value, IEnumerable<SiteDiagnostic> diagnostics)
    {
        Value = value;
        Diagnostics = ContentDiagnostics.Snapshot(diagnostics);
    }

    public bool IsSuccess =>
        !Diagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error);

    public T? Value { get; }

    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }

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
