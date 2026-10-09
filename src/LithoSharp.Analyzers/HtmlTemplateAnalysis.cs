using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

// Symbolic HTML: consume literal characters and typed holes separately. Never substitute holes
// into a document parser, or execute ToString/ToHtmlString to discover their contents.
internal sealed class HtmlTemplateAnalysis
{
    private static DiagnosticDescriptor Rule(string id, string title, string message, DiagnosticSeverity severity) =>
        new(id, title, message, "LithoSharp", severity, isEnabledByDefault: true,
            helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#" + id.ToLowerInvariant());
    private static readonly DiagnosticDescriptor Raw = Rule("LSA1101", "Unverified raw HTML",
        "Raw HTML contents cannot be statically verified ({0})", DiagnosticSeverity.Info);
    private static readonly DiagnosticDescriptor Fragment = Rule("LSA1102", "HTML fragment in a quoted attribute",
        "An HTML fragment is interpolated into a quoted attribute value", DiagnosticSeverity.Error);
    private static readonly DiagnosticDescriptor Position = Rule("LSA1103", "Interpolation in a forbidden HTML position",
        "Interpolation is in the {0} HTML position", DiagnosticSeverity.Error);
    private static readonly DiagnosticDescriptor Encoding = Rule("LSA1104", "HTML encoding in a script or style context",
        "HTML encoding does not establish safety in the {0} context", DiagnosticSeverity.Warning);
    internal static ImmutableArray<DiagnosticDescriptor> Rules => ImmutableArray.Create(Raw, Fragment, Position, Encoding);
    internal const int MaxTemplateLength = 65536;
    internal const int MaxHoles = 256;
    private readonly IMethodSymbol raw;
    private readonly IMethodSymbol encode;
    private readonly INamedTypeSymbol text;
    private readonly INamedTypeSymbol attribute;

    private HtmlTemplateAnalysis(IMethodSymbol raw, IMethodSymbol encode, INamedTypeSymbol text, INamedTypeSymbol attribute)
    { this.raw = raw; this.encode = encode; this.text = text; this.attribute = attribute; }

    internal static HtmlTemplateAnalysis? Create(Compilation compilation)
    {
        INamedTypeSymbol? Core(string name)
        {
            var type = compilation.GetTypeByMetadataName("LithoSharp." + name);
            return type?.ContainingAssembly.Identity.Name == "LithoSharp" && !type.Locations.Any(l => l.IsInSource) ? type : null;
        }
        var html = Core("Html"); var content = Core("IHtmlContent");
        var text = Core("HtmlText"); var attribute = Core("HtmlAttributeValue");
        if (html is null || content is null || text is null || attribute is null) return null;
        IMethodSymbol? Method(string name) => html.GetMembers(name).OfType<IMethodSymbol>().SingleOrDefault(m =>
            m.IsStatic && m.Arity == 0 && m.DeclaredAccessibility == Accessibility.Public && m.Parameters.Length == 1
            && m.Parameters[0].RefKind == RefKind.None && m.Parameters[0].Type.SpecialType == SpecialType.System_String);
        var raw = Method("UnsafeRaw"); var encode = Method("Encode");
        return raw is not null && encode?.ReturnType.SpecialType == SpecialType.System_String
            && SymbolEqualityComparer.Default.Equals(raw.ReturnType, content)
            ? new HtmlTemplateAnalysis(raw, encode, text, attribute) : null;
    }

    internal void Analyze(OperationBlockAnalysisContext context, IOperation operation, Func<IOperation, StaticValue> evaluate)
    {
        if (operation is not IInvocationOperation call || !Same(call.TargetMethod, raw)
            || call.Arguments.Length != 1 || StaticValueFlow.HasBindingError(call)) return;
        var argument = call.Arguments[0].Value;
        var value = evaluate(argument);
        void Report(DiagnosticDescriptor rule, Location location, string state, string reason, params object[] args) =>
            context.ReportDiagnostic(Diagnostic.Create(rule, location, ImmutableDictionary<string, string?>.Empty
                .Add("staticState", state).Add("reason", reason).Add("origin", "original-expression-utf16-span"), args));
        if (value.Values is null)
            Report(Raw, argument.Syntax.GetLocation(), value.Reason?.EndsWith("-budget", StringComparison.Ordinal) == true ? "Deferred" : "Unknown",
                value.Reason ?? "unverified-raw", value.Reason ?? "unverified-raw");
        var syntax = argument.Syntax;
        while (syntax is ParenthesizedExpressionSyntax parenthesized) syntax = parenthesized.Expression;
        if (syntax is not InterpolatedStringExpressionSyntax template || template.Span.Length > MaxTemplateLength
            || template.Contents.OfType<InterpolationSyntax>().Count() > MaxHoles) return;
        var model = context.Compilation.GetSemanticModel(syntax.SyntaxTree);
        var cursor = new HtmlContextCursor();
        var consumed = 0;
        void Feed(string contents)
        {
            if (contents.Length > MaxTemplateLength - consumed) { cursor.Invalidate(); return; }
            consumed += contents.Length;
            cursor.Feed(contents);
        }
        foreach (var part in template.Contents)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (part is InterpolatedStringTextSyntax literal)
            { Feed(literal.TextToken.ValueText); continue; }
            var hole = (InterpolationSyntax)part;
            var expression = model.GetOperation(hole.Expression, context.CancellationToken);
            if (expression is null || StaticValueFlow.HasBindingError(expression)) { cursor.Invalidate(); continue; }
            var kind = Classify(expression);
            if (hole.AlignmentClause is not null || hole.FormatClause is not null) kind = HoleKind.Unknown;
            var position = cursor.ForbiddenPosition;
            if (cursor.Known && position is not null)
                Report(Position, hole.Expression.GetLocation(), "Known", "forbidden-position", position);
            else if (cursor.Quoted && kind == HoleKind.Fragment)
                Report(Fragment, hole.Expression.GetLocation(), "Known", "fragment-in-quoted-attribute");
            else if (cursor.SpecialContext is { } special && kind == HoleKind.Encoded)
                Report(Encoding, hole.Expression.GetLocation(), "Known", "html-encoding-in-special-context", special);
            // Constant string holes have actual contents; all other raw holes can alter subsequent
            // HTML structure. The two sealed wrappers and exact Encode API preserve text/quotes,
            // but neither guarantees comment, name, unquoted, JavaScript or CSS contracts.
            var constant = expression.ConstantValue;
            if (hole.AlignmentClause is null && hole.FormatClause is null && expression.Type?.SpecialType == SpecialType.System_String
                && constant.HasValue && constant.Value is string known) Feed(known);
            else if (kind != HoleKind.Encoded || !cursor.AcceptsEncoded) cursor.Invalidate();
        }
    }

    private static bool Same(IMethodSymbol method, IMethodSymbol expected) => SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, expected);
    private HoleKind Classify(IOperation operation)
    {
        while (operation is IConversionOperation conversion && conversion.OperatorMethod is null) operation = conversion.Operand;
        if (operation is IInvocationOperation call)
        {
            if (Same(call.TargetMethod, encode)) return HoleKind.Encoded;
            if (Same(call.TargetMethod, raw)) return HoleKind.Fragment;
            if (call.Arguments.Length == 0 && call.TargetMethod.Name == "ToHtmlString"
                && call.TargetMethod.ContainingType.ToDisplayString() == "LithoSharp.IHtmlContent"
                && call.TargetMethod.ContainingAssembly.Identity.Name == "LithoSharp"
                && call.Instance is IInvocationOperation receiver && Same(receiver.TargetMethod, raw)) return HoleKind.Fragment;
            // Interface dispatch is safe only for the two sealed, metadata-defined wrappers.
            if (call.Arguments.Length == 0 && call.TargetMethod.Name is "ToHtmlString" or "ToString"
                && (SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, text)
                    || SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, attribute)))
                return HoleKind.Encoded;
        }
        return SymbolEqualityComparer.Default.Equals(operation.Type, text) || SymbolEqualityComparer.Default.Equals(operation.Type, attribute)
            ? HoleKind.Encoded : HoleKind.Unknown;
    }
    private enum HoleKind { Unknown, Encoded, Fragment }
}

// A bounded lexical context cursor, not a replacement HTML parser. Ambiguous/malformed/foreign
// declarations are deliberately Unknown. Raw-text closing tags and comment tails span literals.
internal sealed class HtmlContextCursor
{
    internal const int MaxNameLength = 128;
    private enum State { Text, Open, Tag, BeforeAttribute, Attribute, AfterAttribute, BeforeValue, Quoted, Unquoted, Slash, Comment, Raw, Unknown }
    private State state;
    private string tag = "", attribute = "", pending = "";
    private bool closing;
    private char quote;
    internal bool Known => state != State.Unknown;
    internal bool Quoted => state == State.Quoted;
    internal string? ForbiddenPosition => state switch
    {
        State.Open when pending.Length == 0 => "tag name",
        State.Tag => "tag name",
        State.BeforeAttribute or State.Attribute or State.AfterAttribute => "attribute name",
        State.BeforeValue or State.Unquoted => "unquoted attribute value",
        _ => null
    };
    internal string? SpecialContext => state == State.Raw ? tag : Quoted &&
        (attribute.StartsWith("on", StringComparison.Ordinal) && attribute.Length > 2 || attribute == "style") ? attribute : null;
    // Encoding excludes '<', but can complete or break a closing tag already begun
    // by literal text. Skipping such a hole would fabricate the following context.
    internal bool AcceptsEncoded => state is State.Text or State.Quoted || state == State.Raw && pending.Length == 0;
    internal void Invalidate() => state = State.Unknown;
    internal void Feed(string literal)
    {
        foreach (var character in literal)
        {
            if (!Known) return;
            var c = character >= 'A' && character <= 'Z' ? (char)(character + 32) : character;
            var space = c is ' ' or '\t' or '\r' or '\n' or '\f';
            switch (state)
            {
                case State.Text:
                    if (c == '<') { state = State.Open; pending = ""; tag = ""; closing = false; }
                    break;
                case State.Open:
                    if (pending.Length == 0 && c == '/' && !closing) { closing = true; break; }
                    if (pending.Length == 0 && c >= 'a' && c <= 'z') { tag = c.ToString(); state = State.Tag; break; }
                    pending += c;
                    if (pending == "!--") { state = State.Comment; pending = ""; }
                    else if (!"!--".StartsWith(pending, StringComparison.Ordinal)) Invalidate();
                    break;
                case State.Tag:
                    if (space) state = State.BeforeAttribute;
                    else if (c == '>') EndTag();
                    else if (c == '/') state = State.Slash;
                    else if (Name(c)) tag += c;
                    else Invalidate();
                    // Foreign namespaces and alternate HTML tokenizer modes are not certified.
                    if (tag == "svg" || tag == "math") Invalidate();
                    break;
                case State.BeforeAttribute:
                    if (space) break;
                    if (c == '>') EndTag();
                    else if (c == '/') state = State.Slash;
                    else if (!closing && Name(c)) { attribute = c.ToString(); state = State.Attribute; }
                    else Invalidate();
                    break;
                case State.Attribute:
                    if (space) state = State.AfterAttribute;
                    else if (c == '=') state = State.BeforeValue;
                    else if (c == '>') EndTag();
                    else if (c == '/') state = State.Slash;
                    else if (Name(c)) attribute += c;
                    else Invalidate();
                    break;
                case State.AfterAttribute:
                    if (space) break;
                    if (c == '=') state = State.BeforeValue;
                    else if (c == '>') EndTag();
                    else if (c == '/') state = State.Slash;
                    else if (Name(c)) { attribute = c.ToString(); state = State.Attribute; }
                    else Invalidate();
                    break;
                case State.BeforeValue:
                    if (space) break;
                    if (c is '\'' or '"') { quote = c; state = State.Quoted; }
                    else if (c is '<' or '>' or '=' or '`') Invalidate();
                    else state = State.Unquoted;
                    break;
                case State.Quoted:
                    if (c == quote) state = State.BeforeAttribute;
                    break;
                case State.Unquoted:
                    if (space) state = State.BeforeAttribute;
                    else if (c == '>') EndTag();
                    else if (c is '<' or '=' or '`' or '\'' or '"') Invalidate();
                    break;
                case State.Slash:
                    if (c == '>') EndTag(); else Invalidate();
                    break;
                case State.Comment:
                    pending = (pending + c);
                    if (pending.EndsWith("-->", StringComparison.Ordinal)) { state = State.Text; pending = ""; }
                    else if (pending.Length > 3) pending = pending.Substring(pending.Length - 3);
                    break;
                case State.Raw:
                    if (tag == "script" && pending == "<" && c == '!') { Invalidate(); break; }
                    var end = "</" + tag;
                    if (pending == end && (space || c is '>' or '/'))
                    { closing = true; state = c == '>' ? State.Text : c == '/' ? State.Slash : State.BeforeAttribute; pending = ""; break; }
                    pending += c;
                    if (!end.StartsWith(pending, StringComparison.Ordinal)) pending = c == '<' ? "<" : "";
                    break;
            }
            if (tag.Length > MaxNameLength || attribute.Length > MaxNameLength) Invalidate();
        }
    }
    private void EndTag()
    {
        state = !closing && (tag == "script" || tag == "style") ? State.Raw :
            !closing && (tag == "textarea" || tag == "title" || tag == "plaintext" || tag == "xmp" || tag == "iframe" || tag == "noembed" || tag == "noscript") ? State.Unknown : State.Text;
        pending = "";
    }
    private static bool Name(char c) => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c is '-' or '_' or ':';
}
