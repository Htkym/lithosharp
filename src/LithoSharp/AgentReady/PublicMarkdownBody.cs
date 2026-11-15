using System.Text;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using Syntamark.Compilation;

namespace LithoSharp.AgentReady;

internal sealed record PublicMarkdownBodyResult(PublicMarkdownStatus Status, string? Body, string? Reason)
{
    internal bool IsComplete => Status is PublicMarkdownStatus.Automatic or PublicMarkdownStatus.ExplicitReplacement;
}

// Origin is used only to resolve a public URL. Edits always target the current body snapshot,
// never a guessed range in an included document or the component's source file.
internal sealed record PublicMarkdownLink(string Target, bool IsImage, string SourceId, MdRawRange SourceSpan);

internal static class PublicMarkdownBody
{
    internal const string ImplementationVersion = "1";

    internal static PublicMarkdownBodyResult Convert(MarkdownCompilationResult compilation, MarkdownOptions options,
        DocumentSource? source, Func<PublicMarkdownLink, string?> resolveLink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolveLink);
        cancellationToken.ThrowIfCancellationRequested();
        var analysis = compilation.BodyAnalysis?.Analysis;
        if (analysis is null || !Complete(analysis, options))
            return Unavailable("The authoritative body analysis is unavailable or incomplete.");
        if (source?.FilePath is { Length: > 0 } expectedSource && expectedSource != analysis.SourceId)
            return Unavailable("The source identity does not match the body analysis.");
        if (compilation.Semantics?.Diagnostics.Any(item => item.Severity == SiteDiagnosticSeverity.Error) == true)
            return new(PublicMarkdownStatus.Failed, null, "The body has compilation errors.");
        if (compilation.PublicMarkdownFailures.Count != 0)
            return new(PublicMarkdownStatus.Failed, null, "A public Markdown component provider failed.");
        if (analysis.UnsupportedSyntax!.Count != 0)
            return Unsupported("The body contains unsupported syntax.");
        var original = new MdSourceSnapshot(analysis.SourceId, analysis.SourceVersion!, analysis.RawText);
        if (original.TextHash != analysis.TextHash)
            return Unavailable("The body text does not match its authoritative analysis.");
        MdSourceMap origins;
        if (source?.SourceMap is { } previous)
        {
            if (previous.Output.Text != original.Text)
                return Unavailable("The inclusion source map describes another body.");
            origins = previous.Output.Matches(original) ? previous : Identity(original, previous.Output).Compose([previous]);
        }
        else
            origins = Identity(original, new(original.SourceId, "original/" + original.Version, original.Text));

        var admonitions = Admonitions(analysis, cancellationToken);
        var structural = new List<MdSourceEdit>();
        var providers = new HashSet<MdRawRange>();
        var nodes = new Stack<MdSyntaxNode>();
        nodes.Push(analysis.Root!);
        while (nodes.TryPop(out var node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Kind == MdSyntaxKind.Unknown)
                return Unsupported("The body contains unknown syntax.");
            if (node.Kind is MdSyntaxKind.HtmlInline or MdSyntaxKind.HtmlBlock)
            {
                if (node.RawSpan is not { } span)
                    return Unavailable("An HTML source range is unavailable.");
                var raw = original.Text.Substring(span.Start, span.Length);
                if (!OnlyComments(raw))
                    return Unsupported("Raw HTML has no declared public Markdown alternative.");
                // Preserve line and token boundaries, without publishing comment text.
                Add(span, string.Create(raw.Length, raw, static (output, input) =>
                {
                    for (var i = 0; i < output.Length; i++)
                        output[i] = input[i] is '\r' or '\n' ? input[i] : ' ';
                }));
                continue;
            }
            if (node.Kind is MdSyntaxKind.Admonition or MdSyntaxKind.Directive)
            {
                if (node.Kind == MdSyntaxKind.Admonition && node.Directive is null && node.RawSpan is { } alertSpan
                    && admonitions.ContainsKey(alertSpan.Start))
                {
                    // The same parser recognizes GitHub alert quotes; their ordinary source form already
                    // preserves the kind and every body line. Continue visiting children for safe link edits.
                    for (var index = node.Children.Count - 1; index >= 0; index--)
                        nodes.Push(node.Children[index]);
                    continue;
                }
                if (node.RawSpan is not { } span || node.Directive is not { IsComplete: true } directive)
                    return Unavailable("A directive's complete source ranges are unavailable.");
                if (directive.Name == "component")
                {
                    if (!compilation.PublicMarkdownComponents.TryGetValue(span.Start, out var alternative) || alternative is null)
                        return Unsupported("A component has no complete public Markdown provider.");
                    Add(span, alternative);
                    providers.Add(span);
                    continue; // The explicit static component has no separately executed child body.
                }
                if (node.Kind != MdSyntaxKind.Admonition && directive.Name is not ("tabs" or "tab"))
                    return Unsupported("A directive has no declared public Markdown alternative.");
                var attributes = directive.Attributes;
                if (attributes.Any(item => item.Name != "title" && item.Name != "label"))
                    return Unsupported("A directive has attributes without a public Markdown representation.");
                var label = attributes.FirstOrDefault(item => item.Name is "title" or "label")?.Value?.Text;
                if (options.ProfileId == MarkdownOptions.LegacyProfile)
                {
                    if (node.Kind == MdSyntaxKind.Admonition && admonitions.TryGetValue(span.Start, out var legacy))
                        label = legacy.Title == legacy.Kind ? null : legacy.Title;
                    else if (!string.IsNullOrWhiteSpace(original.Text.Substring(directive.NameRawSpan.End,
                        directive.HeaderRawSpan.End - directive.NameRawSpan.End)))
                        return Unsupported("A legacy directive header has no complete public label representation.");
                }
                var heading = directive.Name == "tabs" ? string.Empty
                    : "**" + AgentReadyLlmsFormatting.Heading(directive.Name == "tab" ? label ?? "Tab" : directive.Name)
                        + (directive.Name == "tab" || string.IsNullOrEmpty(label) ? "" : ": " + AgentReadyLlmsFormatting.Heading(label)) + "**\n\n";
                Add(new(span.Start, directive.ContentRawSpan.Start - span.Start), heading);
                Add(new(directive.ContentRawSpan.End, span.End - directive.ContentRawSpan.End), string.Empty);
            }
            for (var index = node.Children.Count - 1; index >= 0; index--)
                nodes.Push(node.Children[index]);
        }

        if (CheckLiteralMarkup(analysis, original, structural, removeComments: true, cancellationToken) is { } literalFailure)
            return literalFailure;
        var current = original;
        if (structural.Count != 0)
        {
            IReadOnlyList<PreservedNode> preservedSyntax = [];
            if (providers.Count != 0)
            {
                var providerEdits = structural.Where(edit => providers.Contains(edit.Target.Span)).ToArray();
                var ordinaryEdits = structural.Where(edit => !providers.Contains(edit.Target.Span)).OrderBy(edit => edit.Target.Span.Start).ToArray();
                if (ordinaryEdits.Length == 0)
                    preservedSyntax = PreservedSyntax(analysis, providerEdits, cancellationToken);
                else
                {
                    // Removing comments or known wrappers intentionally merges literal nodes and
                    // removes line-break nodes. Establish that changed snapshot first, then demand
                    // that a provider preserve every fact outside its own replacement envelope.
                    if (!TryApply(original, origins, ordinaryEdits, out var ordinary, out _))
                        return Unavailable("The ordinary Markdown transformations overlap.");
                    var ordinaryAnalysis = ordinary.Text == original.Text ? analysis : MarkdownAnalysisAdapter.AnalyzeBody(ordinary.Text, ordinary.SourceId,
                        MarkdownAnalysisAdapter.BodyOptionsFor(options), MarkdownAnalysisAdapter.BodyPartsFor(options, true),
                        ordinary.Version, cancellationToken);
                    if (!Complete(ordinaryAnalysis, options))
                        return Unavailable("The ordinary transformed body's authoritative analysis is unavailable.");
                    var relocated = new List<MdSourceEdit>();
                    var ordinaryIndex = 0;
                    var delta = 0;
                    foreach (var edit in providerEdits.OrderBy(edit => edit.Target.Span.Start))
                    {
                        while (ordinaryIndex < ordinaryEdits.Length && ordinaryEdits[ordinaryIndex].Target.Span.End <= edit.Target.Span.Start)
                        {
                            var preceding = ordinaryEdits[ordinaryIndex++];
                            delta = checked(delta + preceding.Replacement.Length - preceding.Target.Span.Length);
                        }
                        relocated.Add(new(new(ordinary, new(checked(edit.Target.Span.Start + delta), edit.Target.Span.Length)),
                            edit.ExpectedText, edit.Replacement));
                    }
                    preservedSyntax = PreservedSyntax(ordinaryAnalysis, relocated, cancellationToken);
                }
            }
            var originalFootnotes = FootnoteMeanings(analysis, origins, cancellationToken);
            var originalReferences = analysis.Links!.Where(link => link.Kind == MdLinkKind.ReferenceUse)
                .Select(link => (Key: ReferenceOrigin(origins, link.RawSpan, cancellationToken), Link: link))
                .Where(item => item.Key is not null).GroupBy(item => item.Key!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Link, StringComparer.Ordinal);
            if (!TryApply(original, origins, structural, out current, out origins))
                return Unavailable("The structural edits overlap or no longer match the body.");
            analysis = current.Text == original.Text ? analysis : MarkdownAnalysisAdapter.AnalyzeBody(current.Text, current.SourceId,
                MarkdownAnalysisAdapter.BodyOptionsFor(options), MarkdownAnalysisAdapter.BodyPartsFor(options, true),
                current.Version, cancellationToken);
            if (!Complete(analysis, options) || analysis.TextHash != current.TextHash)
                return Unavailable("The transformed body's authoritative analysis is unavailable.");
            if (analysis.UnsupportedSyntax!.Count != 0 || analysis.Root!.DescendantsAndSelf(cancellationToken).Any(node =>
                node.Kind is MdSyntaxKind.Unknown or MdSyntaxKind.HtmlInline or MdSyntaxKind.HtmlBlock or MdSyntaxKind.Directive || node.Kind == MdSyntaxKind.Admonition && node.Directive is not null))
                return Unsupported("A supplied alternative is not complete ordinary public Markdown.");
            if (CheckLiteralMarkup(analysis, current, [], removeComments: false, cancellationToken) is { } alternativeFailure)
                return alternativeFailure;
            // A complete parse can still let a provider's unclosed fence or delimiter swallow
            // unchanged surrounding content. Require every untouched syntax fact, not just
            // surviving links, to retain its projected range and meaning after the edit.
            var transformedSyntax = analysis.Root!.DescendantsAndSelf(cancellationToken)
                .Where(node => node.RawSpan is { Length: > 0 })
                .Select(node => new PreservedNode(node.RawSpan!.Value, Meaning(node))).ToHashSet();
            if (preservedSyntax.Any(node => !transformedSyntax.Contains(node)))
                return Unsupported("A supplied alternative changes surrounding body structure or removes existing syntax.");
            foreach (var (key, meaning) in FootnoteMeanings(analysis, origins, cancellationToken))
                if (originalFootnotes.TryGetValue(key, out var before) && before != meaning)
                    return Unsupported("A supplied alternative changes another footnote's original meaning.");
            foreach (var link in analysis.Links!.Where(link => link.Kind == MdLinkKind.ReferenceUse))
                if (ReferenceOrigin(origins, link.RawSpan, cancellationToken) is { } key
                    && originalReferences.TryGetValue(key, out var before)
                    && (before.Resolution != link.Resolution || before.Target != link.Target))
                    return Unsupported("A supplied alternative changes another reference's original meaning.");
        }

        var edits = new Dictionary<MdRawRange, MdSourceEdit>();
        for (var index = 0; index < analysis.Links!.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var link = analysis.Links[index];
            // An unresolved reference is literal in the paired HTML parser. There is no destination
            // to rewrite; nested real links still have their own facts and are handled normally.
            if (link.Kind == MdLinkKind.ReferenceUse && link.Resolution == MdLinkResolution.UnresolvedReference
                && link.Target is null && link.TargetRawSpan is null)
                continue;
            if (link.Target is null || link.Resolution == MdLinkResolution.UnresolvedReference)
                return Unavailable("A link's destination evidence is inconsistent.");
            var range = link.TargetRawSpan ?? link.RawSpan;
            var mapped = origins.Map(range, cancellationToken);
            if (mapped.Origins.Count != 1 || mapped.Precision is MdMappingPrecision.Unknown or MdMappingPrecision.Partial)
                return Unavailable("A link's original base is unavailable or ambiguous.");
            var origin = mapped.Origins[0];
            var destination = resolveLink(new(link.Target, link.Image, origin.Source.SourceId, origin.Span));
            if (destination is null)
                return Unsupported("A link has no verified public route or asset destination.");
            if (string.Equals(destination, link.Target, StringComparison.Ordinal))
                continue;
            if (!MdLinkEdits.TryCreateDestinationEdit(current, analysis, index, destination,
                out var edit, out var failure, cancellationToken))
                return Unavailable($"A link destination cannot be edited safely ({failure}).");
            if (edits.TryGetValue(edit!.Target.Span, out var existing) && existing.Replacement != edit.Replacement)
                return Unsupported("Shared references request conflicting public destinations.");
            edits[edit.Target.Span] = edit;
        }
        if (!TryApply(current, origins, edits.Values.ToArray(), out var output, out _))
            return Unavailable("The destination edits overlap or no longer match the body.");
        return new(PublicMarkdownStatus.Automatic, output.Text, null);

        void Add(MdRawRange span, string replacement) => structural.Add(new(new(original, span),
            original.Text.Substring(span.Start, span.Length), replacement));
    }

    private static IReadOnlyDictionary<int, LithoAdmonition> Admonitions(MdAnalysisResult analysis, CancellationToken token)
    {
        var result = new Dictionary<int, LithoAdmonition>();
        var pending = new Stack<LithoBlock>((analysis.Blocks ?? []).Reverse());
        while (pending.TryPop(out var block))
        {
            token.ThrowIfCancellationRequested();
            if (block is LithoAdmonition admonition)
                result[admonition.Span.Start] = admonition;
            IEnumerable<LithoBlock> children = block switch
            {
                LithoAdmonition item => item.Children,
                LithoDirective item => item.Children,
                LithoQuote item => item.Children,
                LithoFootnoteDefinition item => item.Children,
                LithoList item => item.Items.SelectMany(child => child.Children),
                _ => [],
            };
            foreach (var child in children.Reverse())
                pending.Push(child);
        }
        return result;
    }

    private sealed record SyntaxMeaning(MdSyntaxKind Kind, string? Text, int? HeadingLevel, int? OrderedListStart,
        bool? IsTightList, bool? TaskChecked, string? CodeInfo, string? Target, string? Title,
        string? ReferenceLabel, string? FootnoteLabel, bool? FootnoteIsDuplicate);
    private sealed record PreservedNode(MdRawRange Span, SyntaxMeaning Meaning);

    private static SyntaxMeaning Meaning(MdSyntaxNode node) => new(node.Kind, node.Text, node.HeadingLevel,
        node.OrderedListStart, node.IsTightList, node.TaskChecked, node.CodeInfo, node.Target, node.Title,
        node.ReferenceLabel, node.FootnoteNormalizedLabel, node.FootnoteIsDuplicate);

    private static IReadOnlyList<PreservedNode> PreservedSyntax(MdAnalysisResult analysis,
        IReadOnlyList<MdSourceEdit> edits, CancellationToken token)
    {
        var ordered = edits.OrderBy(edit => edit.Target.Span.Start).ToArray();
        var deltas = new int[ordered.Length + 1];
        for (var index = 0; index < ordered.Length; index++)
            deltas[index + 1] = checked(deltas[index] + ordered[index].Replacement.Length - ordered[index].Target.Span.Length);
        var result = new List<PreservedNode>();
        foreach (var node in analysis.Root!.DescendantsAndSelf(token))
        {
            if (node.Kind == MdSyntaxKind.Document || node.RawSpan is not { Length: > 0 } span)
                continue;
            // Find the first edit not entirely before this span. Overlapping parents are
            // intentionally transformed, but their unaffected descendants are still checked.
            var low = 0;
            var high = ordered.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (ordered[middle].Target.Span.End <= span.Start)
                    low = middle + 1;
                else
                    high = middle;
            }
            if (low < ordered.Length && ordered[low].Target.Span.Start < span.End)
                continue;
            result.Add(new(new(checked(span.Start + deltas[low]), span.Length), Meaning(node)));
        }
        return result;
    }
    private sealed record FootnoteMeaning(string Label, bool Resolved, string? Binding);

    private static Dictionary<string, FootnoteMeaning> FootnoteMeanings(MdAnalysisResult analysis, MdSourceMap origins, CancellationToken token)
    {
        var facts = MdFootnotes.FromAnalysis(analysis, token);
        var definitions = facts.Definitions.Where(item => !item.IsDuplicate)
            .ToDictionary(item => item.NormalizedLabel, item => ReferenceOrigin(origins, item.LabelRawSpan, token), StringComparer.Ordinal);
        var meanings = new Dictionary<string, FootnoteMeaning>(StringComparer.Ordinal);
        foreach (var reference in facts.References)
        {
            token.ThrowIfCancellationRequested();
            if (ReferenceOrigin(origins, reference.RawSpan, token) is { } key)
                meanings[key] = new(reference.NormalizedLabel, reference.IsResolved,
                    reference.IsResolved ? definitions.GetValueOrDefault(reference.NormalizedLabel) : null);
        }
        return meanings;
    }

    private static string? ReferenceOrigin(MdSourceMap map, MdRawRange span, CancellationToken cancellationToken)
    {
        var mapped = map.Map(span, cancellationToken);
        if (mapped.Precision != MdMappingPrecision.Exact || mapped.Origins.Count != 1)
            return null;
        var origin = mapped.Origins[0];
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            origin.Source.SourceId,
            origin.Source.Version,
            origin.Source.TextHash,
            origin.Span.Start,
            origin.Span.Length
        });
    }

    private static bool Complete(MdAnalysisResult analysis, MarkdownOptions options) =>
        analysis.Status == MdParseStatus.Complete && analysis.ParserVersion == MdParserVersion.Value
        && analysis.ProfileId == options.ProfileId && analysis.OptionsHash == MarkdownAnalysisAdapter.BodyOptionsFor(options).Hash
        && analysis.SourceVersion == MarkdownAnalysisAdapter.AgentReadySourceVersion(analysis.RawText)
        && analysis.BodySpan is { Start: 0 } body && body.Length == analysis.RawText.Length
        && analysis.GetAvailability(MdAnalysisParts.Syntax) == MdAnalysisAvailability.Complete
        && analysis.GetAvailability(MdAnalysisParts.Links) == MdAnalysisAvailability.Complete
        && analysis.GetAvailability(MdAnalysisParts.UnsupportedSyntax) == MdAnalysisAvailability.Complete;

    // Legacy/docs deliberately expose raw HTML as text. Use their authoritative Text ranges
    // plus the shared parser's lexical recognizer, never a second profile parse or regex strip.
    private static PublicMarkdownBodyResult? CheckLiteralMarkup(MdAnalysisResult analysis, MdSourceSnapshot source,
        List<MdSourceEdit> edits, bool removeComments, CancellationToken cancellationToken)
    {
        var candidates = new SortedSet<int>();
        var opaque = new List<MdRawRange>();
        var literalRanges = new List<MdRawRange>();
        var unknownOpaque = false;
        foreach (var node in analysis.Root!.DescendantsAndSelf(cancellationToken))
        {
            if (node.Kind is MdSyntaxKind.FencedCodeBlock or MdSyntaxKind.IndentedCodeBlock or MdSyntaxKind.CodeSpan
                or MdSyntaxKind.MathBlock or MdSyntaxKind.MathInline or MdSyntaxKind.Autolink)
            {
                if (node.RawSpan is { } range)
                    opaque.Add(range);
                else
                    unknownOpaque = true;
            }
            if (node.Kind is MdSyntaxKind.Text or MdSyntaxKind.Emphasis or MdSyntaxKind.StrongEmphasis or MdSyntaxKind.Strikethrough
                && node.RawSpan is { } literalRange)
                literalRanges.Add(literalRange);
            if (node.Kind != MdSyntaxKind.Text || node.Text?.Contains('<') != true)
                continue;
            if (node.RawSpan is not { } span)
                return Unavailable("A literal markup source range is unavailable.");
            for (var at = span.Start; at < span.End; at++)
                if (source.Text[at] == '<')
                    candidates.Add(at);
        }
        if (candidates.Count == 0)
            return null;
        if (unknownOpaque)
            return Unavailable("An opaque source range is unavailable for literal markup checking.");
        foreach (var link in analysis.Links!)
            if (link.TargetRawSpan is { } span)
                opaque.Add(span);
        var blocked = new List<MdRawRange>();
        foreach (var span in opaque.Concat(edits.Select(edit => edit.Target.Span)).OrderBy(span => span.Start))
        {
            if (blocked.Count != 0 && span.Start <= blocked[^1].End)
                blocked[^1] = new(blocked[^1].Start, Math.Max(span.End, blocked[^1].End) - blocked[^1].Start);
            else
                blocked.Add(span);
        }
        var allowed = new List<MdRawRange>();
        foreach (var span in literalRanges.OrderBy(span => span.Start))
        {
            if (allowed.Count != 0 && span.Start <= allowed[^1].End)
                allowed[^1] = new(allowed[^1].Start, Math.Max(span.End, allowed[^1].End) - allowed[^1].Start);
            else
                allowed.Add(span);
        }
        var allowedIndex = 0;
        var blockedIndex = 0;
        var commentEnd = 0;
        foreach (var at in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (at < commentEnd)
                continue;
            while (blockedIndex < blocked.Count && blocked[blockedIndex].End <= at)
                blockedIndex++;
            if (blockedIndex < blocked.Count && blocked[blockedIndex].Start <= at)
                continue;
            var slashes = 0;
            for (var previous = at - 1; previous >= 0 && source.Text[previous] == '\\'; previous--)
                slashes++;
            if ((slashes & 1) != 0)
                continue;
            if (!CommonMarkHtml.TryReadInline(source.Text, at, out var length))
            {
                if (at + 1 < source.Text.Length && (char.IsAsciiLetter(source.Text[at + 1]) || source.Text[at + 1] is '!' or '/' or '?' or '>' or '{'))
                    return Unsupported("Unrecognized literal markup has no complete public Markdown representation.");
                continue;
            }
            if (!source.Text.AsSpan(at).StartsWith("<!--", StringComparison.Ordinal))
                return Unsupported("Literal HTML or JSX has no declared public Markdown alternative.");
            if (!removeComments)
                return Unsupported("A supplied public alternative still contains a comment.");
            var comment = new MdRawRange(at, length);
            if (blockedIndex < blocked.Count && blocked[blockedIndex].Start < comment.End)
                return Unavailable("A literal comment intersects an opaque or transformed source range.");
            while (allowedIndex < allowed.Count && allowed[allowedIndex].End <= at)
                allowedIndex++;
            if (allowedIndex == allowed.Count || allowed[allowedIndex].Start > at || allowed[allowedIndex].End < comment.End
                || source.Text.AsSpan(at, length).IndexOfAny('\r', '\n') >= 0)
                return Unavailable("A literal comment crosses an unverified inline or structural boundary.");
            var raw = source.Text.Substring(at, length);
            var replacement = string.Create(raw.Length, raw, static (output, input) =>
            {
                for (var index = 0; index < output.Length; index++)
                    output[index] = input[index] is '\r' or '\n' ? input[index] : ' ';
            });
            edits.Add(new(new(source, comment), raw, replacement));
            commentEnd = comment.End;
        }
        return null;
    }

    private static bool OnlyComments(string raw)
    {
        var index = 0;
        while (index < raw.Length)
        {
            if (char.IsWhiteSpace(raw[index]))
            {
                index++;
                continue;
            }
            if (!raw.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
                return false;
            if (!CommonMarkHtml.TryReadInline(raw, index, out var length))
                return false;
            index += length;
        }
        return true;
    }

    private static MdSourceMap Identity(MdSourceSnapshot output, MdSourceSnapshot input) => new(output,
        output.Text.Length == 0 ? [] : [new(new(0, output.Text.Length), new(input, new(0, input.Text.Length)), MdSegmentKind.Linear)]);

    private static bool TryApply(MdSourceSnapshot input, MdSourceMap upstream, IReadOnlyList<MdSourceEdit> edits,
        out MdSourceSnapshot output, out MdSourceMap map)
    {
        output = input;
        map = upstream;
        if (edits.Count == 0)
            return true;
        var ordered = edits.OrderBy(edit => edit.Target.Span.Start).ThenBy(edit => edit.Target.Span.End).ToArray();
        var text = new StringBuilder(input.Text.Length);
        var segments = new List<MdSourceMapSegment>();
        var cursor = 0;
        foreach (var edit in ordered)
        {
            if (!edit.IsApplicable(input) || edit.Target.Span.Start < cursor)
                return false;
            AppendOriginal(cursor, edit.Target.Span.Start - cursor);
            if (edit.Replacement.Length != 0)
            {
                segments.Add(new(new(text.Length, edit.Replacement.Length), edit.Target, MdSegmentKind.Atomic));
                text.Append(edit.Replacement);
            }
            cursor = edit.Target.Span.End;
        }
        AppendOriginal(cursor, input.Text.Length - cursor);
        var body = text.ToString();
        if (body == input.Text)
            return true;
        output = new(input.SourceId, MarkdownAnalysisAdapter.AgentReadySourceVersion(body), body);
        map = new MdSourceMap(output, segments).Compose([upstream]);
        return true;

        void AppendOriginal(int start, int length)
        {
            if (length == 0)
                return;
            segments.Add(new(new(text.Length, length), new(input, new(start, length)), MdSegmentKind.Linear));
            text.Append(input.Text, start, length);
        }
    }

    private static PublicMarkdownBodyResult Unsupported(string reason) => new(PublicMarkdownStatus.Unsupported, null, reason);
    private static PublicMarkdownBodyResult Unavailable(string reason) => new(PublicMarkdownStatus.Unavailable, null, reason);
}
