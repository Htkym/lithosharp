using LithoSharp.Content;
using LithoSharp.Documentation;

namespace LithoSharp.Tests;

public sealed class DocumentSnippetVerificationTests
{
    [Test]
    public async Task ZeroExitRequiresPassedExecutionEvidenceAndDoesNotCertifySkippedOrEmptySelections()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "run.trx");
        await Assert.That(Inspect().State).IsEqualTo(DocumentSnippetVerificationState.NotRun);
        foreach (var (report, expected) in new[]
        {
            (Trx("", 0, 0, 0, 0), DocumentSnippetVerificationState.NotRun),
            (Trx(Result("Passed"), 1, 1, 1, 0), DocumentSnippetVerificationState.Success),
            (Trx(Result("NotExecuted"), 1, 0, 0, 1), DocumentSnippetVerificationState.Skipped),
            (Trx(Result("Failed"), 1, 1, 0, 0, failed: 1), DocumentSnippetVerificationState.Failure),
            (Trx(Result("Inconclusive"), 1, 1, 0, 0), DocumentSnippetVerificationState.NotRun),
            (Trx(Result("Passed"), 2, 2, 2, 0), DocumentSnippetVerificationState.NotRun),
            (Trx(Result("Passed"), 1, 1, 1, 0).Replace("failed=\"0\"", "failed=\"invalid\"", StringComparison.Ordinal), DocumentSnippetVerificationState.NotRun),
            (Trx(Result("Passed") + Result("Passed"), 2, 2, 2, 0), DocumentSnippetVerificationState.NotRun),
        })
        {
            await File.WriteAllTextAsync(path, report);
            await Assert.That(Inspect().State).IsEqualTo(expected);
        }
        await Assert.That(DocumentationVerification.InspectSnippetExecutionReports(workspace.Root, 1, CancellationToken.None).State)
            .IsEqualTo(DocumentSnippetVerificationState.Failure);
        await File.WriteAllTextAsync(path, Trx(Result("NotExecuted"), 1, 0, 0, 1));
        await Assert.That(DocumentationVerification.InspectSnippetExecutionReports(workspace.Root, 8, CancellationToken.None).State)
            .IsEqualTo(DocumentSnippetVerificationState.Skipped);
        await File.WriteAllTextAsync(path, Trx("", 0, 0, 0, 0));
        await Assert.That(DocumentationVerification.InspectSnippetExecutionReports(workspace.Root, 8, CancellationToken.None).State)
            .IsEqualTo(DocumentSnippetVerificationState.NotRun);
        await File.WriteAllTextAsync(path, Trx(Result("Passed"), 1, 1, 1, 0));
        await Assert.That(DocumentationVerification.InspectSnippetExecutionReports(workspace.Root, 8, CancellationToken.None).State)
            .IsEqualTo(DocumentSnippetVerificationState.NotRun);

        DocumentationVerification.SnippetExecutionSummary Inspect() =>
            DocumentationVerification.InspectSnippetExecutionReports(workspace.Root, 0, CancellationToken.None);
    }

    [Test]
    public async Task EveryReportedRunMustBeAvailableAndMixedSkippedSelectionCannotClaimFullVerification()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one.trx"), Trx(Result("Passed"), 1, 1, 1, 0));
        var second = Path.Combine(workspace.Root, "two.trx");
        await File.WriteAllTextAsync(second, Trx(Result("Passed", "second"), 1, 1, 1, 0));
        await Assert.That(Inspect().State).IsEqualTo(DocumentSnippetVerificationState.Success);
        await File.WriteAllTextAsync(second, Trx(Result("NotExecuted", "second"), 1, 0, 0, 1));
        await Assert.That(Inspect().State).IsEqualTo(DocumentSnippetVerificationState.Skipped);
        await File.WriteAllTextAsync(second, Trx("", 0, 0, 0, 0));
        await Assert.That(Inspect().State).IsEqualTo(DocumentSnippetVerificationState.NotRun);
        await File.WriteAllTextAsync(second, "<not-a-report />");
        await Assert.That(Inspect().State).IsEqualTo(DocumentSnippetVerificationState.NotRun);

        DocumentationVerification.SnippetExecutionSummary Inspect() =>
            DocumentationVerification.InspectSnippetExecutionReports(workspace.Root, 0, CancellationToken.None);
    }

    [Test]
    public async Task ReportReaderRejectsExternalEntitiesAndHonorsCancellation()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "run.trx"), """
            <!DOCTYPE TestRun [<!ENTITY external SYSTEM "file:///unavailable-snippet-evidence">]>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">&external;</TestRun>
            """);
        var unavailable = DocumentationVerification.InspectSnippetExecutionReports(workspace.Root, 0, CancellationToken.None);
        await Assert.That(unavailable.State).IsEqualTo(DocumentSnippetVerificationState.NotRun);
        await Assert.That(unavailable.Message).Contains("TRX");
        await Assert.That(() => DocumentationVerification.InspectSnippetExecutionReports(workspace.Root, 0, new CancellationToken(true)))
            .Throws<OperationCanceledException>();
    }

    private static string Result(string outcome, string execution = "one") =>
        $"<UnitTestResult executionId=\"{execution}\" outcome=\"{outcome}\" />";

    private static string Trx(string results, long total, long executed, long passed, long skipped, long failed = 0) =>
        $"<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>{results}</Results>"
        + $"<ResultSummary outcome=\"Completed\"><Counters total=\"{total}\" executed=\"{executed}\" passed=\"{passed}\" notExecuted=\"{skipped}\" failed=\"{failed}\" /></ResultSummary></TestRun>";

    [Test]
    public async Task InspectionTracksOriginalFenceIncludeAndDeclaredInputsWithoutRunningProject()
    {
        using var workspace = new TemporaryWorkspace();
        var document = Path.Combine(workspace.Root, "guide.md");
        var code = Path.Combine(workspace.Root, "Code.cs");
        var project = Path.Combine(workspace.Root, "Example.csproj");
        await File.WriteAllTextAsync(document,
            "\uFEFF---\r\ntitle: Example\r\n---\r\n\r\n```csharp snippet=\"sum\" source=\"Code.cs\" region=\"Example\"\r\n```\r\n");
        await File.WriteAllTextAsync(code, "#region Example\nreturn 2 + 3;\n#endregion\n");
        // Invalid project XML proves inspection never evaluates or executes it.
        await File.WriteAllTextAsync(project, "This project must never be evaluated during inspection.");
        var snippet = new DocumentSnippet("sum", "guide.md", "Example.csproj");
        var dependencies = new[] { ContentDependency.FromFile("Code.cs"), ContentDependency.FromValue("test-contract", "1") };
        var first = (await DocumentationVerification.InspectSnippetsAsync(workspace.Root, [snippet], dependencies, "Release")).Single();
        await Assert.That(first.State).IsEqualTo(DocumentSnippetVerificationState.NotRun);
        await Assert.That(first.ExecutedAt).IsNull();
        await Assert.That(first.Location!.Line).IsEqualTo(5);
        await Assert.That(first.Location.EndLine).IsNotNull();
        await Assert.That(first.Location.EndLine!.Value).IsGreaterThan(5);
        await Assert.That(first.RelatedLocations.Single().FilePath).IsEqualTo("Code.cs");
        await Assert.That(first.Fingerprint!.Length).IsEqualTo(64);

        await File.WriteAllTextAsync(code, "#region Example\nreturn 2 - 3;\n#endregion\n");
        var changed = (await DocumentationVerification.InspectSnippetsAsync(workspace.Root, [snippet], dependencies,
            "Release", [first])).Single();
        await Assert.That(changed.Fingerprint).IsNotEqualTo(first.Fingerprint);
        await Assert.That(changed.State).IsEqualTo(DocumentSnippetVerificationState.NotRun);
        await Assert.That(changed.Message).Contains("changed");
        var configured = (await DocumentationVerification.InspectSnippetsAsync(workspace.Root, [snippet],
            [ContentDependency.FromFile("Code.cs"), ContentDependency.FromValue("test-contract", "2")], "Debug")).Single();
        await Assert.That(configured.Fingerprint).IsNotEqualTo(changed.Fingerprint);
        var mapped = new DocumentSnippet("sum", "guide.md", "Example.csproj")
        { ApiReferences = [new("api", "v1", "en", "M:Arithmetic.Add(System.Int32,System.Int32)")] };
        var referenced = (await DocumentationVerification.InspectSnippetsAsync(workspace.Root, [mapped], dependencies,
            "Release", [changed])).Single();
        await Assert.That(referenced.Fingerprint).IsNotEqualTo(changed.Fingerprint);
        await Assert.That(referenced.State).IsEqualTo(DocumentSnippetVerificationState.NotRun);
        await Assert.That(referenced.Message).Contains("changed");
    }

    [Test]
    public async Task ExplicitSkipMissingFenceAndDuplicateIdRemainDifferentFromSuccess()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "guide.md"), "```csharp snippet=\"one\"\nvalue\n```\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "Example.csproj"), "not a project");
        var skipped = new DocumentSnippet("one", "guide.md", "Example.csproj", skipReason: "Requires a device.");
        var absent = new DocumentSnippet("absent", "guide.md", "Example.csproj");
        var results = await DocumentationVerification.InspectSnippetsAsync(workspace.Root, [skipped, absent], [], "Release");
        await Assert.That(results[0].State).IsEqualTo(DocumentSnippetVerificationState.Skipped);
        await Assert.That(results[0].Message).IsEqualTo("Requires a device.");
        await Assert.That(results[1].State).IsEqualTo(DocumentSnippetVerificationState.OutOfScope);
        await Assert.That(results.All(static result => result.ExecutedAt is null)).IsTrue();
        await File.AppendAllTextAsync(Path.Combine(workspace.Root, "guide.md"), "```csharp snippet=\"one\"\nsecond\n```\n");
        var ambiguous = (await DocumentationVerification.InspectSnippetsAsync(workspace.Root, [skipped], [], "Release")).Single();
        await Assert.That(ambiguous.State).IsEqualTo(DocumentSnippetVerificationState.OutOfScope);
    }
}
