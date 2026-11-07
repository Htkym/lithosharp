using System.Collections;
using System.Reflection;
using System.Text.Json;
using Syntamark;

internal static class MdHeaderScanChecks
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static void Run(Assembly runtime, Assembly portable)
    {
        Check(typeof(MarkdownParser).Assembly == runtime, "Header checks must use the loaded runtime facade.");
        var parserVersion = (string)runtime.GetType("Syntamark.Compilation.MdParserVersion", true)!
            .GetField("Value", Members)!.GetValue(null)!;
        foreach (var (newline, bom) in new[] { ("\n", ""), ("\r\n", "\uFEFF") })
        {
            var yaml = "title: [😀" + newline;
            var header = bom + "---" + newline + yaml + "---" + newline;
            var body = "\uD800" + newline + new string('>', 256) + " deep body" + newline + new string('x', 1048577);
            var raw = header + body;
            foreach (var result in ScanPair(portable, parserVersion, raw, maxInput: header.Length))
            {
                Check(Name(result, "State") == "Closed" && Name(result, "Status") == "Complete" && !Diagnostics(result).Any(),
                    "A closed header must not validate malformed YAML or inspect an oversized, invalid-Unicode, deeply nested body.");
                var yamlStart = bom.Length + 3 + newline.Length;
                Check(Span(result, "OpeningSpan") == new RawSpan(bom.Length, 3)
                    && Span(result, "YamlSpan") == new RawSpan(yamlStart, yaml.Length)
                    && Span(result, "ClosingSpan") == new RawSpan(yamlStart + yaml.Length, 3)
                    && Span(result, "HeaderSpan") == new RawSpan(0, header.Length)
                    && Span(result, "BodySpan") == new RawSpan(header.Length, body.Length)
                    && Span(result, "FailureSpan") is null && Value(result, "FailureLine") is null,
                    "Header evidence must retain original BOM/CRLF/UTF-16 delimiter, YAML and body boundaries.");
                var yamlSpan = Span(result, "YamlSpan")!.Value;
                var bodySpan = Span(result, "BodySpan")!.Value;
                Check(raw.Substring(yamlSpan.Start, yamlSpan.Length) == yaml
                    && raw.Substring(bodySpan.Start, bodySpan.Length) == body,
                    "Raw YAML and body slices must come from the unchanged input snapshot.");
            }
        }

        const string unclosed = "---\nkey: x";
        foreach (var result in ScanPair(portable, parserVersion, unclosed))
            Check(Name(result, "State") == "Unterminated" && Name(result, "Status") == "Partial"
                && Span(result, "OpeningSpan") == new RawSpan(0, 3) && Span(result, "YamlSpan") == new RawSpan(4, 6)
                && Span(result, "ClosingSpan") is null && Span(result, "BodySpan") is null
                && Span(result, "HeaderSpan") == new RawSpan(0, unclosed.Length)
                && Span(result, "FailureSpan") == new RawSpan(unclosed.Length, 0) && (int?)Value(result, "FailureLine") == 3
                && Name(Diagnostics(result).Single(), "Id") == "LMD004",
                "Only confirmed untermination may publish EOF failure evidence.");

        const string closed = "---\nkey: x\n---\nbody";
        const string invalid = "---\nkey: \uD800\n---\n";
        foreach (var item in new[]
        {
            (Raw: closed, MaxInput: 7, MaxScan: 256, Status: "Failed", Id: "LMD002"),
            (Raw: closed, MaxInput: 1048576, MaxScan: 1, Status: "Partial", Id: "LMD003"),
            (Raw: invalid, MaxInput: 1048576, MaxScan: 256, Status: "Failed", Id: "LMD001"),
        })
        {
            foreach (var result in ScanPair(portable, parserVersion, item.Raw, item.MaxInput, item.MaxScan))
            {
                var diagnostic = Diagnostics(result).Single();
                Check(Name(result, "State") == "Unknown" && Name(result, "Status") == item.Status
                    && Name(diagnostic, "Id") == item.Id && Value(result, "FailureLine") is null
                    && new[] { "OpeningSpan", "YamlSpan", "ClosingSpan", "BodySpan", "HeaderSpan", "FailureSpan" }
                        .All(property => Span(result, property) is null),
                    "Budget or Unicode failure must not invent boundaries or claim an unterminated EOF.");
                Check(Span(diagnostic, "RawSpan") == (item.Id == "LMD001" ? new RawSpan(invalid.IndexOf('\uD800'), 1) : null),
                    "Invalid header UTF-16 has a known failure unit; budget exhaustion has no invented position.");
            }
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        RejectCancellation(() => MarkdownParser.ScanFrontMatter(closed, cancellationToken: cancellation.Token));
        RejectCancellation(() => ScanPortable(portable, closed, Options(portable, 1048576, 256), cancellation.Token));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            task = "MD-header-scan-portability",
            status = "PASS",
            checks =
            "public runtime/netstandard2 Source/frontmatter-scan-1/header-only/BOM-CRLF/UTF-16 spans/identities/unknown versus EOF/cancellation"
        }));
    }

    private static object[] ScanPair(Assembly portable, string parserVersion, string raw, int maxInput = 1048576, int maxScan = 256)
    {
        var options = Options(portable, maxInput, maxScan);
        var runtime = MarkdownParser.ScanFrontMatter(raw, new MarkdownParseOptions(maxInputUtf16: maxInput,
            maxOutputItems: 1, maxNestingDepth: 1, maxScanUnits: maxScan));
        var source = ScanPortable(portable, raw, options, CancellationToken.None);
        foreach (var result in new object[] { runtime, source })
            Check(Name(result, "ContractVersion") == "frontmatter-scan/1" && Name(result, "ParserVersion") == parserVersion
                && Name(result, "ProfileId") == "lithosharp-markdown/1" && Name(result, "OptionsHash") == (string)Value(options, "Hash")!,
                "Both header hosts must identify their actual parser, profile, effective options and scan-only contract.");
        Check(Snapshot(runtime) == Snapshot(source), "Runtime facade and actual portable Source header evidence differ.");
        return [runtime, source];
    }

    private static object Options(Assembly portable, int maxInput, int maxScan) =>
        portable.GetType("Syntamark.Compilation.MdOptions", true)!.GetConstructors(Members)
            .Single(constructor => constructor.GetParameters().Length == 6)
            .Invoke(new object[] { maxInput, 1, 1, maxScan, "lithosharp-markdown/1", 1 });

    private static object ScanPortable(Assembly portable, string raw, object options, CancellationToken cancellationToken) =>
        portable.GetType("Syntamark.Compilation.MdParser", true)!.GetMethod("ScanFrontMatter", Members)!
            .Invoke(null, new[] { (object)raw, options, cancellationToken })!;

    // Select fields explicitly: reflection must preserve enum values and integer
    // ranges, and this header contract makes no body hash or body-facts claim.
    private static string Snapshot(object result) => JsonSerializer.Serialize(new
    {
        ContractVersion = (string)Value(result, "ContractVersion")!,
        ParserVersion = (string)Value(result, "ParserVersion")!,
        ProfileId = (string)Value(result, "ProfileId")!,
        OptionsHash = (string)Value(result, "OptionsHash")!,
        State = EnumSnapshot(Value(result, "State")!),
        Status = EnumSnapshot(Value(result, "Status")!),
        OpeningSpan = Span(result, "OpeningSpan"),
        YamlSpan = Span(result, "YamlSpan"),
        ClosingSpan = Span(result, "ClosingSpan"),
        BodySpan = Span(result, "BodySpan"),
        HeaderSpan = Span(result, "HeaderSpan"),
        FailureSpan = Span(result, "FailureSpan"),
        FailureLine = (int?)Value(result, "FailureLine"),
        Diagnostics = Diagnostics(result).Select(diagnostic => new
        {
            Id = (string)Value(diagnostic, "Id")!,
            Severity = EnumSnapshot(Value(diagnostic, "Severity")!),
            Message = (string)Value(diagnostic, "Message")!,
            Origin = (string)Value(diagnostic, "Origin")!,
            Reason = (string)Value(diagnostic, "Reason")!,
            RawSpan = Span(diagnostic, "RawSpan"),
        }),
    });

    private static object EnumSnapshot(object value)
    {
        Check(value.GetType().IsEnum, "A header enum must retain its enum type.");
        return new { Name = value.ToString(), Value = Convert.ToInt32(value) };
    }

    private static RawSpan? Span(object result, string property) => Value(result, property) is { } range
        ? new RawSpan((int)Value(range, "Start")!, (int)Value(range, "Length")!) : null;
    private static IEnumerable<object> Diagnostics(object result) => ((IEnumerable)Value(result, "Diagnostics")!).Cast<object>();
    private static string? Name(object result, string property) => Value(result, property)?.ToString();
    private static object? Value(object result, string property) => result.GetType().GetProperty(property, Members)!.GetValue(result);

    private static void RejectCancellation(Action action)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Header scan cancellation was swallowed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
