namespace LithoSharp.HtmlParsing;

// All offsets refer to the caller's original UTF-16 string, before CRLF or entity decoding.
internal readonly record struct HtmlSpan(int Start, int Length)
{
    public int End => Start + Length;
}

internal readonly record struct HtmlTextSegment(int ValueStart, int ValueLength, HtmlSpan Source);

internal sealed record HtmlText(string Value, HtmlSpan Source, IReadOnlyList<HtmlTextSegment> Segments);

internal sealed record HtmlAttribute(HtmlText Name, HtmlText Value);

internal enum HtmlTokenKind { Text, StartTag, EndTag, Comment, Doctype, ProcessingInstruction, EndOfFile }
internal enum HtmlTextMode { Data, RcData, RawText, ScriptData, PlainText }
internal enum HtmlTokenizationStatus { Complete, Partial, Failed }

internal sealed record HtmlToken(
    HtmlTokenKind Kind,
    HtmlSpan Source,
    HtmlText? Name = null,
    HtmlText? Data = null,
    IReadOnlyList<HtmlAttribute>? Attributes = null,
    bool SelfClosing = false,
    HtmlText? PublicIdentifier = null,
    HtmlText? SystemIdentifier = null,
    bool ForceQuirks = false);

internal sealed record HtmlTokenizationDiagnostic(string Code, HtmlSpan Source, int Line, int Column);

// lithosharp-html-tokenizer/1. Tree depth and selector budgets belong to HT-03/HT-05.
internal sealed record HtmlTokenizerLimits
{
    public int MaxInputChars { get; init; } = 4 * 1024 * 1024;
    public int MaxInputUtf8Bytes { get; init; } = 16 * 1024 * 1024;
    public int MaxTokens { get; init; } = 500_000;
    public int MaxAttributesPerTag { get; init; } = 256;
    public int MaxValueChars { get; init; } = 1024 * 1024;
    public int MaxTextChunkChars { get; init; } = 4096;
    public int MaxDecodedChars { get; init; } = 8 * 1024 * 1024;
    public int MaxSourceSegments { get; init; } = 1_000_000;
    public int MaxDiagnostics { get; init; } = 1024;

    internal void Validate()
    {
        if (MaxInputChars < 1 || MaxInputUtf8Bytes < 1 || MaxTokens < 1 ||
            MaxAttributesPerTag < 1 || MaxValueChars < 1 || MaxTextChunkChars < 1 ||
            MaxDecodedChars < 1 || MaxSourceSegments < 1 || MaxDiagnostics < 1)
            throw new ArgumentOutOfRangeException(nameof(HtmlTokenizerLimits), "Tokenizer limits must be positive.");
    }
}
