using System.Text.RegularExpressions;
using LithoSharp.Content.Compilation;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace LithoSharp.Tests;

/// <summary>
/// Reference-only Markdig AST analysis, moved out of the product in C13.
/// Builds the syntax tree and semantic model from one Markdig parse.
/// Holds no state and retains no text; the caller owns document lifetime.
/// </summary>
internal static class ReferenceDocumentAnalyzer
{
    /// <summary>Analyzes an already-parsed document.</summary>
    /// <param name="document">Parsed body document.</param>
    /// <param name="pipeline">Pipeline used for parsing (also renders HTML and plain text).</param>
    /// <param name="body">Markdown body the document was parsed from.</param>
    /// <param name="source">Source identity for position shifting.</param>
    public static (string Html, DocumentSyntax Syntax, DocumentSemantics Semantics) Analyze(
        MarkdownDocument document,
        MarkdownPipeline pipeline,
        string body,
        DocumentSource source)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(source);

        var html = document.ToHtml(pipeline);
        var plainText = RenderPlainText(document, pipeline);

        var blocks = new List<SyntaxBlock>();
        var headings = new List<DocumentHeading>();
        foreach (var block in document)
        {
            var span = ToFileSpan(block.Span, source.BodyStartOffset);
            switch (block)
            {
                case HeadingBlock heading:
                    blocks.Add(new SyntaxBlock(BlockKind.Heading, span, heading.Level));
                    headings.Add(new DocumentHeading(
                        RenderInlineText(heading.Inline, pipeline),
                        Markdig.Renderers.Html.HtmlAttributesExtensions.TryGetAttributes(heading)?.Id,
                        heading.Level,
                        heading.Level == 1 ? 2 : heading.Level,
                        span));
                    break;
                case ParagraphBlock:
                    blocks.Add(new SyntaxBlock(BlockKind.Paragraph, span, 0));
                    break;
                case Markdig.Extensions.Mathematics.MathBlock:
                    blocks.Add(new SyntaxBlock(BlockKind.Math, span, 0));
                    break;
                case FencedCodeBlock or CodeBlock:
                    blocks.Add(new SyntaxBlock(BlockKind.Code, span, 0));
                    break;
                case Markdig.Extensions.Alerts.AlertBlock alert:
                    blocks.Add(new SyntaxBlock(BlockKind.Admonition, span, 0));
                    CollectNestedHeadings(alert, pipeline, source, headings);
                    break;
                case Markdig.Extensions.CustomContainers.CustomContainer container:
                    blocks.Add(new SyntaxBlock(
                        IsAdmonitionContainer(container) ? BlockKind.Admonition : BlockKind.Directive, span, 0));
                    CollectNestedHeadings(container, pipeline, source, headings);
                    break;
                case QuoteBlock quote:
                    blocks.Add(new SyntaxBlock(BlockKind.Quote, span, 0));
                    CollectNestedHeadings(quote, pipeline, source, headings);
                    break;
                case ListBlock list:
                    blocks.Add(new SyntaxBlock(BlockKind.List, span, 0));
                    CollectNestedHeadings(list, pipeline, source, headings);
                    break;
                case Markdig.Extensions.Tables.Table table:
                    blocks.Add(new SyntaxBlock(BlockKind.Table, span, 0));
                    CollectNestedHeadings(table, pipeline, source, headings);
                    break;
                case ThematicBreakBlock:
                    blocks.Add(new SyntaxBlock(BlockKind.ThematicBreak, span, 0));
                    break;
                case LinkReferenceDefinitionGroup:
                case Markdig.Extensions.AutoIdentifiers.HeadingLinkReferenceDefinition:
                    // Reference definitions carry no rendered content.
                    break;
                default:
                    blocks.Add(new SyntaxBlock(BlockKind.Other, span, 0));
                    break;
            }
        }

        var links = new List<DocumentLink>();
        var assets = new List<DocumentAsset>();
        foreach (var link in document.Descendants().OfType<LinkInline>())
        {
            if (link.Span.IsEmpty)
            {
                continue;
            }

            var span = ToFileSpan(link.Span, source.BodyStartOffset);
            links.Add(new DocumentLink(
                Slice(body, link.Span),
                link.Url ?? string.Empty,
                link.Title,
                link.IsImage,
                span));
            if (link.IsImage && link.Url is not null)
            {
                assets.Add(new DocumentAsset(link.Url, span));
            }
        }

        foreach (var autolink in document.Descendants().OfType<AutolinkInline>())
        {
            if (autolink.Span.IsEmpty || autolink.Url is null)
            {
                continue;
            }

            var span = ToFileSpan(autolink.Span, source.BodyStartOffset);
            var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
            links.Add(new DocumentLink(Slice(body, autolink.Span), url, null, false, span));
        }

        links.Sort(static (left, right) => left.Span.Start.CompareTo(right.Span.Start));

        // One parse feeds HTML, plain text, blocks, headings, links, and assets.
        return (
            html,
            new DocumentSyntax(blocks),
            new DocumentSemantics(
                source,
                DocumentSemantics.TitleOf(headings),
                plainText,
                headings,
                links,
                assets,
                Components: [],
                Diagnostics: []));
    }

    private static bool IsAdmonitionContainer(
        Markdig.Extensions.CustomContainers.CustomContainer container)
    {
        var info = container.Info ?? string.Empty;
        var end = 0;
        while (end < info.Length && !char.IsWhiteSpace(info[end]))
        {
            end++;
        }

        return LithoDirectives.IsAdmonitionName(info[..end]);
    }

    private static void CollectNestedHeadings(
        ContainerBlock container,
        MarkdownPipeline pipeline,
        DocumentSource source,
        List<DocumentHeading> headings)
    {
        foreach (var child in container)
        {
            if (child is HeadingBlock heading)
            {
                headings.Add(new DocumentHeading(
                    RenderInlineText(heading.Inline, pipeline),
                    Markdig.Renderers.Html.HtmlAttributesExtensions.TryGetAttributes(heading)?.Id,
                    heading.Level,
                    heading.Level == 1 ? 2 : heading.Level,
                    ToFileSpan(heading.Span, source.BodyStartOffset)));
            }
            else if (child is ContainerBlock nested)
            {
                CollectNestedHeadings(nested, pipeline, source, headings);
            }
        }
    }

    private static LithoSharp.Content.Compilation.SourceSpan ToFileSpan(Markdig.Syntax.SourceSpan span, int bodyStartOffset) =>
        span.IsEmpty
            ? LithoSharp.Content.Compilation.SourceSpan.Empty
            : LithoSharp.Content.Compilation.SourceSpan.FromInclusiveStartEnd(span.Start, span.End).Shift(bodyStartOffset);

    private static string Slice(string body, Markdig.Syntax.SourceSpan span)
    {
        if (span.IsEmpty || span.Start >= body.Length)
        {
            return string.Empty;
        }

        var length = Math.Min(span.Length, body.Length - span.Start);
        return body.Substring(span.Start, length);
    }

    /// <summary>Heading display text, byte-identical to stripping the rendered heading HTML.</summary>
    /// <remarks>
    /// The inline subtree is rendered with the same pipeline (no re-parse) and tags are
    /// stripped exactly like the legacy template heading extraction, so TOC text is unchanged.
    /// </remarks>
    private static string RenderInlineText(ContainerInline? inline, MarkdownPipeline pipeline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        renderer.Render(inline);
        writer.Flush();
        return StripTagsRegex.Replace(writer.ToString(), string.Empty);
    }

    private static readonly Regex StripTagsRegex = new("<.*?>", RegexOptions.Singleline);

    private static string RenderPlainText(MarkdownDocument document, MarkdownPipeline pipeline)
    {
        // Same technique as Markdig's string-based ToPlainText, applied to the
        // already-parsed document so no second parse occurs.
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer)
        {
            EnableHtmlForBlock = false,
            EnableHtmlForInline = false,
            EnableHtmlEscape = false,
        };
        pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }
}
