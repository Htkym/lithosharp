using System.Globalization;
using System.Text;
using LithoSharp.HtmlParsing;

namespace LithoSharp.Testing;

/// <summary>A malformed selector in the declared Testing query grammar.</summary>
public sealed class HtmlSelectorSyntaxException : ArgumentException
{
    /// <summary>Creates a selector syntax error.</summary>
    public HtmlSelectorSyntaxException(string message) : base(message) { }
}

/// <summary>A selector feature or complexity outside the declared Testing query grammar.</summary>
public sealed class HtmlSelectorUnsupportedException : NotSupportedException
{
    /// <summary>Creates an unsupported selector error.</summary>
    public HtmlSelectorUnsupportedException(string message) : base(message) { }
}

// A bounded recursive-descent selector parser. No browser state, mutation or persistent query cache.
internal sealed class HtmlSelector(List<HtmlSelector.Complex> selectors)
{
    internal bool Matches(int id, MatchContext context) => selectors.Any(selector => selector.Matches(id, context));

    internal static HtmlSelector Parse(string selector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (string.IsNullOrWhiteSpace(selector)) throw new ArgumentException("A selector must not be empty.", nameof(selector));
        if (selector.Length > 4096) throw new HtmlSelectorUnsupportedException("Selector length exceeds 4096 UTF-16 units.");
        cancellationToken.ThrowIfCancellationRequested();
        var parser = new Parser(selector.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\f', '\n').Replace('\0', '\uFFFD'), cancellationToken);
        var parsed = parser.List(nested: false);
        parser.Trivia();
        if (!parser.End) throw parser.Syntax();
        return parsed;
    }

    internal static bool NameEquals(string actual, string expected, bool insensitive) =>
        insensitive ? AsciiEquals(actual, expected) : actual == expected;

    private static bool AsciiEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
            if (Lower(left[i]) != Lower(right[i])) return false;
        return true;
    }

    private static char Lower(char value) => value is >= 'A' and <= 'Z' ? (char)(value + 32) : value;
    private static string AsciiLower(string value) => new(value.Select(Lower).ToArray());
    private static bool Space(char value) => value is ' ' or '\t' or '\n' or '\r' or '\f';
    private static bool WordContains(string value, string expected, bool insensitive) =>
        expected.Length != 0 && !expected.Any(Space) &&
        value.Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries).Any(word => NameEquals(word, expected, insensitive));

    internal sealed class MatchContext(HtmlTestDocument owner, int scope, CancellationToken cancellationToken)
    {
        private int operations;
        internal HtmlTestDocument Owner => owner;
        internal int Scope => scope;
        internal bool Quirks => owner.Tree.DocumentMode == HtmlDocumentMode.Quirks;
        internal void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.EnsureAlive();
            if (++operations > 4_000_000) throw new HtmlSelectorUnsupportedException("Selector matching exceeds the operation budget.");
        }
    }

    internal sealed class Complex(List<(List<Func<int, MatchContext, bool>> Tests, char Relation)> parts)
    {
        internal bool Matches(int id, MatchContext context) => At(parts.Count - 1, id, context);

        private bool At(int part, int id, MatchContext context)
        {
            context.Step();
            if (id < 0 || context.Owner.Tree.Nodes[id].Kind != HtmlTreeNodeKind.Element) return false;
            foreach (var test in parts[part].Tests) { context.Step(); if (!test(id, context)) return false; }
            if (part == 0) return true;
            var owner = context.Owner;
            var parent = owner.Tree.Nodes[id].Parent ?? -1;
            switch (parts[part].Relation)
            {
                case '>': return At(part - 1, parent, context);
                case '+': return At(part - 1, owner.Previous[id], context);
                case '~':
                    for (var previous = owner.Previous[id]; previous >= 0; previous = owner.Previous[previous])
                        if (At(part - 1, previous, context)) return true;
                    return false;
                default:
                    while (parent >= 0)
                    {
                        if (At(part - 1, parent, context)) return true;
                        parent = owner.Tree.Nodes[parent].Parent ?? -1;
                    }
                    return false;
            }
        }
    }

    private sealed class Parser(string source, CancellationToken cancellationToken)
    {
        private int position;
        private int depth;
        private int compounds;
        private char Current => position < source.Length ? source[position] : '\0';
        internal bool End => position == source.Length;
        private char Peek(int offset) => position + offset < source.Length ? source[position + offset] : '\0';
        internal HtmlSelectorSyntaxException Syntax() => new($"Malformed selector near UTF-16 offset {position}.");
        private static HtmlSelectorUnsupportedException Unsupported(string feature) => new($"Unsupported selector feature: {feature}.");
        private bool Take(char value) { if (Current != value) return false; position++; return true; }
        private void Require(char value) { if (!Take(value)) throw Syntax(); }

        internal bool Trivia()
        {
            var spaced = false;
            while (!End)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Space(Current)) { spaced = true; position++; }
                else if (Current == '/' && Peek(1) == '*')
                {
                    Comment();
                }
                else break;
            }
            return spaced;
        }

        private void Comment()
        {
            position += 2;
            while (!End && !(Current == '*' && Peek(1) == '/')) position++;
            if (End) throw Syntax();
            position += 2;
        }

        internal HtmlSelector List(bool nested)
        {
            if (++depth > 16) throw Unsupported("nesting depth exceeds 16");
            var result = new List<Complex>();
            Trivia();
            while (true)
            {
                result.Add(Chain());
                if (result.Count > 64) throw Unsupported("selector list exceeds 64 branches");
                Trivia();
                if (!Take(',')) break;
                Trivia();
            }
            if (nested) Require(')');
            depth--;
            return new(result);
        }

        private Complex Chain()
        {
            var parts = new List<(List<Func<int, MatchContext, bool>>, char)> { (Compound(), '\0') };
            while (true)
            {
                var spaced = Trivia();
                if (End || Current is ',' or ')') break;
                var relation = ' ';
                if (Current is '>' or '+' or '~') { relation = Current; position++; Trivia(); }
                else if (!spaced) throw Syntax();
                parts.Add((Compound(), relation));
                if (parts.Count > 64) throw Unsupported("combinator chain exceeds 64 compounds");
            }
            return new(parts);
        }

        private List<Func<int, MatchContext, bool>> Compound()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++compounds > 256) throw Unsupported("selector exceeds 256 compounds");
            var tests = new List<Func<int, MatchContext, bool>>();
            if (Current is '*' or '|' || StartsIdentifier())
            {
                var (anyNamespace, name, universal) = QualifiedName(attribute: false);
                tests.Add((id, context) =>
                {
                    var node = context.Owner.Tree.Nodes[id];
                    return (anyNamespace || string.IsNullOrEmpty(node.Namespace)) &&
                        (universal || NameEquals(node.Name!, name, node.Namespace == HtmlNamespaces.Html));
                });
            }
            while (!End)
            {
                if (Current == '/' && Peek(1) == '*') { Comment(); continue; }
                if (Take('#'))
                {
                    var value = Identifier();
                    tests.Add((id, context) => NameEquals(Attribute(context.Owner.Tree.Nodes[id], "id") ?? "", value, context.Quirks));
                }
                else if (Take('.'))
                {
                    var value = Identifier();
                    tests.Add((id, context) => WordContains(Attribute(context.Owner.Tree.Nodes[id], "class") ?? "", value, context.Quirks));
                }
                else if (Take('[')) tests.Add(AttributeTest());
                else if (Take(':')) tests.Add(Pseudo());
                else break;
            }
            if (tests.Count == 0) throw Syntax();
            return tests;
        }

        private (bool AnyNamespace, string Name, bool Universal) QualifiedName(bool attribute)
        {
            var anyNamespace = !attribute;
            bool universal;
            string name;
            if (Take('|'))
            {
                anyNamespace = false;
                universal = Take('*');
                name = universal ? "" : Identifier();
            }
            else
            {
                universal = Take('*');
                name = universal ? "" : Identifier();
                if (Current == '|' && Peek(1) != '=')
                {
                    position++;
                    var wildcardPrefix = universal;
                    universal = Take('*');
                    name = universal ? "" : Identifier();
                    if (!wildcardPrefix) throw Unsupported("named namespace prefixes");
                    anyNamespace = true;
                }
            }
            if (attribute && universal) throw Syntax();
            return (anyNamespace, name, universal);
        }

        private Func<int, MatchContext, bool> AttributeTest()
        {
            Trivia();
            var (anyNamespace, name, _) = QualifiedName(attribute: true);
            Trivia();
            var op = "";
            string? value = null;
            bool? insensitive = null;
            if (Current != ']')
            {
                if (Take('=')) op = "=";
                else if (Current is '~' or '|' or '^' or '$' or '*') { op = Current + "="; position++; Require('='); }
                else throw Syntax();
                Trivia();
                value = Current is '\'' or '"' ? String() : Identifier();
                Trivia();
                if (Current != ']')
                {
                    var flag = Identifier();
                    if (AsciiEquals(flag, "i")) insensitive = true;
                    else if (AsciiEquals(flag, "s")) insensitive = false;
                    else throw Syntax();
                    Trivia();
                }
            }
            Require(']');
            return (id, context) =>
            {
                var node = context.Owner.Tree.Nodes[id];
                var html = node.Namespace == HtmlNamespaces.Html;
                foreach (var attribute in node.Attributes)
                {
                    context.Step();
                    if ((!anyNamespace && !string.IsNullOrEmpty(attribute.Namespace)) || !NameEquals(attribute.Name, name, html)) continue;
                    if (value is null) return true;
                    var ignoreCase = insensitive ?? (html && CaseInsensitiveAttributeNames.Contains(AsciiLower(name)));
                    var actual = attribute.Value.Value;
                    // ASCII comparison is explicit; Unicode culture folding is never used.
                    var left = ignoreCase ? AsciiLower(actual) : actual;
                    var right = ignoreCase ? AsciiLower(value) : value;
                    if (op switch
                    {
                        "=" => left == right,
                        "~=" => WordContains(left, right, false),
                        "|=" => left == right || left.StartsWith(right + "-", StringComparison.Ordinal),
                        "^=" => right.Length != 0 && left.StartsWith(right, StringComparison.Ordinal),
                        "$=" => right.Length != 0 && left.EndsWith(right, StringComparison.Ordinal),
                        "*=" => right.Length != 0 && left.Contains(right, StringComparison.Ordinal),
                        _ => false
                    }) return true;
                }
                return false;
            };
        }

        private Func<int, MatchContext, bool> Pseudo()
        {
            var pseudoElement = Take(':');
            var name = AsciiLower(Identifier());
            if (pseudoElement || name is not ("scope" or "first-child" or "last-child" or "only-child" or "nth-child" or "not" or "is" or "where"))
            {
                if (Take('(')) SkipFunction();
                throw Unsupported(pseudoElement ? "pseudo-elements" : ":" + name);
            }
            if (name is "not" or "is" or "where")
            {
                Require('(');
                var inner = List(nested: true);
                return (id, context) => inner.Matches(id, context) != (name == "not");
            }
            if (name == "nth-child")
            {
                Require('(');
                var (a, b) = Nth();
                return (id, context) =>
                {
                    var index = context.Owner.Ordinals[id];
                    var delta = (long)index - b;
                    return a == 0 ? delta == 0 : delta % a == 0 && delta / a >= 0;
                };
            }
            if (Current == '(') throw Syntax();
            return (id, context) => name switch
            {
                "scope" => id == context.Scope,
                "first-child" => context.Owner.Ordinals[id] == 1,
                "last-child" => context.Owner.Next[id] < 0,
                "only-child" => context.Owner.Previous[id] < 0 && context.Owner.Next[id] < 0,
                _ => false
            };
        }

        private (long A, long B) Nth()
        {
            Trivia();
            long a = 0, b;
            var signed = Current is '+' or '-';
            var sign = Take('-') ? -1 : Take('+') ? 1 : 1;
            var digits = char.IsAsciiDigit(Current);
            var number = digits ? Digits() : 1;
            if (StartsIdentifier())
            {
                var word = Identifier();
                if (!signed && !digits && AsciiEquals(word, "odd")) { a = 2; b = 1; }
                else if (!signed && !digits && AsciiEquals(word, "even")) { a = 2; b = 0; }
                else if (word.Length > 0 && Lower(word[0]) == 'n')
                {
                    a = sign * number;
                    b = 0;
                    if (word.Length > 1)
                    {
                        if (word[1] != '-') throw Syntax();
                        if (word.Length == 2)
                        {
                            Trivia();
                            if (!char.IsAsciiDigit(Current)) throw Syntax();
                            b = -Digits();
                        }
                        else
                        {
                            var suffix = word.AsSpan(2);
                            if (suffix.IsEmpty || suffix.ContainsAnyExceptInRange('0', '9')) throw Syntax();
                            if (!int.TryParse(suffix, CultureInfo.InvariantCulture, out var offset)) throw Unsupported("An+B coefficient exceeds Int32 range");
                            b = -offset;
                        }
                    }
                    else
                    {
                        Trivia();
                        if (Current is '+' or '-')
                        {
                            var offsetSign = Take('-') ? -1 : Take('+') ? 1 : 1;
                            Trivia();
                            if (!char.IsAsciiDigit(Current)) throw Syntax();
                            b = offsetSign * Digits();
                        }
                    }
                }
                else throw Syntax();
            }
            else { if (!digits) throw Syntax(); b = sign * number; }
            Trivia();
            if (StartsIdentifier())
            {
                var extension = Identifier();
                if (AsciiEquals(extension, "of")) { SkipFunction(); throw Unsupported(":nth-child(... of selector)"); }
                throw Syntax();
            }
            Require(')');
            return (a, b);
        }

        private long Digits()
        {
            long value = 0;
            while (char.IsAsciiDigit(Current))
            {
                value = value * 10 + Current - '0';
                if (value > int.MaxValue) throw Unsupported("An+B coefficient exceeds Int32 range");
                position++;
            }
            return value;
        }

        private void SkipFunction()
        {
            var closings = new Stack<char>();
            closings.Push(')');
            while (!End)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Current == '/' && Peek(1) == '*') { Comment(); continue; }
                if (Current is '\'' or '"') { _ = String(); continue; }
                if (Current == '\\') { _ = Escape(); continue; }
                if (Current is '(' or '[') { closings.Push(Current == '(' ? ')' : ']'); position++; }
                else if (Current is ')' or ']')
                {
                    if (Current != closings.Pop()) throw Syntax();
                    position++;
                    if (closings.Count == 0) return;
                }
                else position++;
            }
            throw Syntax();
        }

        private bool StartsIdentifier()
        {
            if (Current == '-') return Peek(1) == '-' || NameStartAt(position + 1) || ValidEscape(position + 1);
            return NameStartAt(position) || ValidEscape(position);
        }

        private static bool NameStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' || c >= '\u0080' && !char.IsSurrogate(c);
        private bool NameStartAt(int at) => at < source.Length && (NameStart(source[at]) ||
            char.IsHighSurrogate(source[at]) && at + 1 < source.Length && char.IsLowSurrogate(source[at + 1]));
        private bool ValidEscape(int at) => at + 1 < source.Length && source[at] == '\\' && source[at + 1] != '\n';

        private string Identifier()
        {
            if (!StartsIdentifier() && !(char.IsHighSurrogate(Current) && char.IsLowSurrogate(Peek(1)))) throw Syntax();
            var value = new StringBuilder();
            while (!End)
            {
                if (Current == '\\') value.Append(Escape());
                else if (char.IsHighSurrogate(Current) && char.IsLowSurrogate(Peek(1))) { value.Append(Current).Append(Peek(1)); position += 2; }
                else if (NameStart(Current) || char.IsAsciiDigit(Current) || Current == '-') value.Append(source[position++]);
                else break;
            }
            return value.ToString();
        }

        private string String()
        {
            var quote = source[position++];
            var value = new StringBuilder();
            while (!End && Current != quote)
            {
                if (Current == '\n') throw Syntax();
                if (Current == '\\' && Peek(1) == '\n') { position += 2; continue; }
                if (Current == '\\') value.Append(Escape());
                else value.Append(source[position++]);
            }
            Require(quote);
            return value.ToString();
        }

        private string Escape()
        {
            if (!ValidEscape(position)) throw Syntax();
            position++;
            var start = position;
            while (position - start < 6 && Uri.IsHexDigit(Current)) position++;
            if (position == start)
            {
                if (char.IsHighSurrogate(Current) && char.IsLowSurrogate(Peek(1)))
                {
                    var pair = source.Substring(position, 2); position += 2; return pair;
                }
                return source[position++].ToString();
            }
            var code = int.Parse(source.AsSpan(start, position - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (Space(Current)) position++;
            return code is 0 or > 0x10FFFF or >= 0xD800 and <= 0xDFFF ? "\uFFFD" : char.ConvertFromUtf32(code);
        }
    }

    private static string? Attribute(HtmlTreeNode node, string name) =>
        node.Attributes.FirstOrDefault(attribute => attribute.Namespace is null && attribute.Name == name)?.Value.Value;

    // HTML values whose selector matching is ASCII case insensitive; explicit i/s flags override this.
    private static readonly HashSet<string> CaseInsensitiveAttributeNames = new(StringComparer.Ordinal)
    {
        "accept", "accept-charset", "align", "alink", "axis", "bgcolor", "charset", "checked", "clear", "codetype", "color",
        "compact", "declare", "defer", "dir", "direction", "disabled", "enctype", "face", "frame",
        "hreflang", "http-equiv", "lang", "language", "link", "media", "method", "multiple", "nohref",
        "noresize", "noshade", "nowrap", "readonly", "rel", "rev", "rules", "scope", "scrolling",
        "selected", "shape", "target", "text", "type", "valign", "valuetype", "vlink"
    };
}
