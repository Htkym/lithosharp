using Syntamark.Compilation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;

namespace LithoSharp.Content;

internal sealed record MarkdownInclusionOrigin(MdRawRange OutputSpan, SiteSourceLocation Directive);

/// <summary>Markdown code inclusion diagnostic identifiers.</summary>
public static class MarkdownInclusionDiagnosticIds
{
    /// <summary>The referenced code file cannot be read.</summary>
    public const string MissingIncludeFile = "LSM011";

    /// <summary>The referenced code region is missing or unterminated.</summary>
    public const string MissingIncludeRegion = "LSM012";
}

/// <summary>
/// Resolves fenced code inclusion directives before parsing, for every Markdown
/// compiler at once. A fence whose info string carries <c>source="..."</c> has its
/// body replaced with the referenced file content (optionally a <c>#region</c>),
/// mirroring the MDX worker's <c>CodeBlock</c> source/region behavior and names.
/// </summary>
/// <remarks>
/// Resolution needs a file root: include targets must stay inside the input root
/// (existing path containment and reparse-point validation apply). Positions after
/// a splice refer to the resolved body; directive diagnostics use pre-splice
/// coordinates mapped back to the original file. The result retains snapshot-bound source maps.
/// </remarks>
internal static partial class MarkdownCodeInclusion
{
    /// <summary>A resolved inclusion: target bytes plus the directive span (body-relative).</summary>
    internal sealed record ResolvedInclusion(byte[] TargetBytes, int SpanStart, int SpanLength, string TargetRelativePath, string RawHash);

    /// <summary>Resolution outcome: the resolved body plus error diagnostics.</summary>
    internal sealed record InclusionResult(
        string Body,
        IReadOnlyList<ResolvedInclusion> Resolved,
        IReadOnlyList<SiteDiagnostic> Diagnostics)
    {
        internal MdSourceMap? SourceMap { get; init; }
        internal string? OriginalSourceId { get; init; }
        internal IReadOnlyList<MarkdownInclusionOrigin> Origins { get; init; } = [];
    }

    private sealed record IncludeTarget(string Content, string RelativePath, string RawHash,
        MdSourceSnapshot Source, MdRawRange SourceSpan);

    private sealed record BodyLine(string Text, int Offset, string Break);

    /// <summary>
    /// Resolves inclusions in <paramref name="body"/> (a suffix of <paramref name="fileText"/>).
    /// </summary>
    /// <param name="fileText">The full original file text (for diagnostic positions).</param>
    /// <param name="body">The Markdown body to resolve.</param>
    /// <param name="postDirectory">The containing post's directory (full path).</param>
    /// <param name="inputRoot">The content input root includes must stay inside (full path).</param>
    /// <param name="relativePath">The post's root-relative path (for diagnostics).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async ValueTask<InclusionResult> ResolveAsync(
        string fileText,
        string body,
        string postDirectory,
        string inputRoot,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileText);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(postDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var lines = SplitLines(body);
        var fences = FindInclusionFences(lines);
        var bodyOffset = fileText.Length - body.Length;
        if (bodyOffset < 0 || !fileText.AsSpan(bodyOffset).SequenceEqual(body))
            throw new ArgumentException("The body must be an exact suffix of its original source.", nameof(body));
        if (fences.Count == 0)
        {
            return new InclusionResult(body, [], [])
            {
                OriginalSourceId = relativePath,
            };
        }

        var original = new MdSourceSnapshot(relativePath, MdHash.Compute(fileText), fileText);
        var source = new SourceText(fileText);
        var resolved = new List<ResolvedInclusion>(fences.Count);
        var diagnostics = new List<SiteDiagnostic>();
        var segments = new List<MdSourceMapSegment>();
        var origins = new List<MarkdownInclusionOrigin>();
        var builder = new StringBuilder(body.Length);
        var cursor = 0;
        void AppendOriginal(int start, int length)
        {
            if (length == 0)
                return;
            segments.Add(new MdSourceMapSegment(new MdRawRange(builder.Length, length),
                new MdSourceRange(original, new MdRawRange(bodyOffset + start, length)), MdSegmentKind.Linear));
            builder.Append(body, start, length);
        }
        foreach (var fence in fences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendOriginal(cursor, fence.ContentStart - cursor);
            var (line, column) = source.GetLineAndColumn(
                Math.Min(bodyOffset + fence.FenceStart, fileText.Length));
            var location = new SiteSourceLocation(relativePath, line, column);
            var target = await ReadTargetAsync(
                fence, postDirectory, inputRoot, location, diagnostics, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                // Keep the original fence content on failure so output stays
                // complete; the error diagnostic fails the load.
                AppendOriginal(fence.ContentStart, fence.ContentEnd - fence.ContentStart);
            }
            else
            {
                var start = builder.Length;
                if (target.Content.Length != 0)
                    segments.Add(new MdSourceMapSegment(new MdRawRange(start, target.Content.Length),
                        new MdSourceRange(target.Source, target.SourceSpan), MdSegmentKind.Linear));
                builder.Append(target.Content);
                if (target.Content.Length > 0 && target.Content[^1] is not ('\r' or '\n'))
                {
                    segments.Add(new MdSourceMapSegment(new MdRawRange(builder.Length, 1), null, MdSegmentKind.Unknown));
                    builder.Append('\n');
                }
                origins.Add(new MarkdownInclusionOrigin(new MdRawRange(start, builder.Length - start), location));
                resolved.Add(new ResolvedInclusion(
                    Encoding.UTF8.GetBytes(target.Content), fence.FenceStart, fence.ContentEnd - fence.FenceStart,
                    target.RelativePath, target.RawHash));
            }

            cursor = fence.ContentEnd;
        }

        AppendOriginal(cursor, body.Length - cursor);
        var finalBody = builder.ToString();
        return new InclusionResult(finalBody, resolved, diagnostics)
        {
            OriginalSourceId = relativePath,
            SourceMap = new MdSourceMap(new MdSourceSnapshot("lithosharp:included:" + relativePath,
                MdHash.Compute(finalBody), finalBody), segments),
            Origins = origins.AsReadOnly(),
        };
    }

    private sealed record InclusionFence(
        int FenceStart,
        int ContentStart,
        int ContentEnd,
        string Source,
        string? Region);

    private static List<BodyLine> SplitLines(string text)
    {
        var lines = new List<BodyLine>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                var next = index + 1 < text.Length && text[index + 1] == '\n' ? index + 2 : index + 1;
                lines.Add(new BodyLine(text[start..index], start, text[index..next]));
                start = next;
                index = next - 1;
            }
            else if (text[index] == '\n')
            {
                lines.Add(new BodyLine(text[start..index], start, text[index..(index + 1)]));
                start = index + 1;
            }
        }

        lines.Add(new BodyLine(text[start..], start, string.Empty));
        return lines;
    }

    private static List<InclusionFence> FindInclusionFences(List<BodyLine> lines)
    {
        var fences = new List<InclusionFence>();
        var index = 0;
        while (index < lines.Count)
        {
            if (!TryParseFenceOpen(lines[index].Text, out var source, out var region, out var fenceChar, out var run))
            {
                index++;
                continue;
            }

            // Mirror block-fence consumption: to the matching close or end of body.
            var contentStart = lines[index].Offset + lines[index].Text.Length + lines[index].Break.Length;
            var cursor = index + 1;
            while (cursor < lines.Count && !IsFenceClose(lines[cursor].Text, fenceChar, run))
            {
                cursor++;
            }

            var contentEnd = cursor < lines.Count
                ? lines[cursor].Offset
                : lines[^1].Offset + lines[^1].Text.Length + lines[^1].Break.Length;
            if (source is not null)
                fences.Add(new InclusionFence(lines[index].Offset, contentStart, contentEnd, source, region));
            index = Math.Max(cursor + (cursor < lines.Count ? 1 : 0), index + 1);
        }

        return fences;
    }

    private static bool TryParseFenceOpen(
        string text, out string? source, out string? region, out char fenceChar, out int run)
    {
        source = null;
        region = null;
        fenceChar = '\0';
        run = 0;
        CountIndent(text, out var chars, out var columns);
        if (columns >= 4 || chars >= text.Length)
        {
            return false;
        }

        fenceChar = text[chars];
        if (fenceChar is not ('`' or '~'))
        {
            return false;
        }

        while (chars + run < text.Length && text[chars + run] == fenceChar)
        {
            run++;
        }

        if (run < 3)
        {
            return false;
        }

        var info = text[(chars + run)..];
        if (fenceChar == '`' && info.Contains('`'))
        {
            return false;
        }

        var sourceMatch = SourceAttribute().Match(info);
        if (!sourceMatch.Success || string.IsNullOrWhiteSpace(sourceMatch.Groups[1].Value))
        {
            // No (or empty) source: the fence renders as written.
            return true;
        }

        source = sourceMatch.Groups[1].Value;
        var regionMatch = RegionAttribute().Match(info);
        region = regionMatch.Success && !string.IsNullOrWhiteSpace(regionMatch.Groups[1].Value)
            ? regionMatch.Groups[1].Value
            : null;
        return true;
    }

    private static bool IsFenceClose(string text, char fenceChar, int minRun)
    {
        CountIndent(text, out var chars, out var columns);
        if (columns >= 4)
        {
            return false;
        }

        var run = 0;
        while (chars + run < text.Length && text[chars + run] == fenceChar)
        {
            run++;
        }

        return run >= Math.Max(3, minRun) && string.IsNullOrWhiteSpace(text[(chars + run)..]);
    }

    private static void CountIndent(string text, out int chars, out int columns)
    {
        columns = 0;
        chars = 0;
        while (chars < text.Length && (text[chars] is ' ' or '\t'))
        {
            columns = text[chars] == '\t' ? columns + (4 - (columns % 4)) : columns + 1;
            chars++;
        }
    }

    private static async ValueTask<IncludeTarget?> ReadTargetAsync(
        InclusionFence fence,
        string postDirectory,
        string inputRoot,
        SiteSourceLocation location,
        List<SiteDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        string fullPath;
        try
        {
            if (Path.IsPathRooted(fence.Source))
            {
                throw new InvalidOperationException($"Include source '{fence.Source}' must be relative.");
            }

            fullPath = Path.GetFullPath(Path.Combine(postDirectory, fence.Source));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new SiteDiagnostic(
                MarkdownInclusionDiagnosticIds.MissingIncludeFile, SiteDiagnosticSeverity.Error, exception.Message, location));
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await ContentPath.ReadAllBytesAsync(inputRoot, fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            diagnostics.Add(new SiteDiagnostic(
                ContentPath.UnsafePathDiagnosticId, SiteDiagnosticSeverity.Error, exception.Message, location));
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new SiteDiagnostic(
                MarkdownInclusionDiagnosticIds.MissingIncludeFile, SiteDiagnosticSeverity.Error, exception.Message, location));
            return null;
        }

        string content;
        try
        {
            content = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            diagnostics.Add(new SiteDiagnostic(
                MarkdownInclusionDiagnosticIds.MissingIncludeFile, SiteDiagnosticSeverity.Error,
                $"Include source '{fence.Source}' must be valid UTF-8.",
                location));
            return null;
        }

        var originalContent = content;
        var prefixLength = content.Length > 0 && content[0] == '\uFEFF' ? 1 : 0;
        if (prefixLength != 0)
        {
            content = content[1..];
        }

        string resolved;
        MdRawRange sourceSpan;
        if (fence.Region is null)
        {
            resolved = content;
            sourceSpan = new MdRawRange(0, content.Length);
        }
        else
        {
            try
            {
                sourceSpan = ExtractRegionSpan(content, fence.Region);
                resolved = content.Substring(sourceSpan.Start, sourceSpan.Length);
            }
            catch (InvalidOperationException exception)
            {
                diagnostics.Add(new SiteDiagnostic(
                    MarkdownInclusionDiagnosticIds.MissingIncludeRegion, SiteDiagnosticSeverity.Error, exception.Message, location));
                return null;
            }
        }

        var relativeTarget = Path.GetRelativePath(Path.GetFullPath(inputRoot), fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        _ = relativeTarget.Normalize(NormalizationForm.FormC);
        var rawHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new IncludeTarget(resolved, relativeTarget, rawHash,
            new MdSourceSnapshot(relativeTarget, rawHash, originalContent),
            new MdRawRange(prefixLength + sourceSpan.Start, sourceSpan.Length));
    }

    /// <summary>
    /// Extracts a <c>#region</c> with the worker's semantics (nesting depth,
    /// missing or unterminated regions fail). An empty name returns the source.
    /// </summary>
    internal static string ExtractRegion(string source, string? name)
    {
        var span = ExtractRegionSpan(source, name);
        return source.Substring(span.Start, span.Length);
    }

    private static MdRawRange ExtractRegionSpan(string source, string? name)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(name))
        {
            return new MdRawRange(0, source.Length);
        }
        var lines = source.Split('\n');
        var start = -1;
        var startOffset = 0;
        var offset = 0;
        var depth = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var opening = RegionOpen().Match(line);
            if (opening.Success)
            {
                if (start >= 0)
                {
                    depth++;
                }
                else if (string.Equals(opening.Groups[1].Value.Trim(), name, StringComparison.Ordinal))
                {
                    start = index + 1;
                    startOffset = offset + lines[index].Length + 1;
                    depth = 1;
                }
            }
            else if (start >= 0 && RegionClose().IsMatch(line) && --depth == 0)
            {
                return new MdRawRange(startOffset, Math.Max(0, offset - startOffset - 1));
            }
            offset += lines[index].Length + 1;
        }

        throw new InvalidOperationException($"Code region '{name}' is missing or unterminated.");
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [GeneratedRegex("(?:^|\\s)source=\"([^\"]*)\"")]
    private static partial Regex SourceAttribute();

    [GeneratedRegex("(?:^|\\s)region=\"([^\"]*)\"")]
    private static partial Regex RegionAttribute();

    [GeneratedRegex("^\\s*#region(?:\\s+(.*))?\\s*$")]
    private static partial Regex RegionOpen();

    [GeneratedRegex("^\\s*#endregion\\b")]
    private static partial Regex RegionClose();
}
