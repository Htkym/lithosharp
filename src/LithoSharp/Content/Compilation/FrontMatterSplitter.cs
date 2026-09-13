namespace LithoSharp.Content.Compilation;

/// <summary>Shared front matter scan outcome.</summary>
internal enum FrontMatterSplitStatus
{
    /// <summary>YAML and body were found.</summary>
    Ok,

    /// <summary>The document does not start with a YAML front matter marker.</summary>
    MissingFrontMatter,

    /// <summary>No closing front matter marker was found.</summary>
    UnterminatedFrontMatter,

    /// <summary>The front matter block is empty or whitespace-only.</summary>
    EmptyFrontMatter,
}

/// <summary>Offset-aware front matter split result.</summary>
/// <param name="Status">Scan outcome.</param>
/// <param name="Yaml">YAML text (only when <see cref="Status"/> is Ok or EmptyFrontMatter).</param>
/// <param name="Body">Body text (when <see cref="Status"/> is Ok or EmptyFrontMatter).</param>
/// <param name="BodyStartOffset">UTF-16 offset where the body starts (when Ok or EmptyFrontMatter).</param>
/// <param name="FailureLine">1-based line for the failure location (otherwise 1).</param>
internal readonly record struct FrontMatterSplit(
    FrontMatterSplitStatus Status,
    string Yaml,
    string Body,
    int BodyStartOffset,
    int FailureLine);

/// <summary>
/// Single scanner shared by the legacy front matter splitter and the
/// position-aware loader. A leading UTF-8 BOM is skipped, matching the
/// collection loader, source generators, and MDX handling.
/// </summary>
internal static class FrontMatterSplitter
{
    /// <summary>Scans the document for a YAML front matter block.</summary>
    public static FrontMatterSplit TrySplit(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        var openingEnd = FindLineEnd(text, start);
        if (!LineEquals(text, start, openingEnd, "---"))
        {
            return new FrontMatterSplit(FrontMatterSplitStatus.MissingFrontMatter, string.Empty, string.Empty, 0, 1);
        }

        var yamlStart = SkipLineBreak(text, openingEnd);
        var line = 2;
        var lineStart = yamlStart;
        while (lineStart < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lineEnd = FindLineEnd(text, lineStart);
            if (LineEquals(text, lineStart, lineEnd, "---"))
            {
                var bodyStart = SkipLineBreak(text, lineEnd);
                var yaml = text[yamlStart..lineStart];
                if (string.IsNullOrWhiteSpace(yaml))
                {
                    return new FrontMatterSplit(FrontMatterSplitStatus.EmptyFrontMatter, yaml, text[bodyStart..], bodyStart, 2);
                }

                return new FrontMatterSplit(FrontMatterSplitStatus.Ok, yaml, text[bodyStart..], bodyStart, 1);
            }

            lineStart = SkipLineBreak(text, lineEnd);
            line++;
        }

        return new FrontMatterSplit(FrontMatterSplitStatus.UnterminatedFrontMatter, string.Empty, string.Empty, 0, line);
    }

    private static int FindLineEnd(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] is '\n' or '\r')
            {
                return index;
            }
        }

        return text.Length;
    }

    private static int SkipLineBreak(string text, int lineEnd)
    {
        if (lineEnd >= text.Length)
        {
            return lineEnd;
        }

        if (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n')
        {
            return lineEnd + 2;
        }

        return lineEnd + 1;
    }

    private static bool LineEquals(string text, int start, int end, string expected)
    {
        if (end > start && text[end - 1] == '\r')
        {
            end--;
        }

        return text.AsSpan(start, end - start).SequenceEqual(expected);
    }
}
