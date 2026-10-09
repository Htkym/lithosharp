using System.Text;

namespace LithoSharp.HtmlParsing;

// The tree builder explicitly supplies text mode and foreign-content context.
internal sealed partial class HtmlTokenizer
{
    private readonly string source;
    private readonly HtmlTokenizerLimits limits;
    private readonly CancellationToken cancellationToken;
    private readonly List<HtmlTokenizationDiagnostic> diagnostics = [];
    private int position, line = 1, column = 1, steps, tokens, decodedChars, segments;
    private int currentStart, currentLength, currentLine, currentColumn;
    private bool aborted, finished, allowCdata, inCdata;
    private HtmlTextMode mode;
    private string? endTag;

    public HtmlTokenizer(string source, HtmlTokenizerLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        this.limits = limits ?? new();
        this.limits.Validate();
        this.cancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > this.limits.MaxInputChars) FailInput("input-char-budget");
        else if (Encoding.UTF8.GetByteCount(source) > this.limits.MaxInputUtf8Bytes) FailInput("input-byte-budget");
    }

    public HtmlTokenizationStatus Status { get; private set; } = HtmlTokenizationStatus.Complete;
    public IReadOnlyList<HtmlTokenizationDiagnostic> Diagnostics => diagnostics.AsReadOnly();
    public int ConsumedRawChars => position;

    public void SetContext(HtmlTextMode mode, string? endTag = null, bool allowCdata = false)
    {
        if (finished || aborted) throw new InvalidOperationException("Tokenization has ended.");
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode is HtmlTextMode.RcData or HtmlTextMode.RawText or HtmlTextMode.ScriptData && string.IsNullOrEmpty(endTag))
            throw new ArgumentException("A text element name is required.", nameof(endTag));
        this.mode = mode;
        this.endTag = endTag is null ? null : Lower(endTag);
        this.allowCdata = allowCdata;
        scriptState = ScriptState.Normal;
    }

    public HtmlToken NextToken()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (aborted || finished) return Eof();
        try
        {
            if (position < source.Length && tokens >= limits.MaxTokens)
                throw new HtmlTokenizerBudgetException("token-budget");
            var text = Builder();
            while (true)
            {
                Check();
                if (text.Length >= limits.MaxTextChunkChars) return EmitText(text);
                if (Peek() < 0)
                {
                    if (inCdata) { Error("eof-in-cdata"); inCdata = false; }
                    if (mode == HtmlTextMode.ScriptData && scriptState is not
                        (ScriptState.Normal or ScriptState.EscapeStart or ScriptState.EscapeStartDash))
                    {
                        Error("eof-in-script-html-comment-like-text");
                        scriptState = ScriptState.Normal;
                    }
                    return text.Length > 0 ? EmitText(text) : Eof();
                }
                if (inCdata)
                {
                    if (Starts("]]>")) { Consume(3); inCdata = false; }
                    else AppendRead(text, false, diagnoseNull: false);
                    continue;
                }
                if (mode is HtmlTextMode.RcData or HtmlTextMode.RawText or HtmlTextMode.ScriptData &&
                    (mode != HtmlTextMode.ScriptData || ScriptCanClose) && IsAppropriateEndTag())
                {
                    if (text.Length > 0) return EmitText(text);
                    var start = position;
                    Consume(2);
                    var token = ReadTag(start, true);
                    mode = HtmlTextMode.Data;
                    scriptState = ScriptState.Normal;
                    if (token is not null) return Emit(token);
                    continue;
                }
                if (mode == HtmlTextMode.ScriptData) { ReadScript(text); continue; }
                if (mode == HtmlTextMode.Data && Peek() == '<')
                {
                    if (text.Length > 0) return EmitText(text);
                    var token = ReadMarkup(text);
                    if (token is not null) return Emit(token);
                }
                else if (mode is HtmlTextMode.Data or HtmlTextMode.RcData && Peek() == '&')
                    ReadReference(text, false);
                else AppendRead(text, mode != HtmlTextMode.Data);
            }
        }
        catch (HtmlTokenizerBudgetException ex)
        {
            aborted = true;
            Status = HtmlTokenizationStatus.Partial;
            var diagnostic = new HtmlTokenizationDiagnostic(ex.Code, new(position, 0), line, column);
            if (diagnostics.Count < limits.MaxDiagnostics) diagnostics.Add(diagnostic);
            else diagnostics[^1] = diagnostic;
            return Eof();
        }
    }

    private HtmlToken? ReadMarkup(HtmlTextBuilder text)
    {
        var start = position;
        Read();
        if (Alpha(Peek())) return ReadTag(start, false);
        switch (Peek())
        {
            case '/':
                Read();
                if (Alpha(Peek())) return ReadTag(start, true);
                if (Peek() == '>') { Error("missing-end-tag-name"); Read(); return null; }
                if (Peek() < 0) { Error("eof-before-tag-name"); text.Append("</", new(start, 2)); return null; }
                Error("invalid-first-character-of-tag-name");
                return ReadBogusComment(start);
            case '!':
                Read();
                if (Starts("--")) { Consume(2); return ReadComment(start); }
                if (Starts("DOCTYPE", true)) { Consume(7); return ReadDoctype(start); }
                if (Starts("[CDATA["))
                {
                    if (allowCdata) { Consume(7); inCdata = true; return null; }
                    Error("cdata-in-html-content");
                }
                else Error("incorrectly-opened-comment");
                return ReadBogusComment(start);
            case '?':
                Read();
                return ReadProcessingInstruction(start);
            default:
                Error(Peek() < 0 ? "eof-before-tag-name" : "invalid-first-character-of-tag-name");
                text.Append("<", new(start, 1));
                return null;
        }
    }

    private enum TagState { Name, BeforeAttribute, AttributeName, AfterAttributeName, BeforeValue,
        SingleValue, DoubleValue, UnquotedValue, AfterQuotedValue, SelfClosing }

    private HtmlToken? ReadTag(int start, bool closing)
    {
        var state = TagState.Name;
        var name = Builder();
        var attributes = new List<HtmlAttribute>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        HtmlTextBuilder? attributeName = null, attributeValue = null;
        HtmlText? completedName = null;
        var duplicate = false;
        var selfClosing = false;
        var attempts = 0;
        var attributeLine = 0;
        var attributeColumn = 0;
        while (true)
        {
            Check();
            var c = Peek();
            switch (state)
            {
                case TagState.Name:
                    if (c < 0 || Space(c) || c is '/' or '>') state = TagState.BeforeAttribute;
                    else AppendRead(name, true, true);
                    break;
                case TagState.BeforeAttribute:
                    if (Space(c)) Read();
                    else if (c == '/') { Read(); state = TagState.SelfClosing; }
                    else if (c == '>') { Read(); return Finish(); }
                    else if (c < 0) return Incomplete();
                    else
                    {
                        if (++attempts > limits.MaxAttributesPerTag)
                            throw new HtmlTokenizerBudgetException("attribute-budget");
                        attributeLine = line; attributeColumn = column;
                        attributeName = Builder(); attributeValue = Builder();
                        state = TagState.AttributeName;
                        if (c == '=') { Error("unexpected-equals-sign-before-attribute-name"); AppendRead(attributeName, true, true); }
                    }
                    break;
                case TagState.AttributeName:
                    if (c < 0 || Space(c) || c is '/' or '>' or '=')
                    {
                        completedName = attributeName!.Build();
                        duplicate = !names.Add(completedName.Value);
                        if (duplicate) Error("duplicate-attribute", completedName.Source, attributeLine, attributeColumn);
                        state = TagState.AfterAttributeName;
                    }
                    else
                    {
                        if (c is '"' or '\'' or '<') Error("unexpected-character-in-attribute-name");
                        AppendRead(attributeName!, true, true);
                    }
                    break;
                case TagState.AfterAttributeName:
                    if (Space(c)) Read();
                    else if (c == '=') { Read(); state = TagState.BeforeValue; }
                    else if (c == '/') { FinishAttribute(); Read(); state = TagState.SelfClosing; }
                    else if (c == '>') { FinishAttribute(); Read(); return Finish(); }
                    else if (c < 0) return Incomplete();
                    else { FinishAttribute(); state = TagState.BeforeAttribute; }
                    break;
                case TagState.BeforeValue:
                    if (Space(c)) Read();
                    else if (c is '"' or '\'')
                    {
                        Read(); attributeValue = Builder();
                        state = c == '"' ? TagState.DoubleValue : TagState.SingleValue;
                    }
                    else if (c == '>') { Error("missing-attribute-value"); FinishAttribute(); Read(); return Finish(); }
                    else if (c < 0) return Incomplete();
                    else { attributeValue = Builder(); state = TagState.UnquotedValue; }
                    break;
                case TagState.SingleValue:
                case TagState.DoubleValue:
                    if (c == (state == TagState.SingleValue ? '\'' : '"')) { Read(); state = TagState.AfterQuotedValue; }
                    else if (c < 0) return Incomplete();
                    else if (c == '&') ReadReference(attributeValue!, true);
                    else AppendRead(attributeValue!);
                    break;
                case TagState.UnquotedValue:
                    if (Space(c)) { FinishAttribute(); Read(); state = TagState.BeforeAttribute; }
                    else if (c == '>') { FinishAttribute(); Read(); return Finish(); }
                    else if (c < 0) return Incomplete();
                    else if (c == '&') ReadReference(attributeValue!, true);
                    else
                    {
                        if (c is '"' or '\'' or '<' or '=' or 0x60) Error("unexpected-character-in-unquoted-attribute-value");
                        AppendRead(attributeValue!);
                    }
                    break;
                case TagState.AfterQuotedValue:
                    if (Space(c)) { FinishAttribute(); Read(); state = TagState.BeforeAttribute; }
                    else if (c == '/') { FinishAttribute(); Read(); state = TagState.SelfClosing; }
                    else if (c == '>') { FinishAttribute(); Read(); return Finish(); }
                    else if (c < 0) return Incomplete();
                    else { Error("missing-whitespace-between-attributes"); FinishAttribute(); state = TagState.BeforeAttribute; }
                    break;
                case TagState.SelfClosing:
                    if (c == '>') { selfClosing = true; Read(); return Finish(); }
                    else if (c < 0) return Incomplete();
                    else { Error("unexpected-solidus-in-tag"); state = TagState.BeforeAttribute; }
                    break;
            }
        }

        void FinishAttribute()
        {
            if (completedName is not null && !duplicate)
                attributes.Add(new(completedName, attributeValue!.Build()));
            completedName = null; attributeName = null; attributeValue = null;
        }
        HtmlToken? Incomplete() { Error("eof-in-tag"); return null; }
        HtmlToken Finish()
        {
            if (closing && attributes.Count > 0) Error("end-tag-with-attributes");
            if (closing && selfClosing) Error("end-tag-with-trailing-solidus");
            return new(closing ? HtmlTokenKind.EndTag : HtmlTokenKind.StartTag,
                new(start, position - start), Name: name.Build(),
                Attributes: Array.AsReadOnly(attributes.ToArray()), SelfClosing: selfClosing);
        }
    }

    private HtmlTextBuilder Builder() => new(position, limits.MaxValueChars, Reserve);
    private void Reserve(int count, bool segment)
    {
        if (count > limits.MaxDecodedChars - decodedChars) throw new HtmlTokenizerBudgetException("decoded-char-budget");
        if (segment && segments >= limits.MaxSourceSegments) throw new HtmlTokenizerBudgetException("source-segment-budget");
        decodedChars += count;
        if (segment) segments++;
    }
    private int Peek() => position < source.Length ? source[position] : -1;
    private int Read()
    {
        Check();
        currentStart = position; currentLength = 0; currentLine = line; currentColumn = column;
        if (position == source.Length) return -1;
        var c = (int)source[position++];
        if (c == '\r')
        {
            if (position < source.Length && source[position] == '\n') position++;
            c = '\n';
        }
        else if (c is >= 0xD800 and <= 0xDBFF && position < source.Length && char.IsLowSurrogate(source[position]))
            c = char.ConvertToUtf32((char)c, source[position++]);
        currentLength = position - currentStart;
        if (c == '\n') { line++; column = 1; } else column += currentLength;
        if (c is >= 0xD800 and <= 0xDFFF) Error("surrogate-in-input-stream", CurrentSpan, currentLine, currentColumn);
        else if (HtmlCharacterReferences.IsNoncharacter(c)) Error("noncharacter-in-input-stream", CurrentSpan, currentLine, currentColumn);
        else if (HtmlCharacterReferences.IsControl(c)) Error("control-character-in-input-stream", CurrentSpan, currentLine, currentColumn);
        return c;
    }
    private HtmlSpan CurrentSpan => new(currentStart, currentLength);
    private void AppendRead(HtmlTextBuilder builder, bool replaceNull = true, bool lower = false, bool diagnoseNull = true)
    {
        var c = Read();
        if (c == 0)
        {
            if (diagnoseNull) Error("unexpected-null-character", CurrentSpan, currentLine, currentColumn);
            if (replaceNull) c = 0xFFFD;
        }
        if (lower && c is >= 'A' and <= 'Z') c += 32;
        builder.Append(c <= 0xFFFF ? ((char)c).ToString() : char.ConvertFromUtf32(c), CurrentSpan);
    }
    private void ReadReference(HtmlTextBuilder builder, bool attribute)
    {
        var start = position; var startLine = line; var startColumn = column;
        var reference = HtmlCharacterReferences.Read(source, position, attribute, cancellationToken);
        Consume(reference.RawLength);
        var span = new HtmlSpan(start, reference.RawLength);
        foreach (var (flag, code) in ReferenceErrors)
            if ((reference.Errors & flag) != 0) Error(code, span, startLine, startColumn);
        builder.Append(reference.Value, span);
    }
    private static readonly (HtmlReferenceErrors, string)[] ReferenceErrors =
    [
        (HtmlReferenceErrors.MissingSemicolon, "missing-semicolon-after-character-reference"),
        (HtmlReferenceErrors.UnknownName, "unknown-named-character-reference"),
        (HtmlReferenceErrors.MissingDigits, "absence-of-digits-in-numeric-character-reference"),
        (HtmlReferenceErrors.Null, "null-character-reference"),
        (HtmlReferenceErrors.OutOfRange, "character-reference-outside-unicode-range"),
        (HtmlReferenceErrors.Surrogate, "surrogate-character-reference"),
        (HtmlReferenceErrors.Noncharacter, "noncharacter-character-reference"),
        (HtmlReferenceErrors.Control, "control-character-reference")
    ];
    private void Consume(int count) { for (var i = 0; i < count; i++) Read(); }
    private void Check() { if ((++steps & 255) == 0) cancellationToken.ThrowIfCancellationRequested(); }
    private void Error(string code, HtmlSpan? span = null, int? errorLine = null, int? errorColumn = null)
    {
        if (diagnostics.Count >= limits.MaxDiagnostics) throw new HtmlTokenizerBudgetException("diagnostic-budget");
        diagnostics.Add(new(code, span ?? new(position, position < source.Length ? 1 : 0), errorLine ?? line, errorColumn ?? column));
    }
    private void FailInput(string code)
    {
        aborted = true; Status = HtmlTokenizationStatus.Failed;
        diagnostics.Add(new(code, new(0, 0), 1, 1));
    }
    private HtmlToken Eof() { finished = true; return new(HtmlTokenKind.EndOfFile, new(position, 0)); }
    private HtmlToken Emit(HtmlToken token) { tokens++; return token; }
    private HtmlToken EmitText(HtmlTextBuilder builder)
    {
        var data = builder.Build();
        return Emit(new(HtmlTokenKind.Text, data.Source, Data: data));
    }
    private static bool Alpha(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool Space(int c) => c is '\t' or '\n' or '\r' or '\f' or ' ';
    private static char Lower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
    private static string Lower(string text) => new(text.Select(Lower).ToArray());
    private bool Starts(string text, bool ignoreCase = false)
    {
        if (source.Length - position < text.Length) return false;
        for (var i = 0; i < text.Length; i++)
            if ((ignoreCase ? Lower(source[position + i]) : source[position + i]) !=
                (ignoreCase ? Lower(text[i]) : text[i])) return false;
        return true;
    }
    private bool IsAppropriateEndTag()
    {
        if (endTag is null || !Starts("</") || source.Length - position < endTag.Length + 3) return false;
        for (var i = 0; i < endTag.Length; i++)
            if (Lower(source[position + 2 + i]) != endTag[i]) return false;
        var next = source[position + 2 + endTag.Length];
        return Space(next) || next is '/' or '>';
    }
}
