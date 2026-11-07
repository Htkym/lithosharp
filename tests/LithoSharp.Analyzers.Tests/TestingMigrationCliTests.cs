using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LithoSharp.Analyzers.Tests;

[NotInParallel]
public sealed class TestingMigrationCliTests
{
    [Test]
    public async Task DryRunClassifiesReadsAndManualChangesWithoutExecutingOrWritingInputs()
    {
        using var fixture = new Fixture();
        var marker = Path.Combine(fixture.Root, "user-executed.txt");
        var source = "// 😀 original source\r\nclass C {\r\n"
            + "static C() { System.IO.File.WriteAllText(@\"" + marker + "\", \"executed\"); throw new System.Exception(); }\r\n"
            + "static LithoSharp.Testing.SiteTestDocument Owner { get { System.IO.File.WriteAllText(@\"" + marker + "\", \"getter\"); throw new System.Exception(); } }\r\n"
            + "void M(LithoSharp.Testing.SiteTestDocument doc, string selector, AngleSharp.Dom.IElement fallback) {\r\n";
        (string Prefix, string Expression, string Suffix, string Classification, string Reason, string? Preview)[] rows =
        [
            ("var title = /*😀*/ ", "doc.Document.Title", ";", "ReadCandidate", "supported_read_requires_review", "doc.Snapshot.Title"),
            ("var text = ", "doc.Document.QuerySelector(\"p\")?.TextContent", ";", "ReadCandidate", "supported_read_requires_review", "doc.Snapshot.Query(\"p\")?.TextContent"),
            ("var all = ", "doc.Document.QuerySelectorAll(\"p\")", ";", "ReadCandidate", "supported_read_requires_review", "doc.Snapshot.QueryAll(\"p\")"),
            ("var href = ", "doc.Document.QuerySelector(\"a\")?.GetAttribute(\"href\")", ";", "ReadCandidate", "supported_read_requires_review", "doc.Snapshot.Query(\"a\")?.GetAttribute(\"href\")"),
            ("", "doc.Document.QuerySelector(\"p\")!.SetAttribute(\"class\", \"changed\")", ";", "Manual", "dom_mutation", null),
            ("", "doc.Document.QuerySelector(\"p\")!.TextContent", " = \"changed\";", "Manual", "dom_mutation", null),
            ("var cast = (AngleSharp.Dom.IElement)", "doc.Document.QuerySelector(\"p\")", ";", "Manual", "dom_cast", null),
            ("var unsupported = ", "doc.Document.QuerySelector(\"p:has(a)\")", ";", "Manual", "selector_unsupported", null),
            ("var alias = ", "doc.Document", ";", "Manual", "dom_alias_or_escape", null),
            ("var unknown = ", "doc.Document.QuerySelector(selector)", ";", "Unknown", "selector_not_constant", null),
            ("", "doc.Document.QuerySelectorAll(\"p\")[0].TextContent", " = \"changed\";", "Manual", "dom_mutation", null),
            ("var indexedCast = (AngleSharp.Dom.IElement)", "doc.Document.QuerySelectorAll(\"p\")[0]", ";", "Manual", "dom_cast", null),
            ("var indexedRead = ", "doc.Document.QuerySelectorAll(\"p\")[0].TextContent", ";", "Manual", "read_shape_not_supported", null),
            ("", "(doc.Document.QuerySelector(\"p\") ?? fallback).TextContent = \"x\"", ";", "Manual", "outer_use_not_supported", null),
        ];
        var expected = new List<(int Start, int Line, int Column)>();
        foreach (var row in rows)
        {
            const string indent = "    ";
            // BOM is part of the original decoded source; every row starts on a new line.
            var accessOffset = row.Expression.IndexOf("doc.Document", StringComparison.Ordinal);
            expected.Add((1 + source.Length + indent.Length + row.Prefix.Length + accessOffset,
                source.Count(c => c == '\n') + 1, indent.Length + row.Prefix.Length + accessOffset + 1));
            source += indent + row.Prefix + row.Expression + row.Suffix + "\r\n";
        }
        source += "} }\r\n";
        var path = Path.Combine(fixture.Root, "legacy.cs");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(source)).ToArray());
        var before = Sha(path);
        var metadata = Metadata();
        var metadataBefore = metadata.ToDictionary(file => file, Sha);
        var result = await Run(fixture.Root, ["--dry-run", "--source", path, .. References(metadata)]);
        Check(result.Exit == 0, "Analysis succeeds without executing the user static initializer: " + result.Stdout + result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        var report = json.RootElement.GetProperty("migration");
        Check(report.GetProperty("dryRun").GetBoolean() && report.GetProperty("coverage").GetString() == "Partial", "Report is a bounded dry-run, not conversion certification");
        Check(report.GetProperty("bindingCoverage").GetString() == "Partial", "Nonconstant selector remains Unknown");
        Check(report.GetProperty("sources")[0].GetProperty("sourceHash").GetString() == before, "Hash covers original BOM/CRLF bytes");
        var findings = report.GetProperty("findings").EnumerateArray().ToArray();
        Check(findings.Length == rows.Length, "Exactly the original legacy getter accesses are reported");
        for (var i = 0; i < rows.Length; i++)
        {
            Check(findings[i].GetProperty("start").GetInt32() == expected[i].Start
                && findings[i].GetProperty("length").GetInt32() == "doc.Document".Length
                && findings[i].GetProperty("line").GetInt32() == expected[i].Line
                && findings[i].GetProperty("column").GetInt32() == expected[i].Column, "Original UTF16 location for row " + i);
            Check(findings[i].GetProperty("classification").GetString() == rows[i].Classification
                && findings[i].GetProperty("reason").GetString() == rows[i].Reason
                && findings[i].GetProperty("preview").GetString() == rows[i].Preview, "Formal classification/preview for row " + i);
            Check(findings[i].GetProperty("sourceHash").GetString() == before && findings[i].GetProperty("file").GetString() == path, "Finding binds the same source snapshot");
            Check(findings[i].GetProperty("expression").GetString() == rows[i].Expression, "Finding retains the full outer use for row " + i);
        }
        Check(json.RootElement.GetProperty("diagnostics").EnumerateArray().Count(d => d.GetProperty("id").GetString() == "LSA1501"
            && d.GetProperty("severity").GetString() == "Warning") == rows.Length, "CLI retains formal bound Warning identity");
        Check(Sha(path) == before && metadataBefore.All(pair => Sha(pair.Key) == pair.Value), "Source and metadata remain unchanged");
        Check(!File.Exists(marker), "User initializer/getter never executes");
        Check(Directory.GetFiles(fixture.Root).Length == 1, "No user output files are created");
    }

    [Test]
    public async Task DryRunKeepsUnresolvedBindingsUnknownAndRejectsWriteOptions()
    {
        using var fixture = new Fixture();
        foreach (var (name, header) in new[]
        {
            ("suppressed.cs", "#pragma warning disable LSA1501\n"),
            ("generated.g.cs", "// <auto-generated/>\n"),
        })
        {
            var input = Path.Combine(fixture.Root, name);
            var inputPrefix = header + "class Hidden { void M(LithoSharp.Testing.SiteTestDocument doc) { var title = ";
            await File.WriteAllTextAsync(input, inputPrefix + "doc.Document.Title; } }");
            var hash = Sha(input);
            var hidden = await Run(fixture.Root, ["--dry-run", "--source", input, .. References(Metadata())]);
            using var json = JsonDocument.Parse(hidden.Stdout);
            var report = json.RootElement.GetProperty("migration");
            Check(hidden.Exit == 0 && report.GetProperty("findings").GetArrayLength() == 1,
                "Explicit input remains inspected despite IDE suppression/generated policy: " + hidden.Stdout);
            var finding = report.GetProperty("findings")[0];
            Check(finding.GetProperty("classification").GetString() == "ReadCandidate"
                && finding.GetProperty("preview").GetString() == "doc.Snapshot.Title"
                && finding.GetProperty("start").GetInt32() == inputPrefix.Length
                && finding.GetProperty("sourceHash").GetString() == hash
                && report.GetProperty("bindingCoverage").GetString() == "Complete", "Binding coverage includes the reported suppressed/generated access");
            Check(!json.RootElement.GetProperty("diagnostics").EnumerateArray().Any(d => d.GetProperty("id").GetString() == "LSA1501"),
                "IDE warning suppression/generated-code policy stays intact");
            Check(Sha(input) == hash, "Explicit input remains unchanged");
        }
        var fake = Path.Combine(fixture.Root, "lookalike.cs");
        await File.WriteAllTextAsync(fake, "namespace LithoSharp.Testing { public sealed class SiteTestDocument { public AngleSharp.Dom.IDocument Document => throw new System.Exception(); } }"
            + "class Other { public AngleSharp.Dom.IDocument Document => throw new System.Exception(); }"
            + "class C { void M(LithoSharp.Testing.SiteTestDocument doc, Other other) { var x = doc.Document; var y = other.Document; } }");
        var unrelated = await Run(fixture.Root, ["--dry-run", "--source", fake, "--reference", Required("LSA1501_LEGACY_ANGLESHARP_METADATA")]);
        using (var json = JsonDocument.Parse(unrelated.Stdout))
        {
            Check(unrelated.Exit == 0 && json.RootElement.GetProperty("migration").GetProperty("findings").GetArrayLength() == 0,
                "Source lookalikes and unrelated getters are excluded: " + unrelated.Stdout);
            Check(!json.RootElement.GetProperty("diagnostics").EnumerateArray().Any(d => d.GetProperty("id").GetString() == "LSA1501"), "No fabricated migration Warning");
        }
        var v2 = Path.Combine(fixture.Root, "v2.cs");
        await File.WriteAllTextAsync(v2, "class C { void M(LithoSharp.Testing.SiteTestDocument doc) { var text = doc.Snapshot.Query(\"p\")?.TextContent; } }");
        var already = await Run(fixture.Root, ["--dry-run", "--source", v2, "--reference", Required("LSA1501_V2_TESTING_METADATA")]);
        using (var json = JsonDocument.Parse(already.Stdout))
            Check(already.Exit == 0 && json.RootElement.GetProperty("migration").GetProperty("findings").GetArrayLength() == 0,
                "Actual v2 read is already migrated: " + already.Stdout);
        const string prefix = "class C { void M(LithoSharp.Testing.SiteTestDocument doc) { var text = ";
        await File.WriteAllTextAsync(v2, prefix + "doc.Document; } }");
        var before = Sha(v2);
        var unresolved = await Run(fixture.Root, ["--dry-run", "--source", v2, "--reference", Required("LSA1501_V2_TESTING_METADATA")]);
        using (var json = JsonDocument.Parse(unresolved.Stdout))
        {
            var finding = json.RootElement.GetProperty("migration").GetProperty("findings")[0];
            Check(unresolved.Exit == 1 && finding.GetProperty("classification").GetString() == "Unknown"
                && finding.GetProperty("reason").GetString() == "legacy_binding_unavailable"
                && finding.GetProperty("diagnosticId").ValueKind == JsonValueKind.Null
                && finding.GetProperty("preview").ValueKind == JsonValueKind.Null
                && finding.GetProperty("start").GetInt32() == prefix.Length, "Removed v2 getter is a compiler error with Unknown binding");
            Check(json.RootElement.GetProperty("diagnostics").EnumerateArray().Any(d => d.GetProperty("id").GetString() == "CS1061")
                && !json.RootElement.GetProperty("diagnostics").EnumerateArray().Any(d => d.GetProperty("id").GetString() == "LSA1501"), "Compiler error is not a legacy Warning");
        }
        var marker = Path.Combine(fixture.Root, "target-executed.txt");
        var project = Path.Combine(fixture.Root, "user.csproj");
        await File.WriteAllTextAsync(project, "<Project><Target Name='Build'><WriteLinesToFile File='" + marker + "' Lines='executed'/></Target></Project>");
        var projectBefore = Sha(project);
        foreach (var args in new string[][]
        {
            ["--dry-run", "--source", v2, "--reference", Metadata()[0], "--apply"],
            ["--dry-run", "--source", project, "--reference", Metadata()[0]],
            ["--source", v2, "--reference", Metadata()[0]],
        })
        {
            var result = await Run(fixture.Root, args);
            using var json = JsonDocument.Parse(result.Stdout);
            Check(result.Exit == 2 && !json.RootElement.GetProperty("success").GetBoolean(), "Unsupported write/project/missing dry-run gets existing usage error envelope");
        }
        Check(Sha(v2) == before && Sha(project) == projectBefore && !File.Exists(marker), "No source edit or user build target execution");
    }

    private static string[] Metadata() => [Required("LSA1501_LEGACY_TESTING_METADATA"), Required("LSA1501_LEGACY_ANGLESHARP_METADATA")];
    private static string[] References(IEnumerable<string> paths) => paths.SelectMany(path => new[] { "--reference", path }).ToArray();
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name);
    private static string Sha(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
    private static async Task<(int Exit, string Stdout, string Stderr)> Run(string cwd, string[] args)
    {
        var start = new ProcessStartInfo(Required("MIGRATION_DOTNET"))
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { Required("MIGRATION_TOOL_DLL"), "migrate", "testing" }.Concat(args))
            start.ArgumentList.Add(arg);
        var evidence = Path.Combine(Required("MIGRATION_CLI_EVIDENCE"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--source" && File.Exists(args[i + 1]))
                File.Copy(args[i + 1], Path.Combine(evidence, Path.GetFileName(args[i + 1])));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var errors = await stderr;
        await File.WriteAllTextAsync(Path.Combine(evidence, "stdout.json"), output);
        await File.WriteAllTextAsync(Path.Combine(evidence, "stderr.log"), errors);
        await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
        {
            arguments = args,
            exitCode = process.ExitCode,
            pid = process.Id,
            birthUtc = process.StartTime.ToUniversalTime(),
            inputDirectory = cwd,
        }));
        return (process.ExitCode, output, errors);
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(AppContext.BaseDirectory, "testing-migration-" + Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped its directory");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
