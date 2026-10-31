using System.Text.Json;
using LithoSharp.Diagnostics;

namespace LithoSharp.Tool;

// Reads compiler SARIF only; never reads source buffers to invent missing positions.
internal static class CompilerDiagnosticReader
{
    internal static IReadOnlyList<SiteDiagnostic> Read(string? path, int exitCode, string compilerWorkingDirectory)
        => ReadMany(path is null ? [] : [(path, compilerWorkingDirectory)], exitCode);

    internal static IReadOnlyList<SiteDiagnostic> ReadMany(IEnumerable<(string Path, string WorkingDirectory)> reports, int exitCode)
    {
        var diagnostics = reports.SelectMany(report => ReadFile(report.Path, report.WorkingDirectory)).ToList();
        if (!diagnostics.Any(d => d.Severity == SiteDiagnosticSeverity.Error))
            diagnostics.Add(new("LSC7002", SiteDiagnosticSeverity.Error, $"Project compilation failed with exit code {exitCode}; no complete compiler Error snapshot is available."));
        return diagnostics;
    }

    private static IReadOnlyList<SiteDiagnostic> ReadFile(string path, string compilerWorkingDirectory)
    {
        var diagnostics = new List<SiteDiagnostic>();
        if (path is not null && File.Exists(path))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(path));
                foreach (var run in Items(json.RootElement, "runs"))
                {
                    var resolver = new Locations(run, DirectoryUri(compilerWorkingDirectory));
                    foreach (var result in Items(run, "results"))
                    {
                        if (Items(result, "suppressions").Any(Suppressed))
                            continue;
                        var id = Text(result, "ruleId");
                        var message = Text(Property(result, "message"), "text");
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(message))
                            continue;
                        var severity = Text(result, "level") switch
                        { "error" => SiteDiagnosticSeverity.Error, "warning" => SiteDiagnosticSeverity.Warning, _ => SiteDiagnosticSeverity.Info };
                        var primary = Items(result, "locations").FirstOrDefault();
                        var related = Items(result, "relatedLocations").Select(location => resolver.Read(location, result)).OfType<SiteSourceLocation>().ToArray();
                        diagnostics.Add(new(id, severity, message, resolver.Read(primary, result), Category(run, result, id), related));
                    }
                }
            }
            catch (JsonException) { } // The compilation still failed; retain the existing fallback.
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return diagnostics;
    }

    private static bool Suppressed(JsonElement suppression)
    {
        var status = Text(suppression, "status");
        // Roslyn writes kind=inSource without status for pragma/SuppressMessage.
        return status == "accepted" || status is null && Text(suppression, "kind") is "inSource" or "external";
    }
    private static string? Category(JsonElement run, JsonElement result, string id)
    {
        var direct = Text(Property(result, "properties"), "category");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;
        var rules = Items(Property(Property(run, "tool"), "driver"), "rules").ToArray();
        JsonElement rule = default;
        if (Integer(result, "ruleIndex") is { } index && index >= 0 && index < rules.Length && Text(rules[index], "id") == id)
            rule = rules[index];
        else
        {
            var matching = rules.Where(r => Text(r, "id") == id).Take(2).ToArray();
            if (matching.Length == 1)
                rule = matching[0];
        }
        var category = Text(Property(rule, "properties"), "category");
        return string.IsNullOrWhiteSpace(category) ? null : category;
    }

    private sealed class Locations(JsonElement run, Uri launchDirectory)
    {
        internal SiteSourceLocation? Read(JsonElement location, JsonElement result)
        {
            var physical = Property(location, "physicalLocation");
            var uri = Resolve(Property(physical, "artifactLocation"), WorkingDirectory(result), 0);
            if (uri is null)
                return null;
            var region = Property(physical, "region");
            if (region.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                return new(uri.LocalPath);
            if (region.ValueKind != JsonValueKind.Object)
                return null;
            if (!Position(region, "startLine", out var line) || !Position(region, "startColumn", out var column)
                || !Position(region, "endLine", out var endLine) || !Position(region, "endColumn", out var endColumn))
                return null;
            // Missing start/end context stays missing, rather than becoming line/column 1.
            if (line is null)
            {
                column = null;
                endLine = null;
                endColumn = null;
            }
            if (endLine is null)
                endColumn = null;
            try
            {
                var mapped = new SiteSourceLocation(uri.LocalPath, line, column, endLine, endColumn);
                return Text(run, "columnKind") is null or "utf16CodeUnits" ? mapped
                    : new(uri.LocalPath, line, null, endLine, null);
            }
            catch (ArgumentException)
            {
                return null;
            } // Only this unusable location is excluded.
        }

        private Uri? WorkingDirectory(JsonElement result)
        {
            var invocations = Items(run, "invocations").ToArray();
            JsonElement invocation = default;
            if (Integer(Property(result, "provenance"), "invocationIndex") is { } index)
            {
                if (index < 0 || index >= invocations.Length)
                    return null;
                invocation = invocations[index];
            }
            else if (invocations.Length == 1)
                invocation = invocations[0];
            else if (invocations.Length > 1)
                return null;
            var directory = Property(invocation, "workingDirectory");
            if (directory.ValueKind == JsonValueKind.Undefined)
                return launchDirectory;
            var resolved = Resolve(directory, launchDirectory, 0);
            return resolved is null ? null : new Uri(resolved.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        }

        private Uri? Resolve(JsonElement artifact, Uri? fallback, int depth)
        {
            if (depth >= 32 || artifact.ValueKind != JsonValueKind.Object)
                return null;
            var text = Text(artifact, "uri");
            if (text is null)
            {
                var entries = Property(run, "artifacts");
                if (Integer(artifact, "index") is not { } index || index < 0 || entries.ValueKind != JsonValueKind.Array || index >= entries.GetArrayLength())
                    return null;
                return Resolve(Property(entries[index], "location"), fallback, depth + 1);
            }
            if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out var uri))
                return null;
            if (!uri.IsAbsoluteUri)
            {
                var baseId = Text(artifact, "uriBaseId");
                var baseUri = baseId is null ? fallback : Resolve(Property(Property(run, "originalUriBaseIds"), baseId), fallback, depth + 1);
                if (baseUri is null || !Uri.TryCreate(baseUri, uri, out uri))
                    return null;
            }
            return uri.IsFile && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri : null;
        }
    }

    private static Uri DirectoryUri(string directory) => new(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
    private static bool Position(JsonElement region, string key, out int? value)
    {
        var node = Property(region, key);
        value = null;
        if (node.ValueKind == JsonValueKind.Undefined)
            return true;
        if (node.ValueKind != JsonValueKind.Number || !node.TryGetInt32(out var number) || number < 1)
            return false;
        value = number;
        return true;
    }
    private static JsonElement Property(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : default;
    private static string? Text(JsonElement node, string name) => Property(node, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static int? Integer(JsonElement node, string name) => Property(node, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;
    private static IEnumerable<JsonElement> Items(JsonElement node, string name) => Property(node, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
}
