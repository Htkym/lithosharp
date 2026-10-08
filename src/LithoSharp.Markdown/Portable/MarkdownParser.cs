using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace LithoSharp.Content.Compilation;

internal static class MdInlineFacts
{
    internal static MdTextProjection Text(IReadOnlyList<LithoInline> inlines, CancellationToken cancellationToken, bool includeImageLabels = true)
    {
        var pieces = new List<MdTextProjection>();
        foreach (var (node, exit) in LithoInline.Walk(inlines, includeImageLabels, cancellationToken))
        {
            if (exit) continue;
            var piece = node switch
            {
                LithoText text => node.Projection ?? MdSourceMapping.Unknown(text.Text),
                LithoCodeSpan code => node.Projection ?? MdSourceMapping.Unknown(code.Code),
                LithoAutolink link => node.Projection ?? MdSourceMapping.Unknown(link.Text),
                LithoMathInline math => node.Projection ?? MdSourceMapping.Unknown(math.Content),
                LithoLineBreak => node.Projection ?? MdSourceMapping.Unknown("\n"),
                _ => null,
            };
            if (piece is not null) pieces.Add(piece);
        }
        return MdSourceMapping.Join(pieces);
    }
}

internal static class MdParser
{
    internal static MdDocumentFacts Parse(string raw, string scopeId, string sourceId, string? sourceVersion,
        MdOptions options, CancellationToken cancellationToken)
    {
        if (raw is null) throw new ArgumentNullException(nameof(raw));
        ValidateId(scopeId, nameof(scopeId)); ValidateId(sourceId, nameof(sourceId));
        if (sourceVersion is not null && MdHash.InvalidUnicode(sourceVersion, cancellationToken) >= 0)
            throw new ArgumentException("SourceVersion must be well-formed Unicode.", nameof(sourceVersion));
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new FactsBuilder(raw, scopeId, sourceId, sourceVersion, options, cancellationToken);
        if (raw.Length > options.MaxInputUtf16)
            return builder.Failed(new MdDiagnostic("LMD002", MdDiagnosticSeverity.Error,
                "Input exceeds maxInputUtf16.", null, "input", "input"));
        var invalid = MdHash.InvalidUnicode(raw, cancellationToken);
        if (invalid >= 0)
            return builder.Failed(new MdDiagnostic("LMD001", MdDiagnosticSeverity.Error,
                "Input contains an unpaired surrogate.", new MdRawRange(invalid, 1), "input", "invalidUnicode"));
        return builder.Parse();
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || MdHash.InvalidUnicode(value, CancellationToken.None) >= 0)
            throw new ArgumentException("Identifiers must be non-empty, well-formed Unicode.", name);
    }

    private sealed class FactsBuilder(string raw, string scopeId, string sourceId, string? sourceVersion,
        MdOptions options, CancellationToken cancellationToken)
    {
        private readonly MdParseContext context = new(options, cancellationToken);
        private MdFrontMatter frontmatter = new(MdFrontMatterState.Unknown, null, null, null, null);
        private MdRawRange? body;
        private string? textHash;
        private MdParseStatus status = MdParseStatus.Complete;
        private readonly List<MdHeading> headings = new();
        private readonly List<MdSection> sections = new();
        private readonly List<MdLink> links = new();
        private readonly Dictionary<string, MdRawRange?> referenceTargets = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MdFence> fences = new();
        private readonly List<MdTextRegion> regions = new();
        private readonly List<MdDiagnostic> diagnostics = new();
        private readonly List<MdTextRegion> unprojected = new();
        private readonly HashSet<string> reasons = new(StringComparer.Ordinal);
        private readonly HashSet<string> anchors = new(StringComparer.Ordinal);
        private bool unknownMapping;
        private bool incompleteRaw;

        internal MdDocumentFacts Failed(MdDiagnostic diagnostic)
        {
            status = MdParseStatus.Failed; incompleteRaw = true; unknownMapping = true;
            diagnostics.Add(diagnostic); reasons.Add(diagnostic.Reason);
            return Finish();
        }

        internal MdDocumentFacts Parse()
        {
            textHash = MdHash.Compute(raw);
            try
            {
                var split = MdFrontMatterParser.Split(raw, context);
                frontmatter = split.Facts; body = split.Body;
                if (split.Diagnostic is not null) { context.Emit(); diagnostics.Add(split.Diagnostic); Partial(split.Diagnostic.Reason); }
                CheckYaml(frontmatter.Root);
                if (body.HasValue)
                {
                    var bodyText = raw.Substring(body.Value.Start, body.Value.Length);
                    var parsed = LithoBlockParser.ParseFactsBlocks(bodyText, context);
                    foreach (var definition in context.Definitions)
                    {
                        var shifted = ShiftLink(definition); links.Add(shifted);
                        var key = LithoBlockParser.NormalizeLabel(shifted.ReferenceLabel!, context);
                        if (!referenceTargets.ContainsKey(key)) referenceTargets.Add(key, shifted.TargetRawSpan);
                    }
                    foreach (var block in Walk(parsed.Blocks)) Project(block);
                    MakeSections();
                }
                else { incompleteRaw = true; context.Emit(); sections.Add(new MdSection("preamble", null, null, null, null)); }
                if (unknownMapping)
                {
                    context.Emit(); diagnostics.Add(new MdDiagnostic("LMD006", MdDiagnosticSeverity.Information,
                        "Some decoded text cannot be mapped to raw tokens.", null, "mapping", "decodedMapping"));
                    Partial("decodedMapping");
                }
            }
            catch (MdResourceLimit limit)
            {
                // Validated, already-published facts survive; incomplete collections remain explicit.
                Partial(limit.Reason); incompleteRaw = true;
                diagnostics.Add(new MdDiagnostic("LMD003", MdDiagnosticSeverity.Warning,
                    "Parsing stopped at a resource limit.", null, "resource", limit.Reason));
                if (body.HasValue)
                {
                    var retained = new HashSet<int>(links.Where(link => link.Kind == MdLinkKind.ReferenceDefinition).Select(link => link.RawSpan.Start));
                    foreach (var definition in context.Definitions)
                        if (retained.Add(definition.RawSpan.Start + body.Value.Start)) links.Add(ShiftLink(definition));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Finish();
        }

        private void Partial(string reason) { status = MdParseStatus.Partial; reasons.Add(reason); }
        private MdRawRange Range(SourceSpan span) => new(checked((body?.Start ?? 0) + span.Start), span.Length);
        private MdRawRange? Range(SourceSpan? span) => span.HasValue ? Range(span.Value) : null;

        private MdTextProjection PublishText(MdTextProjection source, bool shift = true)
        {
            var text = shift ? MdSourceMapping.Shift(source, body!.Value.Start) : source;
            var valid = new List<MdSourceSegment>();
            var decodedEnd = 0; var rawEnd = -1;
            foreach (var segment in text.SourceSegments)
            {
                context.Scan(segment.DecodedSpan.Length);
                if (segment.DecodedSpan.Start != decodedEnd || segment.DecodedSpan.Length == 0)
                    throw new InvalidOperationException("Decoded mapping must cover the projected string in order.");
                decodedEnd = segment.DecodedSpan.End;
                var converted = segment;
                if (segment.RawSpan.HasValue)
                {
                    var rawSpan = segment.RawSpan.Value;
                    if (rawSpan.End > raw.Length) throw new InvalidOperationException("Projected raw range is outside the source.");
                    if (rawSpan.Start < rawEnd)
                        converted = new MdSourceSegment(segment.DecodedSpan, null, MdSegmentKind.Unknown);
                    else rawEnd = rawSpan.End;
                    if (segment.Kind == MdSegmentKind.Linear && converted.Kind != MdSegmentKind.Unknown)
                    {
                        if (rawSpan.Length != segment.DecodedSpan.Length) throw new InvalidOperationException("Linear lengths differ.");
                        if (string.CompareOrdinal(raw, rawSpan.Start, text.Text!, segment.DecodedSpan.Start, rawSpan.Length) != 0)
                            converted = new MdSourceSegment(segment.DecodedSpan, rawSpan, MdSegmentKind.Atomic);
                    }
                }
                else if (segment.Kind != MdSegmentKind.Unknown) throw new InvalidOperationException("Known mapping has no raw range.");
                if (converted.Kind == MdSegmentKind.Unknown) unknownMapping = true;
                valid.Add(converted);
            }
            if (text.Text is not null && decodedEnd != text.Text.Length) throw new InvalidOperationException("Incomplete decoded mapping.");
            context.Emit(valid.Count);
            return new MdTextProjection(text.Text, valid);
        }

        private void CheckYaml(MdYamlNode? node)
        {
            if (node is null) return;
            var pending = new Stack<MdYamlNode>(); pending.Push(node);
            while (pending.Count != 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested(); var current = pending.Pop();
                if (current.Scalar.SourceSegments.Any(s => s.Kind == MdSegmentKind.Unknown)) unknownMapping = true;
                foreach (var child in current.Children) pending.Push(child);
            }
        }

        private IEnumerable<LithoBlock> Walk(IReadOnlyList<LithoBlock> nodes)
        {
            var pending = new Stack<LithoBlock>();
            for (var i = nodes.Count - 1; i >= 0; i--) pending.Push(nodes[i]);
            while (pending.Count != 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested(); var node = pending.Pop(); yield return node;
                var children = node switch
                {
                    LithoQuote quote => quote.Children,
                    LithoAdmonition admonition => admonition.Children,
                    LithoDirective directive => directive.Children,
                    LithoList list => list.Items.SelectMany(item => item.Children).ToArray(),
                    _ => null,
                };
                if (children is not null) for (var i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
            }
        }

        private void Project(LithoBlock block)
        {
            switch (block)
            {
                case LithoHeading heading:
                {
                    var text = PublishText(MdInlineFacts.Text(heading.Inlines, cancellationToken, includeImageLabels: false));
                    context.Scan((long)text.Text!.Length * 8);
                    var anchor = LithoSlug.Slugify(text.Text!); var basis = anchor; var suffix = 1;
                    while (!anchors.Add(anchor)) anchor = basis + "-" + (suffix++).ToString(CultureInfo.InvariantCulture);
                    MdRawRange? textSpan = null;
                    if (heading.RawContent is not null)
                    {
                        var content = heading.RawContent;
                        var from = content.Map.Global(0);
                        var to = content.Text.Length == 0 ? from : content.Map.Global(content.Text.Length - 1) + 1;
                        textSpan = Range(new SourceSpan(from, to - from));
                    }
                    context.Emit(); headings.Add(new MdHeading("heading-" + headings.Count.ToString(CultureInfo.InvariantCulture),
                        heading.Level, text, Range(heading.Span), textSpan, anchor));
                    AddInlineLinks(heading.Inlines);
                    break;
                }
                case LithoParagraph paragraph:
                    if (IsOpaque(paragraph.RawContent?.Text))
                        Opaque("rawHtmlOrMdx", Range(paragraph.Span));
                    else
                    {
                        var text = PublishText(MdInlineFacts.Text(paragraph.Inlines, cancellationToken));
                        context.Emit(); regions.Add(new MdTextRegion("paragraph", Range(paragraph.Span), text));
                        AddInlineLinks(paragraph.Inlines);
                    }
                    break;
                case LithoCode code when code.Fenced && code.FenceSyntax is not null:
                {
                    var fence = code.FenceSyntax; var text = PublishText(fence.Text);
                    context.Emit(); fences.Add(new MdFence(Range(code.Span), Range(fence.Opening), Range(fence.Info),
                        Range(fence.Content), Range(fence.Closing), fence.Closing.HasValue, fence.InfoText, text));
                    break;
                }
                case LithoCode code:
                {
                    var text = PublishText(MdSourceMapping.Unknown(code.Text));
                    context.Emit(); regions.Add(new MdTextRegion("indentedCode", Range(code.Span), text));
                    break;
                }
                case LithoMath math:
                {
                    var text = PublishText(MdSourceMapping.Unknown(math.Content));
                    context.Emit(); regions.Add(new MdTextRegion("blockMath", Range(math.Span), text));
                    break;
                }
                case LithoTable table:
                    foreach (var row in table.Rows) foreach (var cell in row)
                    {
                        var text = PublishText(MdInlineFacts.Text(cell, cancellationToken));
                        context.Emit(); regions.Add(new MdTextRegion("tableCell", Range(table.Span), text));
                        AddInlineLinks(cell);
                    }
                    break;
            }
        }

        private bool IsOpaque(string? text)
        {
            if (text is null) return false;
            context.Scan(text.Length);
            if (text.TrimStart().StartsWith("import ", StringComparison.Ordinal) || text.TrimStart().StartsWith("export ", StringComparison.Ordinal)) return true;
            for (var i = 0; i + 1 < text.Length; i++)
                if (text[i] == '<' && (text[i + 1] is '/' or '!' || char.IsLetter(text[i + 1])))
                {
                    context.Scan((long)(text.Length - i - 1) * 3);
                    var end = text.IndexOf('>', i + 1);
                    if (end >= 0 && text.Substring(i + 1, end - i - 1).IndexOf(':') < 0
                        && text.Substring(i + 1, end - i - 1).IndexOf('@') < 0) return true;
                }
            return false;
        }
        private void Opaque(string kind, MdRawRange span)
        {
            context.Emit(); var region = new MdTextRegion(kind, span, MdSourceMapping.Unknown(null));
            regions.Add(region); unprojected.Add(region); unknownMapping = true; Partial(kind);
        }

        private MdLink ShiftLink(MdLink link) => new(link.Kind, link.Resolution, link.Image,
            new MdRawRange(link.RawSpan.Start + body!.Value.Start, link.RawSpan.Length),
            MdSourceMapping.Shift(link.Label, body.Value.Start), link.Target, link.Title,
            link.TargetRawSpan.HasValue ? new MdRawRange(link.TargetRawSpan.Value.Start + body.Value.Start, link.TargetRawSpan.Value.Length) : null,
            link.ReferenceLabel);

        private void AddInlineLinks(IReadOnlyList<LithoInline> nodes)
        {
            foreach (var (node, exit) in LithoInline.Walk(nodes, cancellationToken: cancellationToken))
            {
                if (exit) continue;
                if (node.UnresolvedReference is not null) { links.Add(ShiftLink(node.UnresolvedReference)); continue; }
                if (node is LithoLink link)
                {
                    var syntax = link.LinkSyntax ?? throw new InvalidOperationException("Missing link syntax facts.");
                    var label = PublishText(MdInlineFacts.Text(link.Label, cancellationToken));
                    var targetSpan = Range(syntax.Target);
                    if (syntax.Kind == MdLinkKind.ReferenceUse)
                    {
                        var normalized = LithoBlockParser.NormalizeLabel(syntax.ReferenceLabel!, context);
                        referenceTargets.TryGetValue(normalized, out targetSpan);
                    }
                    context.Emit(); links.Add(new MdLink(syntax.Kind,
                        syntax.Kind == MdLinkKind.ReferenceUse ? MdLinkResolution.ResolvedReference : MdLinkResolution.Inline,
                        link.Image, Range(link.Span), label, link.Url, link.Title, targetSpan, syntax.ReferenceLabel));
                }
                else if (node is LithoAutolink autolink)
                {
                    var label = PublishText(node.Projection ?? MdSourceMapping.Unknown(autolink.Text));
                    var mapped = MdSourceMapping.Map(label, new MdRawRange(0, label.Text!.Length));
                    var target = mapped.RawFragments.Count == 1 ? mapped.RawFragments[0] : (MdRawRange?)null;
                    context.Emit(); links.Add(new MdLink(MdLinkKind.Autolink, MdLinkResolution.Inline, false,
                        Range(autolink.Span), label, autolink.Href, null, target, null));
                }
            }
        }

        private void MakeSections()
        {
            var source = body!.Value;
            context.Emit(); sections.Add(new MdSection("preamble", null, null,
                new MdRawRange(source.Start, (headings.Count == 0 ? source.End : headings[0].RawSpan.Start) - source.Start),
                new MdRawRange(source.Start, (headings.Count == 0 ? source.End : headings[0].RawSpan.Start) - source.Start)));
            var parents = new Stack<int>();
            var ends = new int[headings.Count];
            var unfinished = new Stack<int>();
            for (var i = 0; i < headings.Count; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                while (unfinished.Count != 0 && headings[unfinished.Peek()].RawLevel >= headings[i].RawLevel)
                    ends[unfinished.Pop()] = headings[i].RawSpan.Start;
                unfinished.Push(i);
            }
            while (unfinished.Count != 0) ends[unfinished.Pop()] = source.End;
            for (var i = 0; i < headings.Count; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested(); var heading = headings[i];
                while (parents.Count != 0 && headings[parents.Peek()].RawLevel >= heading.RawLevel) parents.Pop();
                var parent = parents.Count == 0 ? null : "section-" + parents.Peek().ToString(CultureInfo.InvariantCulture);
                var end = ends[i];
                var directEnd = i + 1 < headings.Count ? Math.Min(end, headings[i + 1].RawSpan.Start) : end;
                var directStart = heading.RawSpan.End;
                if (directStart < raw.Length && raw[directStart] == '\r') directStart++;
                if (directStart < raw.Length && raw[directStart] == '\n') directStart++;
                context.Emit(); sections.Add(new MdSection("section-" + i.ToString(CultureInfo.InvariantCulture), parent, heading.LocalKey,
                    new MdRawRange(directStart, Math.Max(0, directEnd - directStart)), new MdRawRange(heading.RawSpan.Start, end - heading.RawSpan.Start)));
                parents.Push(i);
            }
        }

        private MdDocumentFacts Finish()
        {
            var coverage = new MdCoverage(incompleteRaw ? MdCoverageState.Partial : MdCoverageState.Complete,
                status == MdParseStatus.Failed ? MdCoverageState.Unknown : unknownMapping || incompleteRaw ? MdCoverageState.Partial : MdCoverageState.Complete,
                unprojected, reasons.OrderBy(value => value, StringComparer.Ordinal).ToArray());
            return new MdDocumentFacts(raw, scopeId, sourceId, sourceVersion, MdParserVersion.Value, options.Hash, textHash,
                status, body, frontmatter, headings, sections, links.OrderBy(link => link.RawSpan.Start).ToArray(), fences, regions, diagnostics,
                new[] {
                    new MdCapability("markdownSyntax", MdFeatureState.Supported),
                    new MdCapability("frontmatterSyntax", MdFeatureState.Supported),
                    new MdCapability("decodedSourceMapping", MdFeatureState.Partial),
                    new MdCapability("rawHtmlMdxProjection", MdFeatureState.Unsupported),
                    new MdCapability("siteResolution", MdFeatureState.Unsupported),
                }, coverage);
        }
    }
}
