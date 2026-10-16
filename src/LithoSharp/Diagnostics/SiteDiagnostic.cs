using System.Collections.ObjectModel;

namespace LithoSharp.Diagnostics;

/// <summary>One-based source positions; null means unknown. An end position requires a start and cannot precede it.</summary>
public sealed class SiteSourceLocation
{
    // RS0027: preserve optional arguments on the legacy overload; the longer overload has no defaults.
#pragma warning disable RS0027
    public SiteSourceLocation(string filePath, int? line = null, int? column = null)
        : this(filePath, line, column, null, null)
    {
    }
#pragma warning restore RS0027

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

    public string FilePath { get; }

    public int? Line { get; }

    public int? Column { get; }

    public int? EndLine { get; }

    public int? EndColumn { get; }
}

public sealed class SiteDiagnostic
{
    // RS0027: preserve optional arguments on the legacy overload; the longer overload has no defaults.
#pragma warning disable RS0027
    public SiteDiagnostic(
        string id,
        SiteDiagnosticSeverity severity,
        string message,
        SiteSourceLocation? location = null)
        : this(id, severity, message, location, null, null)
    {
    }
#pragma warning restore RS0027

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

    public string Id { get; }

    public SiteDiagnosticSeverity Severity { get; }

    public string Message { get; }

    public SiteSourceLocation? Location { get; }

    public string? Category { get; }

    public IReadOnlyList<SiteSourceLocation> RelatedLocations { get; }
}
