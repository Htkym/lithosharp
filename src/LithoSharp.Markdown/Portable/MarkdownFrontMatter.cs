using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace LithoSharp.Content.Compilation;

internal static class MdFrontMatterParser
{
    internal static (MdFrontMatter Facts, MdRawRange? Body, MdDiagnostic? Diagnostic) Split(string raw, MdParseContext context)
    {
        var first = raw.Length != 0 && raw[0] == '\uFEFF' ? 1 : 0;
        var openingEnd = LineEnd(raw, first, context);
        if (raw.Substring(first, openingEnd - first) != "---")
            return (new MdFrontMatter(MdFrontMatterState.Absent, null, null, null, null), new MdRawRange(first, raw.Length - first), null);
        var yamlStart = AfterBreak(raw, openingEnd);
        var closingStart = yamlStart;
        while (closingStart < raw.Length)
        {
            var end = LineEnd(raw, closingStart, context);
            if (raw.Substring(closingStart, end - closingStart) == "---")
            {
                var bodyStart = AfterBreak(raw, end);
                var opening = new MdRawRange(first, 3); var yaml = new MdRawRange(yamlStart, closingStart - yamlStart);
                var closing = new MdRawRange(closingStart, 3); var body = new MdRawRange(bodyStart, raw.Length - bodyStart);
                var yamlText = raw.Substring(yaml.Start, yaml.Length);
                if (context.Inspect(yamlText).Trim().Length == 0)
                    return (new MdFrontMatter(MdFrontMatterState.Empty, opening, yaml, closing, null), body, null);
                try
                {
                    var root = new YamlFactsReader(yamlText, yamlStart, context).Parse();
                    return (new MdFrontMatter(MdFrontMatterState.Parsed, opening, yaml, closing, root), body, null);
                }
                catch (YamlFactsError error)
                {
                    return (new MdFrontMatter(MdFrontMatterState.Invalid, opening, yaml, closing, null), body,
                        Diagnostic("LMD005", error.Message, error.Span, error.Reason));
                }
                catch (YamlException error)
                {
                    var location = MarkSpan(error.Start, error.End, yamlText.Length, yamlStart);
                    return (new MdFrontMatter(MdFrontMatterState.Invalid, opening, yaml, closing, null), body,
                        Diagnostic("LMD005", error.Message, location, "syntax"));
                }
            }
            closingStart = AfterBreak(raw, end);
        }
        return (new MdFrontMatter(MdFrontMatterState.Unterminated, new MdRawRange(first, 3),
            new MdRawRange(yamlStart, raw.Length - yamlStart), null, null), null,
            Diagnostic("LMD004", "Frontmatter has no closing delimiter.", new MdRawRange(first, 3), "unterminated"));
    }

    private static MdDiagnostic Diagnostic(string id, string message, MdRawRange? span, string reason) =>
        new(id, MdDiagnosticSeverity.Warning, message, span, "frontmatter", reason);

    private static int LineEnd(string raw, int from, MdParseContext context)
    {
        while (from < raw.Length)
        {
            context.Scan();
            if (raw[from] is '\r' or '\n') break;
            from++;
        }
        return from;
    }
    private static int AfterBreak(string raw, int end) => end >= raw.Length ? end
        : end + (raw[end] == '\r' && end + 1 < raw.Length && raw[end + 1] == '\n' ? 2 : 1);

    // YamlDotNet 18.1.0's Mark boundaries are checked by the dedicated UTF-16 fixture.
    // An out-of-range event mark is an invariant failure, never silently clamped.
    private static MdRawRange MarkSpan(Mark start, Mark end, int length, int offset)
    {
        var from = checked((int)start.Index); var to = checked((int)end.Index);
        if (from < 0 || to < from || to > length) throw new InvalidOperationException("YAML mark is outside its input.");
        return new MdRawRange(checked(offset + from), to - from);
    }

    private sealed class YamlFactsError(string reason, string message, MdRawRange? span) : Exception(message)
    {
        internal string Reason { get; } = reason;
        internal MdRawRange? Span { get; } = span;
    }

    private sealed class YamlFactsReader(string yaml, int offset, MdParseContext context)
    {
        private readonly IParser parser = new Parser(new StringReader(yaml));
        private ParsingEvent Current => parser.Current ?? throw new InvalidOperationException("Missing YAML event.");
        private void Next() { context.CancellationToken.ThrowIfCancellationRequested(); parser.MoveNext(); }
        private MdRawRange Span(ParsingEvent e) => MarkSpan(e.Start, e.End, yaml.Length, offset);
        private YamlFactsError Fail(string reason, string message) => new(reason, message, Span(Current));

        internal MdYamlNode Parse()
        {
            Next();
            if (Current is not StreamStart) throw Fail("syntax", "Missing YAML stream start.");
            Next();
            if (Current is not DocumentStart) throw Fail("syntax", "Missing YAML document start.");
            Next();
            if (Current is not MappingStart) throw Fail("invalidRoot", "YAML root must be a mapping.");
            var root = Node(1);
            if (Current is not DocumentEnd) throw Fail("syntax", "Unexpected trailing YAML content.");
            Next();
            if (Current is not StreamEnd) throw Fail("multipleDocument", "YAML must contain exactly one document.");
            return root;
        }

        private MdYamlNode Node(int depth)
        {
            context.Depth(depth);
            var current = Current;
            if (current is AnchorAlias || current is NodeEvent node && !node.Anchor.IsEmpty)
                throw Fail("alias", "YAML anchors and aliases are not allowed.");
            if (current is Scalar scalar)
            {
                var span = Span(scalar);
                var value = scalar.Value;
                var raw = yaml.Substring(span.Start - offset, span.Length);
                context.Scan(raw.Length);
                MdTextProjection projection;
                if (raw == value)
                    projection = new MdTextProjection(value, value.Length == 0 ? MdFreeze.Empty<MdSourceSegment>()
                        : new[] { new MdSourceSegment(new MdRawRange(0, value.Length), span, MdSegmentKind.Linear) });
                else if (raw.Length >= 2 && raw.Substring(1, raw.Length - 2) == value
                    && scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted)
                    projection = new MdTextProjection(value, value.Length == 0 ? MdFreeze.Empty<MdSourceSegment>()
                        : new[] { new MdSourceSegment(new MdRawRange(0, value.Length), new MdRawRange(span.Start + 1, value.Length), MdSegmentKind.Linear) });
                else if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted)
                    projection = new MdTextProjection(value, value.Length == 0 ? MdFreeze.Empty<MdSourceSegment>()
                        : new[] { new MdSourceSegment(new MdRawRange(0, value.Length), span, MdSegmentKind.Atomic) });
                else projection = MdSourceMapping.Unknown(value);
                context.Emit(1 + projection.SourceSegments.Count);
                Next();
                return new MdYamlNode(MdYamlKind.Scalar, span, projection, MdFreeze.Empty<MdYamlNode>());
            }
            if (current is SequenceStart)
            {
                context.Emit();
                Next(); var children = new List<MdYamlNode>();
                while (Current is not SequenceEnd) children.Add(Node(depth + 1));
                var span = MarkSpan(current.Start, Current.End, yaml.Length, offset);
                Next();
                return new MdYamlNode(MdYamlKind.Sequence, span, MdSourceMapping.Unknown(null), children);
            }
            if (current is MappingStart)
            {
                context.Emit();
                Next(); var children = new List<MdYamlNode>(); var keys = new HashSet<string>(StringComparer.Ordinal);
                while (Current is not MappingEnd)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (Current is AnchorAlias || Current is NodeEvent keyNode && !keyNode.Anchor.IsEmpty)
                        throw Fail("alias", "YAML anchors and aliases are not allowed.");
                    if (Current is not Scalar key || IsNull(key)) throw Fail("invalidKey", "Mapping keys must be non-null scalars.");
                    if (!keys.Add(key.Value.Normalize(NormalizationForm.FormC))) throw Fail("duplicateKey", "A mapping key is duplicated.");
                    var keyFacts = Node(depth + 1); var valueFacts = Node(depth + 1);
                    children.Add(keyFacts); children.Add(valueFacts); // alternating key/value facts, without typed binding
                }
                var span = MarkSpan(current.Start, Current.End, yaml.Length, offset);
                Next();
                return new MdYamlNode(MdYamlKind.Mapping, span, MdSourceMapping.Unknown(null), children);
            }
            throw Fail("syntax", "Unsupported YAML event.");
        }

        private static bool IsNull(Scalar value) => value.Style is ScalarStyle.Any or ScalarStyle.Plain
            && (value.Value.Length == 0 || value.Value == "~" || string.Equals(value.Value, "null", StringComparison.OrdinalIgnoreCase));
    }
}
