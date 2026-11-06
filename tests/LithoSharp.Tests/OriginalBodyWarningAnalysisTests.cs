using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Routing;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class OriginalBodyWarningAnalysisTests
{
    [Test]
    [Arguments("\n", "")]
    [Arguments("\r\n", "\uFEFF")]
    public async Task EagerWarningsKeepOriginalPositionsAcrossIncludesAndTrim(string newline, string bom)
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "post.md");
        var body = string.Join(newline,
            "", "```text source=\"./snippet.txt\"", "placeholder", "```", "",
            "😀 See [^note] and $x$.", "");
        var raw = Header(newline, bom) + body;
        await File.WriteAllTextAsync(path, raw);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "snippet.txt"),
            string.Join(newline, Enumerable.Repeat("Included code [^literal] $literal$", 20)));

        var post = await new MarkdownPostReader().ReadAsync(path, workspace.Root);
        var collection = await Loader(workspace.Root).LoadAsync();

        await Assert.That(collection.IsSuccess).IsTrue();
        await Assert.That(post.MarkdownBody).DoesNotContain("placeholder");
        await Assert.That(post.MarkdownBody.StartsWith("```", StringComparison.Ordinal)).IsTrue();
        await Assert.That(post.MarkdownBody.EndsWith(newline, StringComparison.Ordinal)).IsFalse();
        await Assert.That(collection.Collection!.Entries.Single().Body).DoesNotContain("placeholder");
        await Assert.That(post.BodyAnalysis).IsNull();
        await Assert.That(post.BodySource).IsNull();
        await Assert.That(collection.Collection.Entries.Single().BodyAnalysis).IsNull();
        await Assert.That(collection.Collection.Entries.Single().BodySource).IsNull();
        await AssertOriginalWarningsAsync(post.CompilerDiagnostics!, raw, path);
        await AssertOriginalWarningsAsync(collection.Diagnostics, raw, "post.md");
    }

    [Test]
    [Arguments("```md\n[^ignored]\n$x$\n```\n")]
    [Arguments("`[^ignored] $x$`\n")]
    [Arguments("\\[^ignored]\n")]
    [Arguments("<span data-note=\"[^ignored]\">text</span>\n")]
    [Arguments("[text](target \"[^ignored]\")\n")]
    public async Task EagerWarningsUseTypedCandidateAndBlockExclusions(string body)
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "post.md");
        await File.WriteAllTextAsync(path, Header("\n", "") + body);

        var post = await new MarkdownPostReader().ReadAsync(path, workspace.Root);
        var collection = await Loader(workspace.Root).LoadAsync();

        await Assert.That(post.CompilerDiagnostics!).IsEmpty();
        await Assert.That(collection.IsSuccess).IsTrue();
        await Assert.That(collection.Diagnostics).IsEmpty();
    }

    [Test]
    [Arguments("> Quote $x$.\n\n- List $y$.\n\n```mermaid title=\"sample\"\ngraph TD; A-->B\n```\n", "$x$")]
    [Arguments("- List $y$.\n", "$y$")]
    [Arguments("```mermaid title=\"sample\"\ngraph TD; A-->B\n```\n", "```mermaid")]
    public async Task EagerBrowserWarningUsesTheEarliestTypedNestedBlock(string body, string marker)
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "post.md");
        var raw = Header("\n", "") + body;
        await File.WriteAllTextAsync(path, raw);

        var post = await new MarkdownPostReader().ReadAsync(path, workspace.Root);
        var collection = await Loader(workspace.Root).LoadAsync();

        await Assert.That(collection.IsSuccess).IsTrue();
        await AssertDiagnosticPositionAsync(post.CompilerDiagnostics!.Single(), raw, path, marker);
        await AssertDiagnosticPositionAsync(collection.Diagnostics.Single(), raw, "post.md", marker);
        await Assert.That(post.CompilerDiagnostics!.Single().Id).IsEqualTo(LithoLimits.BrowserAssetDiagnosticId);
    }

    [Test]
    public async Task LargeOriginalBodyCanShrinkBeforeStrictCompilerAnalysis()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "post.md");
        var body = "```text source=\"./snippet.txt\"\n" + new string('x', 1024 * 1024 + 1)
            + "\n```\n\nSee [^note] and $x$.\n";
        var raw = Header("\n", "") + body;
        await File.WriteAllTextAsync(path, raw);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "snippet.txt"), "small");

        var post = await new MarkdownPostReader().ReadAsync(path, workspace.Root);
        var collection = await Loader(workspace.Root).LoadAsync();

        await Assert.That(collection.IsSuccess).IsTrue();
        await Assert.That(post.MarkdownBody.Length < 1024).IsTrue();
        await Assert.That(collection.Collection!.Entries.Single().Body.Length < 1024).IsTrue();
        await Assert.That(post.BodyAnalysis).IsNull();
        await Assert.That(collection.Collection.Entries.Single().BodyAnalysis).IsNull();
        await AssertOriginalWarningsAsync(post.CompilerDiagnostics!, raw, path);
        await AssertOriginalWarningsAsync(collection.Diagnostics, raw, "post.md");
    }

    [Test]
    public async Task LargeUnchangedOriginalDoesNotInheritTheStrictCompilerInputLimit()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "post.md");
        var body = new string('x', 1024 * 1024 + 1);
        await File.WriteAllTextAsync(path, Header("\n", "") + body);

        var post = await new MarkdownPostReader().ReadAsync(path, workspace.Root);
        var collection = await Loader(workspace.Root).LoadAsync();

        await Assert.That(post.MarkdownBody).IsEqualTo(body);
        await Assert.That(post.CompilerDiagnostics!).IsEmpty();
        await Assert.That(collection.IsSuccess).IsTrue();
        await Assert.That(collection.Collection!.Entries.Single().Body).IsEqualTo(body);
        await Assert.That(collection.Diagnostics).IsEmpty();
        await Assert.That(post.BodyAnalysis).IsNull();
        await Assert.That(collection.Collection.Entries.Single().BodyAnalysis).IsNull();
    }

    [Test]
    [Arguments("Body [^note] and $x$.", true)]
    [Arguments("\nBody [^note] and $x$.\n", false)]
    public async Task OnlyExactStrictBodiesRetainAnalysisAndOriginalSourceMapping(string body, bool readerCanReuse)
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "post.md");
        var header = Header("\n", "");
        await File.WriteAllTextAsync(path, header + body);

        var post = await new MarkdownPostReader().ReadAsync(path, workspace.Root);
        var collection = await Loader(workspace.Root).LoadAsync();
        await Assert.That(collection.IsSuccess).IsTrue();
        var entry = collection.Collection!.Entries.Single();

        await Assert.That(post.BodyAnalysis is not null).IsEqualTo(readerCanReuse);
        await Assert.That(post.BodySource is not null).IsEqualTo(readerCanReuse);
        await Assert.That(entry.BodyAnalysis).IsNotNull();
        var snapshot = entry.BodyAnalysis!;
        await Assert.That(snapshot.Analysis.RawText).IsEqualTo(body);
        await Assert.That(snapshot.Analysis.OptionsHash).IsEqualTo(MarkdownAnalysisAdapter.BodyOptionsHash);
        await Assert.That(entry.BodySource!.FilePath).IsEqualTo("post.md");
        await Assert.That(entry.BodySource.BodyStartOffset).IsEqualTo(header.Length);
        await Assert.That(entry.BodySource.BodyStartLine).IsEqualTo(5);
        if (readerCanReuse)
        {
            await Assert.That(post.BodySource!.FilePath).IsEqualTo(path);
            await Assert.That(post.BodySource.BodyStartOffset).IsEqualTo(header.Length);
            await Assert.That(post.BodySource.BodyStartLine).IsEqualTo(5);
        }

        await Assert.That(ReferenceEquals(snapshot,
            MarkdownAnalysisAdapter.RetainForBody(snapshot, body, entry.SourcePath))).IsTrue();
        await Assert.That(MarkdownAnalysisAdapter.RetainForBody(snapshot, body + " ", entry.SourcePath)).IsNull();
        await Assert.That(MarkdownAnalysisAdapter.RetainForBody(snapshot, body, "other.md")).IsNull();
    }

    [Test]
    public async Task InvalidUnicodeOriginalRetainsItsKnownRawFailurePosition()
    {
        const string body = "\r\nKnown 😀 \uD800";
        var snapshot = MarkdownAnalysisAdapter.AnalyzeOriginalBody(body, "original.md", CancellationToken.None);
        var diagnostics = MarkdownAnalysisAdapter.BodyDiagnostics(snapshot, "original.md", bodyStartLine: 7);
        var diagnostic = diagnostics.Single();
        var (line, column) = new SourceText(body).GetLineAndColumn(body.Length - 1);

        await Assert.That(diagnostic.Id).IsEqualTo(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId);
        await Assert.That(diagnostic.Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("original.md");
        await Assert.That((diagnostic.Location.Line, diagnostic.Location.Column,
            diagnostic.Location.EndLine, diagnostic.Location.EndColumn))
            .IsEqualTo(((int?)(line + 6), (int?)column, (int?)(line + 6), (int?)(column + 1)));
        await Assert.That(ReferenceEquals(snapshot,
            MarkdownAnalysisAdapter.RetainForBody(snapshot, body, "original.md"))).IsTrue();
    }

    [Test]
    public async Task EagerWarningInputLimitIsUnavailableWithNoInventedPosition()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "post.md");
        await File.WriteAllTextAsync(path, Header("\n", "") + new string('x', 8 * 1024 * 1024 + 1));

        var post = await new MarkdownPostReader().ReadAsync(path, workspace.Root);
        var collection = await Loader(workspace.Root).LoadAsync();

        await AssertUnavailableAsync(post.CompilerDiagnostics!.Single(), path);
        await Assert.That(collection.IsSuccess).IsFalse();
        await Assert.That(collection.Collection).IsNull();
        await AssertUnavailableAsync(collection.Diagnostics.Single(), "post.md");
    }

    [Test]
    public async Task EagerWarningDepthLimitIsUnavailableInsteadOfSuccessfulAbsence()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "post.md");
        var body = string.Concat(Enumerable.Repeat("> ", 500)) + "deep\n";
        await File.WriteAllTextAsync(path, Header("\n", "") + body);

        var post = await new MarkdownPostReader().ReadAsync(path, workspace.Root);
        var collection = await Loader(workspace.Root).LoadAsync();

        await AssertUnavailableAsync(post.CompilerDiagnostics!.Single(), path);
        await Assert.That(collection.IsSuccess).IsFalse();
        await AssertUnavailableAsync(collection.Diagnostics.Single(), "post.md");
    }

    private static string Header(string newline, string bom) =>
        bom + string.Join(newline, "---", "title: Original", "date: 2026-01-02T03:04:05Z", "---", "");

    private static MarkdownContentCollectionLoader<WarningFrontMatter> Loader(string root) =>
        new(new ContentCollectionId("warnings"), root,
            static entry => SiteRoute.ForFile(Path.ChangeExtension(entry.SourcePath, ".html")),
            static _ => new PageMetadata());

    private static async Task AssertOriginalWarningsAsync(
        IReadOnlyList<SiteDiagnostic> diagnostics, string raw, string path)
    {
        await Assert.That(diagnostics.Count).IsEqualTo(2);
        await AssertDiagnosticPositionAsync(
            diagnostics.Single(diagnostic => diagnostic.Id == LithoLimits.UnsupportedFootnoteDiagnosticId), raw, path, "[^note]");
        await AssertDiagnosticPositionAsync(
            diagnostics.Single(diagnostic => diagnostic.Id == LithoLimits.BrowserAssetDiagnosticId), raw, path, "$x$");
    }

    private static async Task AssertDiagnosticPositionAsync(SiteDiagnostic diagnostic, string raw, string path, string marker)
    {
        var (line, column) = new SourceText(raw).GetLineAndColumn(raw.IndexOf(marker, StringComparison.Ordinal));
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo(path);
        await Assert.That((diagnostic.Location.Line, diagnostic.Location.Column)).IsEqualTo(((int?)line, (int?)column));
    }

    private static async Task AssertUnavailableAsync(SiteDiagnostic diagnostic, string path)
    {
        await Assert.That(diagnostic.Id).IsEqualTo(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId);
        await Assert.That(diagnostic.Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("analysis is incomplete");
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo(path);
        await Assert.That((diagnostic.Location.Line, diagnostic.Location.Column,
            diagnostic.Location.EndLine, diagnostic.Location.EndColumn))
            .IsEqualTo(((int?)null, (int?)null, (int?)null, (int?)null));
    }

    internal sealed class WarningFrontMatter
    {
        public WarningFrontMatter() { }

        public string Title { get; init; } = string.Empty;

        public DateTimeOffset Date { get; init; }
    }
}
