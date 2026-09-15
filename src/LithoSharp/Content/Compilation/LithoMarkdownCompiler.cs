using System.Text.RegularExpressions;

namespace LithoSharp.Content.Compilation;

/// <summary>
/// Litho Markdown frontend (CommonMark 0.31.2 + GFM 0.29 subset) behind
/// <see cref="IMarkdownCompiler"/>. The only product compiler since C13; the
/// Markdig reference lives in the explicit comparison project.
/// </summary>
internal sealed class LithoMarkdownCompiler : IMarkdownCompiler
{
    private int _parseCount;

    /// <summary>
    /// Compiler implementation version, pinned in <see cref="Fingerprint"/>.
    /// Bump on any output or semantics behavior change so persistent parse
    /// records from older implementations miss instead of rendering stale HTML.
    /// </summary>
    internal const string ImplementationVersion = "2";

    /// <summary>Pinned CommonMark/GFM specification versions (C04).</summary>
    internal const string SpecVersion = "commonmark-0.31.2+gfm-0.29";

    /// <summary>Creates a compiler with Litho frontend settings.</summary>
    public LithoMarkdownCompiler(MarkdownCompilerOptions? options = null)
    {
        Options = options ?? MarkdownCompilerOptions.Default;
        if (!string.Equals(Options.Frontend, "lithosharp", StringComparison.Ordinal))
        {
            Options = Options with { Frontend = "lithosharp" };
        }

        Fingerprint = $"lithosharp/{ImplementationVersion}/spec={SpecVersion}/syntax={Options.SyntaxProfile}/output={Options.OutputProfile}";
    }

    /// <inheritdoc />
    public string Frontend => "lithosharp";

    /// <inheritdoc />
    public MarkdownCompilerOptions Options { get; }

    /// <inheritdoc />
    public string Fingerprint { get; }

    /// <inheritdoc />
    public int ParseCount => Volatile.Read(ref _parseCount);

    /// <inheritdoc />
    public MarkdownCompilationResult Compile(string markdown)
    {
        var prepared = PrepareTree(markdown, default);
        return new MarkdownCompilationResult(RenderHtml(prepared));
    }

    /// <inheritdoc />
    public MarkdownCompilationResult Analyze(string markdown, DocumentSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var prepared = PrepareTree(markdown, default);
        var resolved = source ?? new DocumentSource(null, 0);
        var (syntax, semantics) = Map(prepared, markdown, resolved);
        return new MarkdownCompilationResult(RenderHtml(prepared), syntax, semantics);
    }

    /// <summary>Analyzes with cancellation.</summary>
    public MarkdownCompilationResult Analyze(string markdown, DocumentSource? source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var prepared = PrepareTree(markdown, cancellationToken);
        var resolved = source ?? new DocumentSource(null, 0);
        var (syntax, semantics) = Map(prepared, markdown, resolved);
        return new MarkdownCompilationResult(RenderHtml(prepared), syntax, semantics);
    }

    private readonly record struct PreparedTree(
        IReadOnlyList<LithoBlock> Blocks,
        List<string> HeadingTexts,
        IReadOnlyList<string> HeadingIds);

    private PreparedTree PrepareTree(string markdown, CancellationToken cancellationToken)
    {
        var blocks = Parse(markdown, cancellationToken);
        var texts = new List<string>();
        CollectHeadingTexts(blocks, texts);
        return new PreparedTree(blocks, texts, AssignIds(texts));
    }

    private static string RenderHtml(PreparedTree prepared) =>
        LithoHtmlRenderer.RenderHtml(prepared.Blocks, prepared.HeadingIds);

    /// <inheritdoc />
    public string RenderPlainText(string markdown) =>
        LithoHtmlRenderer.RenderPlainText(Parse(markdown));

    /// <summary>Parses a body into Litho blocks.</summary>
    public IReadOnlyList<LithoBlock> Parse(string markdown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        Interlocked.Increment(ref _parseCount);
        return LithoBlockParser.ParseBlocks(markdown, cancellationToken).Blocks;
    }

    /// <summary>Pre-parses a tree with heading ids for render-only measurement (no parse inside).</summary>
    internal (IReadOnlyList<LithoBlock> Tree, IReadOnlyList<string> HeadingIds) PrepareRender(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var tree = Parse(markdown);
        var texts = new List<string>();
        CollectHeadingTexts(tree, texts);
        return (tree, AssignIds(texts));
    }

    /// <summary>Renders a pre-parsed tree with precomputed ids (no parse inside).</summary>
    internal static string RenderTree(IReadOnlyList<LithoBlock> tree, IReadOnlyList<string> headingIds)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(headingIds);
        return LithoHtmlRenderer.RenderHtml(tree, headingIds);
    }

    /// <summary>Assigns ids from entity-decoded heading text, matching the reference slugs.</summary>
    private static IReadOnlyList<string> AssignIds(List<string> texts) => LithoSlug.Assign(texts);

    private static void CollectHeadingTexts(IReadOnlyList<LithoBlock> blocks, List<string> texts)
    {
        foreach (var heading in WalkHeadings(blocks))
        {
            texts.Add(System.Net.WebUtility.HtmlDecode(StripTags(LithoHtmlRenderer.RenderInlines(heading.Inlines))));
        }
    }

    private static IEnumerable<LithoHeading> WalkHeadings(IReadOnlyList<LithoBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case LithoHeading heading:
                    yield return heading;
                    break;
                case LithoQuote quote:
                    foreach (var nested in WalkHeadings(quote.Children))
                    {
                        yield return nested;
                    }

                    break;
                case LithoAdmonition admonition:
                    foreach (var nested in WalkHeadings(admonition.Children))
                    {
                        yield return nested;
                    }

                    break;
                case LithoDirective directive:
                    foreach (var nested in WalkHeadings(directive.Children))
                    {
                        yield return nested;
                    }

                    break;
                case LithoList list:
                    foreach (var item in list.Items)
                    {
                        foreach (var nested in WalkHeadings(item.Children))
                        {
                            yield return nested;
                        }
                    }

                    break;
            }
        }
    }

    private static (DocumentSyntax Syntax, DocumentSemantics Semantics) Map(
        PreparedTree prepared,
        string body,
        DocumentSource source)
    {
        var document = prepared.Blocks;
        var blocks = new List<SyntaxBlock>();
        var headings = new List<DocumentHeading>();
        var links = new List<DocumentLink>();
        var assets = new List<DocumentAsset>();
        var diagnostics = new List<LithoSharp.Diagnostics.SiteDiagnostic>();
        if (LithoLimits.FindFootnoteWarning(body, source.FilePath, source.BodyStartLine) is { } footnote)
        {
            diagnostics.Add(footnote);
        }

        if (LithoLimits.FindBrowserAssetWarning(body, source.FilePath, source.BodyStartLine) is { } browserAsset)
        {
            diagnostics.Add(browserAsset);
        }

        var headingIndex = new Counter();
        MapInto(document, body, source.BodyStartOffset, blocks, headings, links, assets, prepared, headingIndex);
        return (
            new DocumentSyntax(blocks),
            new DocumentSemantics(
                source,
                DocumentSemantics.TitleOf(headings),
                LithoHtmlRenderer.RenderPlainText(document),
                headings,
                links,
                assets,
                Components: [],
                Diagnostics: diagnostics));
    }

    private sealed class Counter
    {
        public int Value;
    }

    private static void MapInto(
        IReadOnlyList<LithoBlock> blocks,
        string body,
        int shift,
        List<SyntaxBlock> syntax,
        List<DocumentHeading> headings,
        List<DocumentLink> links,
        List<DocumentAsset> assets,
        PreparedTree prepared,
        Counter headingIndex)
    {
        // Syntax keeps top-level blocks only; semantics recurse into containers.
        foreach (var block in blocks)
        {
            switch (block)
            {
                case LithoHeading heading:
                    syntax.Add(new SyntaxBlock(BlockKind.Heading, heading.Span.Shift(shift), heading.Level));
                    break;
                case LithoParagraph paragraph:
                    syntax.Add(new SyntaxBlock(BlockKind.Paragraph, paragraph.Span.Shift(shift), 0));
                    break;
                case LithoCode code:
                    syntax.Add(new SyntaxBlock(BlockKind.Code, code.Span.Shift(shift), 0));
                    break;
                case LithoQuote quote:
                    syntax.Add(new SyntaxBlock(BlockKind.Quote, quote.Span.Shift(shift), 0));
                    break;
                case LithoAdmonition admonition:
                    syntax.Add(new SyntaxBlock(BlockKind.Admonition, admonition.Span.Shift(shift), 0));
                    break;
                case LithoDirective directive:
                    syntax.Add(new SyntaxBlock(BlockKind.Directive, directive.Span.Shift(shift), 0));
                    break;
                case LithoMath math:
                    syntax.Add(new SyntaxBlock(BlockKind.Math, math.Span.Shift(shift), 0));
                    break;
                case LithoList list:
                    syntax.Add(new SyntaxBlock(BlockKind.List, list.Span.Shift(shift), 0));
                    break;
                case LithoTable table:
                    syntax.Add(new SyntaxBlock(BlockKind.Table, table.Span.Shift(shift), 0));
                    break;
                case LithoBreak:
                    syntax.Add(new SyntaxBlock(BlockKind.ThematicBreak, block.Span.Shift(shift), 0));
                    break;
            }

            CollectSemantics(block, body, shift, headings, links, assets, prepared, headingIndex);
        }
    }

    private static void CollectSemantics(
        LithoBlock block,
        string body,
        int shift,
        List<DocumentHeading> headings,
        List<DocumentLink> links,
        List<DocumentAsset> assets,
        PreparedTree prepared,
        Counter headingIndex)
    {
        switch (block)
        {
            case LithoHeading heading:
            {
                var ordinal = headingIndex.Value++;
                var id = ordinal < prepared.HeadingIds.Count ? prepared.HeadingIds[ordinal] : "section";
                var text = ordinal < prepared.HeadingTexts.Count ? prepared.HeadingTexts[ordinal] : string.Empty;
                headings.Add(new DocumentHeading(
                    text,
                    id,
                    heading.Level,
                    heading.Level == 1 ? 2 : heading.Level,
                    heading.Span.Shift(shift)));
                MapInlines(heading.Inlines, body, shift, links, assets);
                break;
            }

            case LithoParagraph paragraph:
                MapInlines(paragraph.Inlines, body, shift, links, assets);
                break;
            case LithoQuote quote:
                foreach (var child in quote.Children)
                {
                    CollectSemantics(child, body, shift, headings, links, assets, prepared, headingIndex);
                }

                break;
            case LithoAdmonition admonition:
                foreach (var child in admonition.Children)
                {
                    CollectSemantics(child, body, shift, headings, links, assets, prepared, headingIndex);
                }

                break;
            case LithoDirective directive:
                foreach (var child in directive.Children)
                {
                    CollectSemantics(child, body, shift, headings, links, assets, prepared, headingIndex);
                }

                break;
            case LithoList list:
                foreach (var item in list.Items)
                {
                    foreach (var child in item.Children)
                    {
                        CollectSemantics(child, body, shift, headings, links, assets, prepared, headingIndex);
                    }
                }

                break;
            case LithoTable table:
                foreach (var row in table.Rows)
                {
                    foreach (var cell in row)
                    {
                        MapInlines(cell, body, shift, links, assets);
                    }
                }

                break;
        }
    }

    private static void MapInlines(
        IReadOnlyList<LithoInline> inlines,
        string body,
        int shift,
        List<DocumentLink> links,
        List<DocumentAsset> assets)
    {
        foreach (var (inline, exit) in LithoInline.Walk(inlines))
        {
            if (exit) continue;
            switch (inline)
            {
                case LithoLink link:
                {
                    links.Add(new DocumentLink(
                        Slice(body, link.Span),
                        link.Url,
                        link.Title,
                        link.Image,
                        link.Span.Shift(shift)));
                    if (link.Image)
                    {
                        assets.Add(new DocumentAsset(link.Url, link.Span.Shift(shift)));
                    }

                    break;
                }

                case LithoAutolink autolink:
                    links.Add(new DocumentLink(
                        autolink.Text, autolink.Href, null, false, autolink.Span.Shift(shift)));
                    break;
            }
        }
    }

    private static string Slice(string body, SourceSpan span)
    {
        if (span.IsEmpty || span.Start >= body.Length)
        {
            return string.Empty;
        }

        return body.Substring(span.Start, Math.Min(span.Length, body.Length - span.Start));
    }

    private static string StripTags(string html) =>
        TagStripper.Replace(html, string.Empty);

    private static readonly Regex TagStripper = new("<.*?>", RegexOptions.Singleline);
}
