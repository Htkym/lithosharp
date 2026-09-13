using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using LithoSharp.Diagnostics;

namespace LithoSharp.Quality;

/// <summary>品質検査の出力形式です。</summary>
public enum SiteDiagnosticFormat
{
    /// <summary>人間が読むテキスト形式です。</summary>
    Text,
    /// <summary>診断情報の JSON 形式です。</summary>
    Json,
    /// <summary>SARIF 2.1.0 形式です。</summary>
    Sarif,
}

/// <summary>品質検査で収集した診断を表します。</summary>
public sealed class SiteQualityReport
{
    /// <summary>品質検査レポートを作成します。</summary>
    /// <param name="diagnostics">収集した診断。</param>
    /// <exception cref="ArgumentException">診断一覧に null が含まれています。</exception>
    public SiteQualityReport(IEnumerable<SiteDiagnostic>? diagnostics = null)
    {
        var values = (diagnostics ?? []).ToArray();
        if (values.Any(static value => value is null)) throw new ArgumentException("Diagnostics must not contain null entries.", nameof(diagnostics));
        Diagnostics = new ReadOnlyCollection<SiteDiagnostic>(values
            .OrderBy(static value => value.Id, StringComparer.Ordinal)
            .ThenBy(static value => value.Location?.FilePath, StringComparer.Ordinal)
            .ThenBy(static value => value.Location?.Line)
            .ThenBy(static value => value.Location?.Column)
            .ThenBy(static value => value.Severity)
            .ThenBy(static value => value.Message, StringComparer.Ordinal)
            .ThenBy(static value => value.Location?.EndLine)
            .ThenBy(static value => value.Location?.EndColumn)
            .ThenBy(static value => value.Category, StringComparer.Ordinal)
            .ThenBy(static value => RelatedKey(value), StringComparer.Ordinal)
            .DistinctBy(static value => (value.Id, value.Severity, value.Message, value.Location?.FilePath, value.Location?.Line, value.Location?.Column, value.Location?.EndLine, value.Location?.EndColumn, value.Category, RelatedKey(value)))
            .ToArray());
    }

    /// <summary>決定的な順序で並べた診断を取得します。</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }

    /// <summary>指定形式で診断を出力します。</summary>
    /// <param name="format">テキスト、JSON、またはSARIF形式。</param>
    /// <returns>整列済み診断の文字列表現。</returns>
    /// <exception cref="ArgumentOutOfRangeException">形式が未定義です。</exception>
    public string Format(SiteDiagnosticFormat format) => format switch
    {
        SiteDiagnosticFormat.Text => FormatText(),
        SiteDiagnosticFormat.Json => FormatJson(),
        SiteDiagnosticFormat.Sarif => FormatSarif(),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private string FormatText() => string.Join("\n", Diagnostics.Select(static diagnostic =>
        $"{diagnostic.Severity.ToString().ToUpperInvariant()} {diagnostic.Id}"
        + (string.IsNullOrEmpty(diagnostic.Category) ? "" : $" [{diagnostic.Category}]")
        + $": {diagnostic.Message}"
        + (diagnostic.Location is null ? "" : FormatTextLocation(diagnostic.Location))));

    private static string FormatTextLocation(SiteSourceLocation location) =>
        $" ({location.FilePath}:{location.Line?.ToString() ?? "?"}:{location.Column?.ToString() ?? "?"}" +
        (location.EndLine is null ? "" : $"-{location.EndLine}:{location.EndColumn?.ToString() ?? "?"}") + ")";

    private string FormatJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject(); writer.WriteStartArray("diagnostics");
            foreach (var diagnostic in Diagnostics) WriteDiagnostic(writer, diagnostic);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private string FormatSarif()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject(); writer.WriteString("version", "2.1.0"); writer.WriteString("$schema", "https://json.schemastore.org/sarif-2.1.0.json");
            writer.WriteStartArray("runs"); writer.WriteStartObject(); writer.WriteStartObject("tool"); writer.WriteStartObject("driver"); writer.WriteString("name", "LithoSharp"); writer.WriteStartArray("rules");
            foreach (var id in Diagnostics.Select(static d => d.Id).Distinct(StringComparer.Ordinal)) { writer.WriteStartObject(); writer.WriteString("id", id); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteStartArray("results");
            foreach (var diagnostic in Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("ruleId", diagnostic.Id);
                writer.WriteString("level", diagnostic.Severity switch { SiteDiagnosticSeverity.Error => "error", SiteDiagnosticSeverity.Warning => "warning", _ => "note" });
                writer.WriteStartObject("message");
                writer.WriteString("text", diagnostic.Message);
                writer.WriteEndObject();
                if (diagnostic.Location is { } location)
                {
                    writer.WriteStartArray("locations");
                    writer.WriteStartObject();
                    writer.WriteStartObject("physicalLocation");
                    writer.WriteStartObject("artifactLocation");
                    writer.WriteString("uri", ArtifactUri(location.FilePath));
                    writer.WriteEndObject();
                    if (location.Line is { } line)
                    {
                        writer.WriteStartObject("region");
                        writer.WriteNumber("startLine", line);
                        if (location.Column is { } column) writer.WriteNumber("startColumn", column);
                        if (location.EndLine is { } endLine) writer.WriteNumber("endLine", endLine);
                        if (location.EndColumn is { } endColumn) writer.WriteNumber("endColumn", endColumn);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }
                if (diagnostic.Category is { } category) { writer.WriteStartObject("properties"); writer.WriteString("category", category); writer.WriteEndObject(); }
                if (diagnostic.RelatedLocations.Count > 0)
                {
                    writer.WriteStartArray("relatedLocations");
                    foreach (var related in diagnostic.RelatedLocations)
                    {
                        writer.WriteStartObject(); writer.WriteStartObject("physicalLocation"); writer.WriteStartObject("artifactLocation");
                        writer.WriteString("uri", ArtifactUri(related.FilePath));
                        writer.WriteEndObject();
                        if (related.Line is { } relatedLine)
                        {
                            writer.WriteStartObject("region");
                            writer.WriteNumber("startLine", relatedLine);
                            if (related.Column is { } relatedColumn) writer.WriteNumber("startColumn", relatedColumn);
                            if (related.EndLine is { } relatedEndLine) writer.WriteNumber("endLine", relatedEndLine);
                            if (related.EndColumn is { } relatedEndColumn) writer.WriteNumber("endColumn", relatedEndColumn);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndObject(); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ArtifactUri(string path)
    {
        path = path.Replace('\\', '/');
        if (Path.IsPathRooted(path)) return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        return string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    }

    private static string RelatedKey(SiteDiagnostic diagnostic) =>
        string.Join(";", diagnostic.RelatedLocations.Select(static related => string.Concat(related.FilePath, ":", related.Line?.ToString(), ":", related.Column?.ToString(), "-", related.EndLine?.ToString(), ":", related.EndColumn?.ToString())));

    private static void WriteLocationBody(Utf8JsonWriter writer, SiteSourceLocation location)
    {
        writer.WriteString("filePath", location.FilePath);
        if (location.Line is { } line) writer.WriteNumber("line", line);
        if (location.Column is { } column) writer.WriteNumber("column", column);
        if (location.EndLine is { } endLine) writer.WriteNumber("endLine", endLine);
        if (location.EndColumn is { } endColumn) writer.WriteNumber("endColumn", endColumn);
    }

    private static void WriteDiagnostic(Utf8JsonWriter writer, SiteDiagnostic diagnostic)
    {
        writer.WriteStartObject(); writer.WriteString("id", diagnostic.Id); writer.WriteString("severity", diagnostic.Severity.ToString()); writer.WriteString("message", diagnostic.Message);
        if (diagnostic.Category is { } category) writer.WriteString("category", category);
        if (diagnostic.Location is { } location) { writer.WriteStartObject("location"); WriteLocationBody(writer, location); writer.WriteEndObject(); }
        if (diagnostic.RelatedLocations.Count > 0) { writer.WriteStartArray("relatedLocations"); foreach (var related in diagnostic.RelatedLocations) { writer.WriteStartObject(); WriteLocationBody(writer, related); writer.WriteEndObject(); } writer.WriteEndArray(); }
        writer.WriteEndObject();
    }
}

/// <summary>品質検査が失敗したことを表します。</summary>
public sealed class SiteQualityValidationException : InvalidOperationException
{
    /// <summary>品質検査失敗例外を作成します。</summary>
    /// <param name="report">失敗した検査のレポート。</param>
    public SiteQualityValidationException(SiteQualityReport report) : base("Site quality validation failed.")
    {
        ArgumentNullException.ThrowIfNull(report); Report = report;
    }
    /// <summary>失敗した検査のレポートを取得します。</summary>
    public SiteQualityReport Report { get; }
    /// <summary>レポートの診断を取得します。</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics => Report.Diagnostics;
}
