using System.Collections;
using System.Reflection;
using System.Text.Json;

internal static class MdBodyAnalysisChecks
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static void Run(Assembly runtime, Assembly portable)
    {
        foreach (var (newline, bom) in new[] { ("\n", ""), ("\r\n", "\uFEFF") })
        {
            var body = bom + string.Join(newline, "---", "", "# Body 😀", "",
                "See [go](target.md), [^note] and $x$.", "", "```md", "[^ignored]", "```", "", "---", "");
            var runtimeAnalysis = Analyze(runtime, body);
            var portableAnalysis = Analyze(portable, body);
            foreach (var (assembly, analysis) in new[] { (runtime, runtimeAnalysis), (portable, portableAnalysis) })
            {
                Check((string)Value(analysis, "RawText")! == body, "Body analysis must retain the exact BOM/newline snapshot.");
                Check(Range(Value(analysis, "BodySpan")) == (0, body.Length), "Explicit body input must begin at raw offset zero.");
                Check(Value(analysis, "Status")!.ToString() == "Complete"
                    && Value(Value(analysis, "FrontMatter")!, "State")!.ToString() == "Absent",
                    "Leading delimiters must remain body syntax without front matter recovery.");
                Check((string)Value(analysis, "ProfileId")! == "lithosharp-markdown/1", "Body input must keep the site profile.");
                Check((string)Value(analysis, "OptionsHash")! != (string)Value(Options(assembly, "Document"), "Hash")!,
                    "Document and body options must have distinct identities.");
                var heading = Items(Value(analysis, "Blocks")).Single(block => block.GetType().Name == "LithoHeading");
                Check(Range(Value(heading, "Span")).Start == body.IndexOf("# Body", StringComparison.Ordinal),
                    "Retained heading spans must use original UTF-16 offsets.");
                var candidate = Items(Value(analysis, "UnsupportedSyntax")).Single();
                Check(Value(candidate, "Kind")!.ToString() == "Footnote"
                    && (string)Value(candidate, "Reason")! == "footnoteReference"
                    && Range(Value(candidate, "RawSpan")) == (body.IndexOf("[^note]", StringComparison.Ordinal), "[^note]".Length),
                    "Body candidates must retain raw coordinates and exclude fenced content.");
            }
            Check(Serialize(runtimeAnalysis) == Serialize(portableAnalysis), "Runtime/portable body blocks, candidates and identities differ.");
        }

        const string limitedBody = "# Guide\n\nA paragraph.\n";
        var runtimeLimited = Analyze(runtime, limitedBody, "Headings, Syntax", maxOutputItems: 4);
        var portableLimited = Analyze(portable, limitedBody, "Headings, Syntax", maxOutputItems: 4);
        foreach (var analysis in new[] { runtimeLimited, portableLimited })
        {
            Check(Value(analysis, "Status")!.ToString() == "Partial" && Value(analysis, "Root") is null,
                "The post-block output limit must leave derived syntax incomplete.");
            Check(Items(Value(analysis, "Blocks")).Select(block => block.GetType().Name)
                .SequenceEqual(new[] { "LithoHeading", "LithoParagraph" }), "A later resource exit must retain completed rendering blocks.");
            var resource = Items(Value(analysis, "Diagnostics")).Single(diagnostic => (string)Value(diagnostic, "Origin")! == "resource");
            Check(Value(resource, "RawSpan") is null, "A resource exit must preserve its unknown source position.");
        }
        Check(Serialize(runtimeLimited) == Serialize(portableLimited), "Runtime/portable retained-block resource results differ.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            task = "MD-body-portability",
            status = "PASS",
            checks =
            "net10 runtime/netstandard2 Source/body identity/BOM-CRLF/raw spans/typed blocks/candidates/post-block resource retention/unknown position"
        }));
    }

    private static object Analyze(Assembly assembly, string body, string parts = "FrontMatter, UnsupportedSyntax", int maxOutputItems = 131072)
    {
        var parser = assembly.GetType("Syntamark.Compilation.MdParser", true)!;
        var partsType = assembly.GetType("Syntamark.Compilation.MdAnalysisParts", true)!;
        return parser.GetMethod("AnalyzeWithBlocks", Members)!.Invoke(null, new object?[]
            { body, Enum.Parse(partsType, parts), "portability", "body.md", null, Options(assembly, "Body", maxOutputItems), CancellationToken.None })!;
    }

    private static object Options(Assembly assembly, string inputKind, int maxOutputItems = 131072)
    {
        var type = assembly.GetType("Syntamark.Compilation.MdOptions", true)!;
        var constructor = type.GetConstructors(Members).Single(candidate => candidate.GetParameters().Length == 8);
        var parameters = constructor.GetParameters();
        return constructor.Invoke(new object?[] { Enum.Parse(parameters[0].ParameterType, inputKind),
            Enum.Parse(parameters[1].ParameterType, "None"), 1048576, maxOutputItems, 200, 16777216, "lithosharp-markdown/1", 1 });
    }

    private static object? Value(object value, string property) => value.GetType().GetProperty(property, Members)!.GetValue(value);
    private static object[] Items(object? value) => value is IEnumerable items ? items.Cast<object>().ToArray() : [];
    private static (int Start, int Length) Range(object? range) => range is null ? (-1, -1)
        : ((int)Value(range, "Start")!, (int)Value(range, "Length")!);
    private static object? RangeSnapshot(object? range) => range is null ? null
        : new { Start = (int)Value(range, "Start")!, Length = (int)Value(range, "Length")! };

    private static string Serialize(object analysis) => JsonSerializer.Serialize(new
    {
        RawText = Value(analysis, "RawText"),
        BodySpan = RangeSnapshot(Value(analysis, "BodySpan")),
        ProfileId = Value(analysis, "ProfileId"),
        ParserVersion = Value(analysis, "ParserVersion"),
        OptionsHash = Value(analysis, "OptionsHash"),
        Status = Value(analysis, "Status")!.ToString(),
        Blocks = Snapshot(Value(analysis, "Blocks")),
        Candidates = Items(Value(analysis, "UnsupportedSyntax")).Select(candidate => new
        {
            Kind = Value(candidate, "Kind")!.ToString(),
            Reason = Value(candidate, "Reason"),
            RawSpan = RangeSnapshot(Value(candidate, "RawSpan")),
        }),
        Diagnostics = Items(Value(analysis, "Diagnostics")).Select(diagnostic => new
        {
            Id = Value(diagnostic, "Id"),
            Origin = Value(diagnostic, "Origin"),
            Reason = Value(diagnostic, "Reason"),
            RawSpan = RangeSnapshot(Value(diagnostic, "RawSpan")),
        }),
    });

    private static object? Snapshot(object? value)
    {
        if (value is null || value is string || value is bool || value is int)
            return value;
        if (value is IEnumerable sequence)
            return sequence.Cast<object?>().Select(Snapshot).ToArray();
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["$type"] = value.GetType().Name };
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.GetIndexParameters().Length == 0)
                result[property.Name] = Snapshot(property.GetValue(value));
        return result;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid)
            throw new InvalidOperationException(message);
    }
}
