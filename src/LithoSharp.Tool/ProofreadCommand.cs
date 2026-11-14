using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Proofreading;
using LithoSharp.Quality;
using Syntamark;

namespace LithoSharp.Tool;

internal static class ProofreadCommand
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        const string usage = "Usage: lithosharp proofread <markdown-file> --python <executable> --yomiyasu <lint-script> [--profile id] [--protect identifier] [--format text|json] [--preview segment:start:length --replacement text]";
        if (args.Length == 0 || args[0].StartsWith('-'))
            throw new CliUsageException(usage);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var identifiers = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            var key = args[index];
            if (key is not ("--python" or "--yomiyasu" or "--profile" or "--protect" or "--format" or "--preview" or "--replacement")
                || ++index >= args.Length)
                throw new CliUsageException(usage);
            if (key == "--protect")
                identifiers.Add(args[index]);
            else if (!values.TryAdd(key, args[index]))
                throw new CliUsageException(usage);
        }
        if (!values.ContainsKey("--python") || !values.ContainsKey("--yomiyasu")
            || values.ContainsKey("--preview") != values.ContainsKey("--replacement"))
            throw new CliUsageException(usage);
        var format = values.GetValueOrDefault("--format", "text");
        if (format is not ("text" or "json"))
            throw new CliUsageException(usage);
        var path = Path.GetFullPath(args[0]);
        byte[] raw;
        using (var input = BuildInputFingerprint.OpenVerifiedContainedRead(Path.GetDirectoryName(path)!, path, asynchronous: true))
        {
            if (input.Length > 4 * 1024 * 1024)
                throw new CliUsageException("Proofreading input exceeds the supported 4 MiB byte limit.");
            raw = new byte[(int)input.Length];
            await input.ReadExactlyAsync(raw, cancellationToken).ConfigureAwait(false);
            if (input.ReadByte() != -1)
                throw new IOException("The proofreading input changed while it was captured.");
        }
        var source = new SourceSnapshot(path, Convert.ToHexStringLower(SHA256.HashData(raw)), new UTF8Encoding(false, true).GetString(raw));
        var selection = MarkdownProofreading.Select(source, values.GetValueOrDefault("--profile", MarkdownOptions.LegacyProfile), identifiers, cancellationToken);
        var result = await YomiyasuProofreader.CheckAsync(selection, values["--python"], values["--yomiyasu"], cancellationToken).ConfigureAwait(false);
        ProofreadingEditPreview? preview = null;
        if (values.TryGetValue("--preview", out var range))
        {
            var parts = range.Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var segment)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
                throw new CliUsageException("The preview uses a zero-based segment, code-point start and code-point length.");
            preview = selection.PreviewReplacement(segment, start, length, values["--replacement"]);
        }
        var diagnostics = result.Findings.Select(finding => finding.Diagnostic).ToArray();
        if (format == "json")
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                sourceId = source.SourceId,
                version = source.Version,
                sourceHash = source.TextHash,
                selection.ProfileId,
                selection.ParserVersion,
                parserStatus = selection.ParseStatus.ToString(),
                result.State,
                result.WasRun,
                result.ToolVersion,
                selectedSegments = selection.Segments.Count,
                segments = selection.Segments.Select((segment, index) => new { index, segment.Kind, segment.Text }),
                exclusions = selection.Exclusions,
                diagnostics,
                preview = preview is null ? null : new
                {
                    preview.OriginalText,
                    preview.Replacement,
                    preview.CanApply,
                    preview.MappingPrecision,
                    preview.Reason,
                    fragments = preview.Fragments.Select(fragment => new
                    {
                        sourceId = fragment.Source.SourceId,
                        version = fragment.Source.Version,
                        hash = fragment.Source.TextHash,
                        fragment.Span
                    }),
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        else
        {
            Console.WriteLine($"Proofreading: {result.State}; selected segments: {selection.Segments.Count}; findings: {diagnostics.Length}.");
            Console.WriteLine(new SiteQualityReport(diagnostics).Format(SiteDiagnosticFormat.Text));
            if (preview is not null)
            {
                Console.WriteLine($"Replacement preview (applicable: {preview.CanApply}, precision: {preview.MappingPrecision}):");
                Console.WriteLine(preview.OriginalText + " -> " + preview.Replacement);
                if (preview.Reason is not null)
                    Console.WriteLine(preview.Reason);
            }
        }
        return result.State == "Complete" ? 0 : 1;
    }
}
