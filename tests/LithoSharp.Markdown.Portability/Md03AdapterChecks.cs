using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using LithoSharp.Content;
using LithoSharp.Inspection;
using Syntamark;

internal static class Md03AdapterChecks
{
    internal static void Run(Assembly product, Assembly runtime, Assembly portable)
    {
        const string body = "# A &amp; **bold** ![hidden](pic.png) $x$ &amp;lt;C&amp;gt;\n\n[Guide](/docs)\n";
        var generic = MarkdownParser.Parse(body, "scope", "source");
        var site = DocumentInspection.Inspect("doc.md", body);
        Check(generic.Status == MarkdownParseStatus.Complete && generic.FrontMatter.State == MarkdownFrontMatterState.Absent,
            "Generic optional frontmatter remains independent");
        var missing = site.Diagnostics.Single(diagnostic => diagnostic.Id == "LSM001");
        Check(missing.Location?.Line == 1 && missing.Location?.Column == 1 && site.Headings[0].RawLevel == 1 && site.Headings[0].OutputLevel == 2,
            "Site keeps required frontmatter and h1 output projection");
        var compilerType = product.GetType("LithoSharp.Content.Compilation.LithoMarkdownCompiler", true)!;
        var compiler = Activator.CreateInstance(compilerType, new object?[] { null })!;
        var sourceType = product.GetType("LithoSharp.Content.Compilation.DocumentSource", true)!;
        var source = Activator.CreateInstance(sourceType, new object?[] { "doc.md", 0 })!;
        var analyze = compilerType.GetMethod("Analyze", new[] { typeof(string), sourceType, typeof(CancellationToken) })!;
        var inspect = compilerType.GetMethod("AnalyzeForInspection", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var rendered = analyze.Invoke(compiler, new[] { (object)body, source, CancellationToken.None })!;
        var inspected = inspect.Invoke(compiler, new[] { (object)body, source, CancellationToken.None })!;
        Check((int)compilerType.GetProperty("ParseCount")!.GetValue(compiler)! == 2, "One parse per compile/inspection call");
        Check((string)inspected.GetType().GetProperty("Html")!.GetValue(inspected)! == "", "Inspection does not produce body HTML");
        foreach (var property in new[] { "Syntax", "Semantics" })
            Check(JsonSerializer.Serialize(rendered.GetType().GetProperty(property)!.GetValue(rendered))
                == JsonSerializer.Serialize(inspected.GetType().GetProperty(property)!.GetValue(inspected)), "Inspection/render " + property + " parity");

        // The old observable heading calculation is retained only as this small oracle.
        var parser = runtime.GetType("Syntamark.Compilation.LithoBlockParser", true)!;
        var parsed = parser.GetMethod("ParseBlocks", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { body, CancellationToken.None })!;
        var blocks = ((IEnumerable)parsed.GetType().GetField("Item1")!.GetValue(parsed)!).Cast<object>().ToArray();
        var heading = blocks.First(block => block.GetType().Name == "LithoHeading");
        var inlines = heading.GetType().GetProperty("Inlines")!.GetValue(heading)!;
        var renderer = product.GetType("LithoSharp.Content.Compilation.LithoHtmlRenderer", true)!;
        var inlineHtml = (string)renderer.GetMethod("RenderInlines")!.Invoke(null, new[] { inlines })!;
        var legacyText = System.Net.WebUtility.HtmlDecode(Regex.Replace(inlineHtml, "<.*?>", "", RegexOptions.Singleline));
        Check(site.Headings[0].Text == legacyText && site.Title == legacyText
            && legacyText.Contains(@"\(x\)", StringComparison.Ordinal) && !legacyText.Contains("hidden", StringComparison.Ordinal),
            "Heading keeps decoded-once text, omitted image labels and visible math delimiters");

        const string delimited = "\uFEFF---\r\ntitle: Yaml\r\n---\r\n# Body\r\n";
        var split = MarkdownDocumentParser.SplitFrontMatter(delimited, "doc.md");
        Check(split.Yaml == "title: Yaml\r\n" && split.Body == "# Body\r\n", "Shared delimiters preserve legacy slices");
        site = DocumentInspection.Inspect("doc.md", delimited);
        Check(site.Diagnostics.Count == 0 && site.Headings[0].Location?.Line == 4
            && site.FrontMatter["title"] is string value && value == "Yaml", "Site located YAML and body positions remain unchanged");
        const string unclosed = "---\ntitle: Yaml\n# Body\n";
        generic = MarkdownParser.Parse(unclosed, "scope", "source");
        site = DocumentInspection.Inspect("doc.md", unclosed);
        Check(generic.FrontMatter.State == MarkdownFrontMatterState.Unterminated && generic.BodySpan is null && generic.Headings.Count == 0,
            "Generic unclosed frontmatter does not reinterpret YAML as Markdown");
        Check(site.Diagnostics.Single().Id == "LSM002" && site.Diagnostics[0].Location?.Line == 4
            && site.Headings.Single().Location?.Line == 3, "Legacy unclosed whole-input fallback and failure line remain unchanged");
        site = DocumentInspection.Inspect("doc.md", "---\n \n---\n# Body\n");
        Check(site.Diagnostics.Single().Id == "LSM003" && site.Diagnostics[0].Location?.Line == 2, "Site empty-frontmatter diagnostic");
        foreach (var yamlCase in new[] { ("title: [", "LSM004"), ("- item", "LSM008"), ("title: &name x", "LSM006"), ("title: x\ntitle: y", "LSM005"), ("? [x, y]\n: z", "LSM007") })
            Check(DocumentInspection.Inspect("doc.md", "---\n" + yamlCase.Item1 + "\n---\n# Body\n").Diagnostics.Any(d => d.Id == yamlCase.Item2),
                "Located site YAML diagnostic preserved: " + yamlCase.Item2);

        // Source packages intentionally exclude the runtime facade; exercise the compiled portable parser.
        const BindingFlags portableMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        Check(portable.GetType("Syntamark.MarkdownParser") is null, "Source host must exclude the runtime facade");
        var portableParser = portable.GetType("Syntamark.Compilation.MdParser", true)!;
        var optionsType = portable.GetType("Syntamark.Compilation.MdOptions", true)!;
        var options = optionsType.GetConstructors(portableMembers).Single().Invoke(new object?[]
            { 1048576, 131072, 200, 16777216, "lithosharp-markdown/1", 1 });
        var portableParse = portableParser.GetMethod("Parse", portableMembers)!;
        var portableDoc = portableParse.Invoke(null, new object?[] { delimited, "scope", "source", null, options, CancellationToken.None })!;
        generic = MarkdownParser.Parse(delimited, "scope", "source");
        foreach (var property in new[] { "ParserVersion", "TextHash", "OptionsHash" })
            Check((string)portableDoc.GetType().GetProperty(property, portableMembers)!.GetValue(portableDoc)!
                == (string)generic.GetType().GetProperty(property)!.GetValue(generic)!, "Runtime/portable generic " + property + " agreement");
        Check(portableDoc.GetType().GetProperty("Status", portableMembers)!.GetValue(portableDoc)!.ToString() == "Complete",
            "Portable generic facts can parse YAML in a separate load context");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { DocumentInspection.Inspect("doc.md", body, cancellationToken: cancelled.Token); throw new InvalidOperationException("Site cancellation swallowed"); }
        catch (OperationCanceledException) { }

        Console.WriteLine(JsonSerializer.Serialize(new { task = "MD-03", status = "PASS", checks =
            "shared runtime assembly/one parse/no body HTML/heading legacy parity/frontmatter site policy and LSM001-008/offsets/runtime-portable facts/cancellation",
            parserVersion = generic.ParserVersion, actualRoslynHost = "NOT_RUN; MD-04/IN-01" }));
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
