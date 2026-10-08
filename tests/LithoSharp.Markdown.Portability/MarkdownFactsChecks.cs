using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Markdown;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

internal static class MarkdownFactsChecks
{
    internal static void Run()
    {
        var doc = Parse("\uFEFF---\r\ntitle: A\r\n---\r\n# A\r\n");
        Check(doc.Status == MarkdownParseStatus.Complete && doc.FrontMatter.State == MarkdownFrontMatterState.Parsed, "BOM/FM status");
        Span(doc.FrontMatter.OpeningSpan, 1, 4); Span(doc.FrontMatter.YamlSpan, 6, 16); Span(doc.FrontMatter.ClosingSpan, 16, 19);
        Span(doc.BodySpan, 21, 26); Span(doc.Headings[0].RawSpan, 21, 24); Span(doc.Headings[0].TextRawSpan, 23, 24);
        Span(doc.FrontMatter.Root!.Children[0].RawSpan, 6, 11); Span(doc.FrontMatter.Root.Children[1].RawSpan, 13, 14);
        Span(doc.Sections[1].SubtreeSpan, 21, 26); Span(doc.Sections[1].DirectBodySpan, 26, 26);
        Check(doc.TextHash == "dd4912570ba5d83a5629e11798ccf3d378862ec206fbd2ece9bd14609b889930", "Strict full-input hash");
        doc = Parse("\uFEFF# A\r\n"); Span(doc.BodySpan, 1, 6); Span(doc.Sections[0].SubtreeSpan, 1, 1);

        doc = Parse("# A&amp;\\*😀\r\n");
        var text = doc.Headings[0].Text;
        Check(doc.Status == MarkdownParseStatus.Complete && text.Text == "A&*😀", "Entity/escape/emoji text");
        Check(text.SourceSegments.Count == 4, "Four token mappings");
        var mapped = text.Map(new RawSpan(1, 1));
        Check(mapped.Precision == MarkdownMappingPrecision.CoveringTokens, "Entity query precision"); Span(mapped.RawFragments.Single(), 3, 8);
        mapped = text.Map(new RawSpan(2, 1)); Span(mapped.RawFragments.Single(), 8, 10);
        mapped = text.Map(new RawSpan(3, 1)); Check(mapped.Precision == MarkdownMappingPrecision.Exact, "UTF-16 surrogate half is selectable"); Span(mapped.RawFragments.Single(), 10, 11);
        doc = Parse("> a\r\n> b"); text = doc.TextRegions.Single().Content; mapped = text.Map(new RawSpan(0, 3));
        Check(text.Text == "a\nb" && mapped.RawFragments.Count == 2 && mapped.Precision == MarkdownMappingPrecision.CoveringTokens, "Quote prefix gap");
        Span(mapped.RawFragments[0], 2, 5); Span(mapped.RawFragments[1], 7, 8);
        Check(text.Map(new RawSpan(2, 0)).Precision == MarkdownMappingPrecision.Unknown, "Ambiguous gap boundary");

        const string nested = "intro\r\n# Parent\r\nparent\r\n> ### Child\r\n> [Guide][r]\r\n\r\n## Sibling\r\n~~~csharp\r\nx😀\r\n~~~\r\n[r]: /docs?a=1&amp;b=2 \"Guide\"\r\n[missing][none]\r\n";
        doc = Parse(nested);
        Check(doc.Status == MarkdownParseStatus.Complete, "Nested/ref/fence facts complete");
        Check(doc.Headings.Select(h => h.RawLevel).SequenceEqual(new[] { 1, 3, 2 }), "Nested outline raw levels");
        Check(doc.Sections[2].ParentLocalKey == "section-0" && doc.Sections[3].ParentLocalKey == "section-0", "Skipped-level outline parents");
        Check(nested.Substring(doc.Sections[1].DirectBodySpan!.Value.Start, doc.Sections[1].DirectBodySpan!.Value.Length) == "parent\r\n> ", "Direct body stops at child heading");
        var reference = doc.Links.Single(l => l.Kind == MarkdownLinkKind.ReferenceUse && l.Resolution == MarkdownLinkResolution.ResolvedReference);
        Check(reference.Target == "/docs?a=1&b=2" && reference.TargetRawSpan.HasValue, "Forward definition resolution");
        Check(doc.Links.Any(l => l.Resolution == MarkdownLinkResolution.UnresolvedReference), "Unresolved reference fact");
        var fence = doc.Fences.Single(); Check(fence.Closed && fence.Content.Text == "x😀\n", "CRLF fence decoding");
        Check(nested.Substring(fence.OpeningMarkerSpan.Start, fence.OpeningMarkerSpan.Length) == "~~~", "Fence opening slice");
        Check(nested.Substring(fence.ClosingMarkerSpan!.Value.Start, fence.ClosingMarkerSpan!.Value.Length) == "~~~", "Fence closing slice");
        Check(fence.Content.Map(new RawSpan(0, fence.Content.Text!.Length)).Precision == MarkdownMappingPrecision.CoveringTokens, "Fence content mapping");
        doc = Parse("~~~\nx\n"); Check(doc.Status == MarkdownParseStatus.Complete && !doc.Fences.Single().Closed, "Unclosed valid fence");
        doc = Parse("Title\r\n===\r\nbody\r\n"); Span(doc.Headings[0].RawSpan, 0, 10); Span(doc.Sections[1].DirectBodySpan, 12, 18);
        doc = Parse("# A![ALT](/x)B\n# A![ALT](/x)B\n");
        Check(doc.Headings.Select(h => h.Anchor).SequenceEqual(new[] { "ab", "ab-1" }), "Legacy image heading slug and duplicate suffix");

        // Verify the installed library's actual Mark.Index/End boundaries around a surrogate pair.
        const string yaml = "title: 😀\r\nnext: Z\r\n";
        var yamlParser = new Parser(new StringReader(yaml)); var scalars = new List<Scalar>();
        while (yamlParser.MoveNext()) if (yamlParser.Current is Scalar scalar) scalars.Add(scalar);
        Check(scalars.Count == 4, "YAML event count");
        Check(scalars[1].Start.Index == 7 && scalars[1].End.Index == 9 && scalars[2].Start.Index == 11 && scalars[3].End.Index == 18, "YamlDotNet 18.1.0 UTF-16 half-open marks");
        doc = Parse("---\r\n" + yaml + "---\r\n# A\r\n");
        Check(doc.Status == MarkdownParseStatus.Complete, "YAML emoji status"); Span(doc.FrontMatter.Root!.Children[1].RawSpan, 12, 14);
        foreach (var (input, reason) in new[] { ("a: 1\na: 2\n", "duplicateKey"), ("a: &x value\n", "alias"), ("[one, two]\n", "invalidRoot"), ("null: x\n", "invalidKey"), ("a: [\n", "syntax"), ("a: 1\n--- \nb: 2\n", "multipleDocument"), ("a: 1\n---\nb: 2\n", "delimiterBody") })
        {
            doc = Parse("---\n" + input + "---\n# A\n");
            if (reason == "delimiterBody") { Check(doc.FrontMatter.State == MarkdownFrontMatterState.Parsed, "Strict first closing delimiter"); continue; }
            Check(doc.Status == MarkdownParseStatus.Partial && doc.Headings.Count == 1 && doc.Diagnostics.Any(d => d.Id == "LMD005" && d.Reason == reason), "Invalid YAML reason " + reason);
        }
        doc = Parse("---\n\n---\n# A\n"); Check(doc.Status == MarkdownParseStatus.Complete && doc.FrontMatter.State == MarkdownFrontMatterState.Empty, "Empty optional FM");
        doc = Parse("---\ntitle: X\n# Body\n"); Check(doc.Status == MarkdownParseStatus.Partial && doc.BodySpan is null && doc.Headings.Count == 0, "Unterminated FM is not parsed as Markdown");
        doc = Parse("---\ntitle: >\n  folded\n  text\n---\n# A\n");
        Check(doc.Status == MarkdownParseStatus.Partial && doc.Coverage.DecodedMapping == MarkdownCoverageState.Partial && doc.Diagnostics.Any(d => d.Id == "LMD006"), "Folded scalar Unknown mapping");
        doc = Parse("<Widget>{value}</Widget>\n"); Check(doc.Status == MarkdownParseStatus.Partial && doc.Coverage.UnprojectedRegions.Single().Content.Text is null, "Opaque HTML/MDX");

        doc = Parse("---\na: b\n---\n# A\n", new MarkdownParseOptions(maxOutputItems: 2));
        Check(doc.Status == MarkdownParseStatus.Partial && doc.FrontMatter.State == MarkdownFrontMatterState.Unknown
            && doc.BodySpan is null && doc.Diagnostics.Any(d => d.Id == "LMD003"), "Unfinished YAML is not confirmed Absent");
        doc = Parse(new string(new[] { (char)55296 }));
        Check(doc.Status == MarkdownParseStatus.Failed && doc.TextHash is null && doc.FrontMatter.State == MarkdownFrontMatterState.Unknown && doc.Diagnostics.Single().Id == "LMD001", "Invalid Unicode failure"); Span(doc.Diagnostics.Single().RawSpan, 0, 1);
        doc = Parse("abc", new MarkdownParseOptions(maxInputUtf16: 2)); Check(doc.Status == MarkdownParseStatus.Failed && doc.TextHash is null && doc.Diagnostics.Single().Id == "LMD002", "Input limit precedes hashing");
        foreach (var options in new[] { new MarkdownParseOptions(maxScanUnits: 1), new MarkdownParseOptions(maxOutputItems: 2), new MarkdownParseOptions(maxNestingDepth: 1) })
        { doc = Parse("> > # Deep\n", options); Check(doc.Status == MarkdownParseStatus.Partial && doc.Diagnostics.Any(d => d.Id == "LMD003"), "Resource limit is explicit"); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { MarkdownParser.Parse("x", "s", "d", cancellationToken: cancelled.Token); throw new InvalidOperationException("Cancellation swallowed"); } catch (OperationCanceledException) { }
        try { MarkdownParser.Parse("x", "", "d"); throw new InvalidOperationException("Empty identifier accepted"); } catch (ArgumentException) { }
        try { _ = new MarkdownParseOptions(maxScanUnits: 0); throw new InvalidOperationException("Bad option accepted"); } catch (ArgumentOutOfRangeException) { }
        doc = Parse("# Same\n");
        Check(doc.OptionsHash == Hash("{\"maxInputUtf16\":1048576,\"maxNestingDepth\":200,\"maxOutputItems\":131072,\"maxScanUnits\":16777216,\"optionsSchemaVersion\":1,\"profileId\":\"lithosharp-markdown/1\"}"), "Canonical default options hash");
        var bound = MarkdownParser.Parse("# Same\n", "different", "different", "2");
        Check(doc.TextHash == bound.TextHash && doc.ScopeId != bound.ScopeId && doc.SourceId != bound.SourceId && bound.SourceVersion == "2", "Separate immutable binding");
        Check(doc.Headings is IList list && list.IsReadOnly, "Collections expose read-only backing");
        doc = Parse("a\r\nb\nc\rd"); Check(doc.GetLinePosition(7) == new MarkdownLinePosition(4, 1), "Derived CRLF/LF/CR line positions");
        Console.WriteLine(JsonSerializer.Serialize(new { task = "MD-02", status = "PASS", checks = "UTF16 mapping/frontmatter/outline/reference/fence/identity/options/resources/cancellation", yamlMarkUnit = "UTF-16", yamlAssembly = typeof(Parser).Assembly.FullName,
            yamlInformationalVersion = typeof(Parser).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            parserVersion = doc.ParserVersion }));
    }
    private static MarkdownDocument Parse(string text, MarkdownParseOptions? options = null) => MarkdownParser.Parse(text, "scope", "source", "1", options);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(value))).ToLowerInvariant();
    private static void Span(RawSpan? span, int start, int end) => Check(span.HasValue && span.Value.Start == start && span.Value.End == end, $"Raw span [{start},{end}) got {span}");
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
