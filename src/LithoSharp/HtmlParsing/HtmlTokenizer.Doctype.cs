namespace LithoSharp.HtmlParsing;

internal sealed partial class HtmlTokenizer
{
    private enum DoctypeState { BeforeName, Name, AfterName, AfterPublicKeyword, BeforePublic,
        PublicSingle, PublicDouble, AfterPublic, BetweenIdentifiers, AfterSystemKeyword,
        BeforeSystem, SystemSingle, SystemDouble, AfterSystem, Bogus }

    private HtmlToken ReadDoctype(int start)
    {
        HtmlTextBuilder? name = null, publicId = null, systemId = null;
        var forceQuirks = false;
        var state = DoctypeState.BeforeName;
        if (!Space(Peek()) && Peek() is not ('>' or -1)) Error("missing-whitespace-before-doctype-name");
        while (true)
        {
            Check();
            var c = Peek();
            if (c < 0)
            {
                if (state != DoctypeState.Bogus) { Error("eof-in-doctype"); forceQuirks = true; }
                return Finish();
            }
            switch (state)
            {
                case DoctypeState.BeforeName:
                    if (Space(c)) Read();
                    else if (c == '>') { Error("missing-doctype-name"); forceQuirks = true; Read(); return Finish(); }
                    else { name = Builder(); state = DoctypeState.Name; }
                    break;
                case DoctypeState.Name:
                    if (Space(c)) { Read(); state = DoctypeState.AfterName; }
                    else if (c == '>') { Read(); return Finish(); }
                    else AppendRead(name!, true, true);
                    break;
                case DoctypeState.AfterName:
                    if (Space(c)) Read();
                    else if (c == '>') { Read(); return Finish(); }
                    else if (Starts("PUBLIC", true)) { Consume(6); state = DoctypeState.AfterPublicKeyword; }
                    else if (Starts("SYSTEM", true)) { Consume(6); state = DoctypeState.AfterSystemKeyword; }
                    else { Error("invalid-character-sequence-after-doctype-name"); forceQuirks = true; state = DoctypeState.Bogus; }
                    break;
                case DoctypeState.AfterPublicKeyword:
                case DoctypeState.AfterSystemKeyword:
                    var isPublicKeyword = state == DoctypeState.AfterPublicKeyword;
                    if (Space(c)) { Read(); state = isPublicKeyword ? DoctypeState.BeforePublic : DoctypeState.BeforeSystem; }
                    else if (c is '"' or '\'')
                    {
                        Error(isPublicKeyword ? "missing-whitespace-after-doctype-public-keyword" : "missing-whitespace-after-doctype-system-keyword");
                        BeginIdentifier(isPublicKeyword, c);
                    }
                    else if (c == '>')
                    {
                        Error(isPublicKeyword ? "missing-doctype-public-identifier" : "missing-doctype-system-identifier");
                        forceQuirks = true; Read(); return Finish();
                    }
                    else
                    {
                        Error(isPublicKeyword ? "missing-quote-before-doctype-public-identifier" : "missing-quote-before-doctype-system-identifier");
                        forceQuirks = true; state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.BeforePublic:
                case DoctypeState.BeforeSystem:
                    var isPublic = state == DoctypeState.BeforePublic;
                    if (Space(c)) Read();
                    else if (c is '"' or '\'') BeginIdentifier(isPublic, c);
                    else if (c == '>')
                    {
                        Error(isPublic ? "missing-doctype-public-identifier" : "missing-doctype-system-identifier");
                        forceQuirks = true; Read(); return Finish();
                    }
                    else
                    {
                        Error(isPublic ? "missing-quote-before-doctype-public-identifier" : "missing-quote-before-doctype-system-identifier");
                        forceQuirks = true; state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.PublicSingle:
                case DoctypeState.PublicDouble:
                case DoctypeState.SystemSingle:
                case DoctypeState.SystemDouble:
                    var readingPublic = state is DoctypeState.PublicSingle or DoctypeState.PublicDouble;
                    var quote = state is DoctypeState.PublicSingle or DoctypeState.SystemSingle ? '\'' : '"';
                    if (c == quote) { Read(); state = readingPublic ? DoctypeState.AfterPublic : DoctypeState.AfterSystem; }
                    else if (c == '>')
                    {
                        Error(readingPublic ? "abrupt-doctype-public-identifier" : "abrupt-doctype-system-identifier");
                        forceQuirks = true; Read(); return Finish();
                    }
                    else AppendRead(readingPublic ? publicId! : systemId!);
                    break;
                case DoctypeState.AfterPublic:
                    if (Space(c)) { Read(); state = DoctypeState.BetweenIdentifiers; }
                    else if (c == '>') { Read(); return Finish(); }
                    else if (c is '"' or '\'')
                    {
                        Error("missing-whitespace-between-doctype-public-and-system-identifiers");
                        BeginIdentifier(false, c);
                    }
                    else
                    {
                        Error("missing-quote-before-doctype-system-identifier");
                        forceQuirks = true; state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.BetweenIdentifiers:
                    if (Space(c)) Read();
                    else if (c == '>') { Read(); return Finish(); }
                    else if (c is '"' or '\'') BeginIdentifier(false, c);
                    else
                    {
                        Error("missing-quote-before-doctype-system-identifier");
                        forceQuirks = true; state = DoctypeState.Bogus;
                    }
                    break;
                case DoctypeState.AfterSystem:
                    if (Space(c)) Read();
                    else if (c == '>') { Read(); return Finish(); }
                    else { Error("unexpected-character-after-doctype-system-identifier"); state = DoctypeState.Bogus; }
                    break;
                case DoctypeState.Bogus:
                    if (c == '>') { Read(); return Finish(); }
                    else
                    {
                        Read();
                        if (c == 0) Error("unexpected-null-character", CurrentSpan, currentLine, currentColumn);
                    }
                    break;
            }
        }
        void BeginIdentifier(bool isPublic, int quote)
        {
            Read();
            if (isPublic) { publicId = Builder(); state = quote == '\'' ? DoctypeState.PublicSingle : DoctypeState.PublicDouble; }
            else { systemId = Builder(); state = quote == '\'' ? DoctypeState.SystemSingle : DoctypeState.SystemDouble; }
        }
        HtmlToken Finish() => new(HtmlTokenKind.Doctype, new(start, position - start),
            Name: name?.Build(), PublicIdentifier: publicId?.Build(), SystemIdentifier: systemId?.Build(), ForceQuirks: forceQuirks);
    }
}
