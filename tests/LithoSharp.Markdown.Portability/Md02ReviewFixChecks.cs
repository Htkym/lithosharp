using System.Reflection;
using System.Text.Json;
using LithoSharp.Markdown;

internal static class Md02ReviewFixChecks
{
    internal static void Run(Assembly runtime, Assembly portable)
    {
        var tick = ((char)96).ToString();
        foreach (var protectedText in new[] { tick + "<Widget>" + tick, @"\<Widget>", "&lt;Widget&gt;", "[Target](<Widget>)" })
        {
            var doc = Parse(protectedText + " [Guide](/docs)\n");
            Check(doc.Status == MarkdownParseStatus.Complete && doc.Coverage.UnprojectedRegions.Count == 0,
                "Code/escape/entity/destination must not be opaque: " + protectedText);
            Guide(doc);
        }

        const string attributed = "<Widget title=\"[fake](/fake) >\"> [Guide](/docs)\n";
        var opaque = Parse(attributed);
        Guide(opaque);
        Check(opaque.Status == MarkdownParseStatus.Partial && opaque.Links.All(link => link.Target != "/fake"),
            "Opaque attribute must not manufacture a Markdown link");
        Check(opaque.Coverage.UnprojectedRegions.Count == 1
            && Slice(attributed, opaque.Coverage.UnprojectedRegions[0].RawSpan) == "<Widget title=\"[fake](/fake) >\">",
            "A quoted greater-than must not end the opaque tag");
        var paragraph = opaque.TextRegions.First(region => region.Kind == "paragraph").Content;
        var guideAt = paragraph.Text!.IndexOf("Guide", StringComparison.Ordinal);
        var guideMap = paragraph.Map(new RawSpan(guideAt, 5));
        Check(guideMap.Precision == MarkdownMappingPrecision.Exact && guideMap.RawFragments.Count == 1
            && Slice(attributed, guideMap.RawFragments[0]) == "Guide", "Text outside opaque tokens remains exact");
        Check(paragraph.Map(new RawSpan(0, paragraph.Text.Length)).Precision == MarkdownMappingPrecision.Partial,
            "A mixed opaque projection must not advertise Exact");

        foreach (var source in new[] { "# <Widget> [Guide](/docs)\n", "| Cell |\n| --- |\n| <Widget> [Guide](/docs) |\n" })
        {
            var doc = Parse(source); Guide(doc);
            Check(doc.Status == MarkdownParseStatus.Partial && doc.Coverage.UnprojectedRegions.Count == 1,
                "Real HTML in heading/table must be Partial");
            var text = doc.Headings.Count != 0 ? doc.Headings[0].Text
                : doc.TextRegions.First(region => region.Kind == "tableCell" && region.Content.Text!.Contains("Widget", StringComparison.Ordinal)).Content;
            Check(text.Map(new RawSpan(0, text.Text!.Length)).Precision == MarkdownMappingPrecision.Partial,
                "Heading/table opaque mapping must not advertise Exact");
        }
        opaque = Parse("[<Widget>][missing] [Guide](/docs)\n"); Guide(opaque);
        var unresolved = opaque.Links.Single(link => link.Resolution == MarkdownLinkResolution.UnresolvedReference);
        Check(unresolved.Label.Map(new RawSpan(0, unresolved.Label.Text!.Length)).Precision == MarkdownMappingPrecision.Unknown,
            "Unresolved reference labels use the same opaque mask");
        foreach (var prose in new[] { "import the guide", "export useful features" })
        {
            var doc = Parse(prose + " [Guide](/docs)\n"); Guide(doc);
            Check(doc.Status == MarkdownParseStatus.Complete && doc.Coverage.UnprojectedRegions.Count == 0,
                "Ordinary import/export prose must not hide a Markdown link");
        }
        opaque = Parse("import X from 'x'; [Guide](/docs)\n"); Guide(opaque);
        Check(opaque.Status == MarkdownParseStatus.Partial && opaque.Coverage.UnprojectedRegions.Count == 1,
            "ESM candidate preserves the following Markdown link");

        foreach (var assembly in new[] { runtime, portable }) CheckAnchors(assembly);
        Console.WriteLine(JsonSerializer.Serialize(new { task = "MD-02 reviewer fixes", status = "PASS",
            checks = "opaque protected tokens/outside links/heading/table/unresolved label/ESM; suffix ordering/budget/cancellation", parserVersion = opaque.ParserVersion }));
    }

    private static void CheckAnchors(Assembly assembly)
    {
        var slug = assembly.GetType("LithoSharp.Content.Compilation.LithoSlug", throwOnError: true)!;
        var assign = slug.GetMethod("Assign", BindingFlags.Public | BindingFlags.Static)!;
        Check(((IReadOnlyList<string>)assign.Invoke(null, new object[] { new[] { "A", "A-1", "A", "A" } })!)
            .SequenceEqual(new[] { "a", "a-1", "a-2", "a-3" }), "Literal -1 must keep its collision slot");
        Check(((IReadOnlyList<string>)assign.Invoke(null, new object[] { new[] { "A", "A", "A-1", "A" } })!)
            .SequenceEqual(new[] { "a", "a-1", "a-1-1", "a-2" }), "Suffix-family ordering must stay compatible");
        // Exercise this loop directly: a whole-parse low budget could fail before anchor allocation.
        var optionsType = assembly.GetType("LithoSharp.Content.Compilation.MdOptions", true)!;
        var contextType = assembly.GetType("LithoSharp.Content.Compilation.MdParseContext", true)!;
        var allocatorType = assembly.GetType("LithoSharp.Content.Compilation.LithoAnchorIds", true)!;
        var next = allocatorType.GetMethod("Next", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object Allocator(int budget, CancellationToken token)
        {
            var options = Activator.CreateInstance(optionsType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { 1048576, 131072, 200, budget, "lithosharp-markdown/1", 1 }, null)!;
            var context = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { options, (object)token }, null)!;
            return Activator.CreateInstance(allocatorType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { context }, null)!;
        }
        var tiny = Allocator(9, CancellationToken.None);
        Check((string)next.Invoke(tiny, new object[] { "a" })! == "a", "First anchor fits the tiny budget");
        try { next.Invoke(tiny, new object[] { "a" }); throw new InvalidOperationException("Suffix search bypassed budget"); }
        catch (TargetInvocationException error) when (error.InnerException?.GetType().Name == "MdResourceLimit") { }
        using var cancelled = new CancellationTokenSource();
        var cancellable = Allocator(10000, cancelled.Token); next.Invoke(cancellable, new object[] { "a" }); cancelled.Cancel();
        try { next.Invoke(cancellable, new object[] { "a" }); throw new InvalidOperationException("Suffix search swallowed cancellation"); }
        catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException) { }
        var repeated = Allocator(10000, CancellationToken.None);
        for (var index = 0; index < 256; index++)
            Check((string)next.Invoke(repeated, new object[] { "a" })! == (index == 0 ? "a" : "a-" + index),
                "Repeated headings fit a linear candidate budget");
    }

    private static MarkdownDocument Parse(string source) => MarkdownParser.Parse(source, "scope", "source");
    private static string Slice(string source, RawSpan? span) => span.HasValue ? source.Substring(span.Value.Start, span.Value.Length) : "";
    private static void Guide(MarkdownDocument doc) => Check(doc.Links.Any(link => link.Target == "/docs" && link.Label.Text == "Guide"), "Outside Guide link lost");
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
