namespace LithoSharp.HtmlParsing;

internal sealed partial class HtmlTokenizer
{
    private enum ScriptState { Normal, EscapeStart, EscapeStartDash, Escaped, EscapedDash,
        EscapedDashDash, DoubleEscapeStart, DoubleEscaped, DoubleEscapedDash,
        DoubleEscapedDashDash, DoubleEscapedLessThan, DoubleEscapeEnd }
    private ScriptState scriptState;
    private int scriptWordLength;
    private bool scriptWordMatches;
    private bool ScriptCanClose => scriptState is ScriptState.Normal or ScriptState.Escaped or
        ScriptState.EscapedDash or ScriptState.EscapedDashDash;

    private void StartScriptWord() { scriptWordLength = 0; scriptWordMatches = true; }
    private void AppendScriptWord(int c)
    {
        // Only equality with "script" matters; never allocate the arbitrarily long input word.
        scriptWordMatches &= scriptWordLength < 6 && Lower((char)c) == "script"[Math.Min(scriptWordLength, 5)];
        if (scriptWordLength < 7) scriptWordLength++;
    }
    private bool ScriptWord => scriptWordMatches && scriptWordLength == 6;
    private void ReadScript(HtmlTextBuilder text)
    {
        var c = Peek();
        switch (scriptState)
        {
            case ScriptState.Normal:
                AppendRead(text);
                if (c == '<' && Peek() == '!')
                {
                    AppendRead(text);
                    scriptState = ScriptState.EscapeStart;
                }
                break;
            case ScriptState.EscapeStart:
                if (c == '-') { AppendRead(text); scriptState = ScriptState.EscapeStartDash; }
                else scriptState = ScriptState.Normal;
                break;
            case ScriptState.EscapeStartDash:
                if (c == '-') { AppendRead(text); scriptState = ScriptState.EscapedDashDash; }
                else scriptState = ScriptState.Normal;
                break;
            case ScriptState.Escaped:
            case ScriptState.EscapedDash:
            case ScriptState.EscapedDashDash:
                if (c == '-')
                {
                    AppendRead(text);
                    scriptState = scriptState == ScriptState.Escaped ? ScriptState.EscapedDash : ScriptState.EscapedDashDash;
                }
                else if (c == '<')
                {
                    AppendRead(text);
                    scriptState = ScriptState.Escaped;
                    if (Alpha(Peek())) { StartScriptWord(); scriptState = ScriptState.DoubleEscapeStart; }
                    else if (Peek() == '/') AppendRead(text);
                }
                else
                {
                    AppendRead(text);
                    scriptState = c == '>' && scriptState == ScriptState.EscapedDashDash
                        ? ScriptState.Normal : ScriptState.Escaped;
                }
                break;
            case ScriptState.DoubleEscapeStart:
            case ScriptState.DoubleEscapeEnd:
                if (Alpha(c)) { AppendScriptWord(c); AppendRead(text); }
                else if (Space(c) || c is '/' or '>')
                {
                    AppendRead(text);
                    scriptState = scriptState == ScriptState.DoubleEscapeStart
                        ? (ScriptWord ? ScriptState.DoubleEscaped : ScriptState.Escaped)
                        : (ScriptWord ? ScriptState.Escaped : ScriptState.DoubleEscaped);
                }
                else scriptState = scriptState == ScriptState.DoubleEscapeStart ? ScriptState.Escaped : ScriptState.DoubleEscaped;
                break;
            case ScriptState.DoubleEscaped:
            case ScriptState.DoubleEscapedDash:
            case ScriptState.DoubleEscapedDashDash:
                AppendRead(text);
                scriptState = c switch
                {
                    '<' => ScriptState.DoubleEscapedLessThan,
                    '-' => scriptState == ScriptState.DoubleEscaped ? ScriptState.DoubleEscapedDash : ScriptState.DoubleEscapedDashDash,
                    '>' when scriptState == ScriptState.DoubleEscapedDashDash => ScriptState.Normal,
                    _ => ScriptState.DoubleEscaped
                };
                break;
            case ScriptState.DoubleEscapedLessThan:
                if (c == '/') { AppendRead(text); StartScriptWord(); scriptState = ScriptState.DoubleEscapeEnd; }
                else scriptState = ScriptState.DoubleEscaped;
                break;
        }
    }

    private enum CommentState { Start, StartDash, Body, LessThan, LessThanBang, LessThanBangDash,
        LessThanBangDashDash, EndDash, End, EndBang }

    private HtmlToken ReadComment(int start)
    {
        var data = Builder();
        var state = CommentState.Start;
        var pending = position;
        while (true)
        {
            Check();
            var c = Peek();
            switch (state)
            {
                case CommentState.Start:
                    if (c == '-') { pending = position; Read(); state = CommentState.StartDash; }
                    else if (c == '>') { Error("abrupt-closing-of-empty-comment"); Read(); return Finish(); }
                    else state = CommentState.Body;
                    break;
                case CommentState.StartDash:
                    if (c == '-') { Read(); state = CommentState.End; }
                    else if (c == '>') { Error("abrupt-closing-of-empty-comment"); Read(); return Finish(); }
                    else if (c < 0) return Incomplete();
                    else { data.Append("-", new(pending, 1)); state = CommentState.Body; }
                    break;
                case CommentState.Body:
                    if (c < 0) return Incomplete();
                    if (c == '-') { pending = position; Read(); state = CommentState.EndDash; }
                    else if (c == '<') { AppendRead(data); state = CommentState.LessThan; }
                    else AppendRead(data);
                    break;
                case CommentState.LessThan:
                    if (c == '!') { AppendRead(data); state = CommentState.LessThanBang; }
                    else if (c == '<') AppendRead(data);
                    else state = CommentState.Body;
                    break;
                case CommentState.LessThanBang:
                    if (c == '-') { pending = position; Read(); state = CommentState.LessThanBangDash; }
                    else state = CommentState.Body;
                    break;
                case CommentState.LessThanBangDash:
                    if (c == '-') { Read(); state = CommentState.LessThanBangDashDash; }
                    else state = CommentState.EndDash;
                    break;
                case CommentState.LessThanBangDashDash:
                    if (c is not ('>' or -1)) Error("nested-comment");
                    state = CommentState.End;
                    break;
                case CommentState.EndDash:
                    if (c == '-') { Read(); state = CommentState.End; }
                    else if (c < 0) return Incomplete();
                    else { data.Append("-", new(pending, 1)); state = CommentState.Body; }
                    break;
                case CommentState.End:
                    if (c == '>') { Read(); return Finish(); }
                    if (c == '!') { Read(); state = CommentState.EndBang; }
                    else if (c == '-') { Read(); data.Append("-", new(pending++, 1)); }
                    else if (c < 0) return Incomplete();
                    else { data.Append("--", new(pending, 2)); state = CommentState.Body; }
                    break;
                case CommentState.EndBang:
                    if (c == '-')
                    {
                        data.Append("--!", new(pending, 3));
                        pending = position; Read(); state = CommentState.EndDash;
                    }
                    else if (c == '>') { Error("incorrectly-closed-comment"); Read(); return Finish(); }
                    else if (c < 0) return Incomplete();
                    else { data.Append("--!", new(pending, 3)); state = CommentState.Body; }
                    break;
            }
        }
        HtmlToken Incomplete() { Error("eof-in-comment"); return Finish(); }
        HtmlToken Finish() => new(HtmlTokenKind.Comment, new(start, position - start), Data: data.Build());
    }

    private HtmlToken ReadBogusComment(int start, int? initialDataStart = null)
    {
        var data = Builder();
        if (initialDataStart is int prefix && prefix < position)
            data.Append(source.Substring(prefix, position - prefix), new(prefix, position - prefix));
        while (Peek() >= 0 && Peek() != '>') AppendRead(data);
        if (Peek() == '>') Read();
        return new(HtmlTokenKind.Comment, new(start, position - start), Data: data.Build());
    }

    private HtmlToken? ReadProcessingInstruction(int start)
    {
        var name = Builder();
        if (!Alpha(Peek()) && Peek() != '_')
        {
            if (Peek() < 0) { Error("eof-in-processing-instruction"); return null; }
            Error("invalid-first-character-of-processing-instruction-target");
            return ReadBogusComment(start, start + 1);
        }
        while (HtmlCharacterReferences.IsAsciiAlphanumeric(Peek()) || Peek() is '-' or '_') AppendRead(name, false);
        if (Peek() < 0) { Error("eof-in-processing-instruction"); return null; }
        var target = name.Build();
        if (!Space(Peek()) && Peek() is not ('?' or '>'))
        {
            Error("invalid-processing-instruction-target");
            return ReadBogusComment(start, start + 1);
        }
        if (Lower(target.Value) is "xml" or "xml-stylesheet")
        {
            Error("disallowed-processing-instruction-target");
            return ReadBogusComment(start, start + 1);
        }
        while (Space(Peek())) Read();
        var data = Builder();
        while (Peek() >= 0)
        {
            if (Peek() == '>') { Read(); return Finish(); }
            if (Starts("?>")) { Consume(2); return Finish(); }
            AppendRead(data, false, diagnoseNull: false);
        }
        Error("eof-in-processing-instruction");
        return null;
        HtmlToken Finish() => new(HtmlTokenKind.ProcessingInstruction, new(start, position - start),
            Name: target, Data: data.Build());
    }
}
