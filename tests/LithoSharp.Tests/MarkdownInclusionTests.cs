using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

/// <summary>C07 code inclusion: file/region references resolve at load time with
/// missing-file/region diagnostics, and target changes invalidate dependents.</summary>
public sealed class MarkdownInclusionTests
{
    private static async Task WriteAsync(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private const string DemoSource = """
        // header
        #region main
        var answer = 42;
        #endregion
        // footer
        """;

    private const string PostWithIncludes = """
        ---
        title: "Include Post"
        date: "2026-01-02T03:04:05Z"
        summary: "inclusion post"
        tags:
          - test
        ---

        # Demo

        ```csharp source="./snippets/demo.cs" region="main"
        ```

        ```text source="./snippets/demo.cs"
        ```
        """;

    [Test]
    public async Task RegionExtraction_MatchesWorkerSemantics()
    {
        await Assert.That(MarkdownCodeInclusion.ExtractRegion(DemoSource, "main"))
            .IsEqualTo("var answer = 42;");
        await Assert.That(MarkdownCodeInclusion.ExtractRegion(DemoSource, null!))
            .IsEqualTo(DemoSource);
    }

    [Test]
    public async Task RegionExtraction_RejectsMissingAndUnterminated()
    {
        await Assert.That(() => MarkdownCodeInclusion.ExtractRegion(DemoSource, "absent"))
            .Throws<InvalidOperationException>();
        await Assert.That(() => MarkdownCodeInclusion.ExtractRegion("#region open\nbody\n", "open"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RegionExtraction_SupportsNesting()
    {
        const string nested = "#region outer\na\n#region inner\nb\n#endregion\nc\n#endregion";
        await Assert.That(MarkdownCodeInclusion.ExtractRegion(nested, "outer"))
            .IsEqualTo("a\n#region inner\nb\n#endregion\nc");
        await Assert.That(MarkdownCodeInclusion.ExtractRegion(nested, "inner"))
            .IsEqualTo("b");
    }

    [Test]
    [Arguments("var answer = 42;", "")]
    [Arguments("#region main\nvar answer = 42;\n#endregion", " region=\"main\"")]
    public async Task Inclusion_PreservesClosingFenceAndFollowingText(string target, string region)
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "demo.cs", target);
        var body = "```csharp source=\"./demo.cs\"" + region + "\n```\n\n## After\nVisible text\n";
        var result = await MarkdownCodeInclusion.ResolveAsync(body, body, workspace.Root, workspace.Root, "entry.md");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(new LithoMarkdownCompiler().Compile(result.Body).Html)
            .IsEqualTo("<pre><code class=\"language-csharp\">var answer = 42;\n</code></pre>\n<h2 id=\"after\">After</h2>\n<p>Visible text</p>\n");
    }

    [Test]
    public async Task Inclusion_LeavesExamplesInsideNormalFencesUnchanged()
    {
        using var workspace = new TemporaryWorkspace();
        const string body = "````markdown\n```cs source=\"./missing.cs\"\n```\n````\n";
        var result = await MarkdownCodeInclusion.ResolveAsync(body, body, workspace.Root, workspace.Root, "entry.md");

        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Resolved).IsEmpty();
        await Assert.That(result.Body).IsEqualTo(body);
    }

    [Test]
    public async Task PostReader_ResolvesInclusions()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        await WriteAsync(content, "post.md", PostWithIncludes);
        await WriteAsync(content, "snippets/demo.cs", DemoSource);

        var posts = await new MarkdownPostReader().ReadAllAsync(content);

        await Assert.That(posts.Single().MarkdownBody).Contains("var answer = 42;");
        await Assert.That(posts.Single().MarkdownBody).Contains("// header");
        // Fence lines (with source attributes) are preserved; only bodies resolve.
        await Assert.That(posts.Single().MarkdownBody).Contains("```csharp source=");
    }

    [Test]
    public async Task PostReader_RejectsMissingFile()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        await WriteAsync(content, "post.md", PostWithIncludes.Replace(
            "./snippets/demo.cs", "./snippets/absent.cs"));

        await Assert.That(async () => await new MarkdownPostReader().ReadAllAsync(content))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("LSM011");
    }

    [Test]
    public async Task PostReader_RejectsMissingRegion()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        await WriteAsync(content, "post.md", PostWithIncludes.Replace(
            "region=\"main\"", "region=\"absent\""));
        await WriteAsync(content, "snippets/demo.cs", DemoSource);

        await Assert.That(async () => await new MarkdownPostReader().ReadAllAsync(content))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("LSM012");
    }

    [Test]
    public async Task PostReader_RejectsEscape()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        await WriteAsync(content, "post.md", PostWithIncludes.Replace(
            "./snippets/demo.cs", "../outside.cs"));

        await Assert.That(async () => await new MarkdownPostReader().ReadAllAsync(content))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TypedLoader_FoldsTargetsIntoFingerprint()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "entry.md", """
            ---
            title: Entry
            ---
            ```csharp source="./snippets/demo.cs" region="main"
            ```
            """);
        await WriteAsync(workspace.Root, "snippets/demo.cs", DemoSource);

        var first = await Loader<TypedEntry>(workspace.Root).LoadAsync();
        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(first.Collection!.Entries.Single().Body).Contains("var answer = 42;");
        var before = first.Collection.Entries.Single().SourceFingerprint;
        var beforeDependencies = first.Collection.Entries.Single().DeclaredDependencies;

        await WriteAsync(workspace.Root, "snippets/demo.cs", DemoSource.Replace("42", "43"));
        var second = await Loader<TypedEntry>(workspace.Root).LoadAsync();
        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(second.Collection!.Entries.Single().Body).Contains("var answer = 43;");
        // The source fingerprint stays the raw file hash (MDX integrity uses
        // it); target-only changes invalidate through declared dependencies.
        await Assert.That(second.Collection.Entries.Single().SourceFingerprint).IsEqualTo(before);
        await Assert.That(second.Collection.Entries.Single().DeclaredDependencies
            .Any(dependency => dependency.Kind == LithoSharp.Content.ContentDependencyKind.File
                && dependency.Key == "snippets/demo.cs")).IsTrue();
        await Assert.That(second.Collection.Entries.Single().DeclaredDependencies).IsNotEquivalentTo(beforeDependencies);
    }

    [Test]
    public async Task TypedLoader_ReportsMissingRegionWithLocation()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "entry.md", """
            ---
            title: Entry
            ---
            ```csharp source="./snippets/demo.cs" region="absent"
            ```
            """);
        await WriteAsync(workspace.Root, "snippets/demo.cs", DemoSource);

        var result = await Loader<TypedEntry>(workspace.Root).LoadAsync();

        await Assert.That(result.IsSuccess).IsFalse();
        var diagnostic = result.Diagnostics.Single(item => item.Id == MarkdownInclusionDiagnosticIds.MissingIncludeRegion);
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("entry.md");
        await Assert.That(diagnostic.Location.Line).IsEqualTo(4);
    }

    [Test]
    public async Task MdxLoader_PreservesInclusionBodyAndRawFingerprint()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "entry.mdx", """
            ---
            title: Entry
            ---
            ```csharp source="./snippets/demo.cs" region="main"
            ```
            """);
        await WriteAsync(workspace.Root, "snippets/demo.cs", DemoSource);

        var loader = new LithoSharp.Mdx.MdxContentCollectionLoader<TypedEntry>(
            new ContentCollectionId("tests"),
            workspace.Root,
            static entry => SiteRoute.ForFile(Path.ChangeExtension(entry.SourcePath, ".html")),
            static _ => new PageMetadata());
        var result = await loader.LoadAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        var entry = result.Collection!.Entries.Single();
        await Assert.That(entry.Body.Body).Contains("var answer = 42;");
        var raw = await File.ReadAllBytesAsync(Path.Combine(workspace.Root, "entry.mdx"));
        await Assert.That(entry.SourceFingerprint)
            .IsEqualTo("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(raw)));
        await Assert.That(entry.DeclaredDependencies
            .Any(dependency => dependency.Kind == LithoSharp.Content.ContentDependencyKind.File
                && dependency.Key == "snippets/demo.cs")).IsTrue();
        // The body offset points past the front matter in the original file.
        var original = await File.ReadAllTextAsync(Path.Combine(workspace.Root, "entry.mdx"));
        await Assert.That(entry.Body.BodyStartOffset).IsEqualTo(original.IndexOf("```csharp", StringComparison.Ordinal));
        await Assert.That(entry.Body.BodyStartLine).IsEqualTo(4);
    }

    [Test]
    public async Task Inclusion_SemanticsSpansReferToResolvedBody()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "demo.cs", "var answer = 42;\n");
        const string body = "```csharp source=\"./demo.cs\"\n```\n\n[link](https://example.org/a)\n";
        var result = await MarkdownCodeInclusion.ResolveAsync(body, body, workspace.Root, workspace.Root, "entry.md");

        await Assert.That(result.Diagnostics).IsEmpty();
        var analyzed = new LithoMarkdownCompiler().Analyze(result.Body);
        var link = analyzed.Semantics!.Links.Single(item => item.Url == "https://example.org/a");
        // Intentional divergence (recorded in the plan): spans after a splice
        // are resolved-body coordinates, not original-file offsets.
        await Assert.That(result.Body.Substring(link.Span.Start, link.Span.Length)).IsEqualTo(link.RawText);
        await Assert.That(link.Span.Start).IsEqualTo(result.Body.IndexOf("[link]", StringComparison.Ordinal));
        await Assert.That(link.Span.Start).IsNotEqualTo(body.IndexOf("[link]", StringComparison.Ordinal));
    }

    private static MarkdownContentCollectionLoader<T> Loader<T>(string root)
        where T : notnull, new() =>
        new(
            new ContentCollectionId("tests"),
            root,
            static entry => SiteRoute.ForFile(Path.ChangeExtension(entry.SourcePath, ".html")),
            static _ => new PageMetadata());

    internal sealed class TypedEntry
    {
        public TypedEntry()
        {
        }

        public string Title { get; init; } = string.Empty;
    }
}
