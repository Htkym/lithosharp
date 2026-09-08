using System.Globalization;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class MarkdownContentCollectionLoaderTests
{
    [Test]
    public async Task LoadAsync_BindsTypedValuesNullsDefaultsAndNestedCollections()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "typed.md", """
            ---
            title: Typed
            count: 42
            ratio: 1.25
            published: true
            published_on: 2026-08-30
            optional: null
            tags: [alpha, beta]
            details:
              label: Nested
            ---
            Body
            """);

        var result = await Loader<TypedFrontMatter>(workspace.Root).LoadAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        var frontMatter = result.Collection!.Entries.Single().FrontMatter;
        await Assert.That(frontMatter.Title).IsEqualTo("Typed");
        await Assert.That(frontMatter.Count).IsEqualTo(42);
        await Assert.That(frontMatter.Ratio).IsEqualTo(1.25m);
        await Assert.That(frontMatter.Published).IsTrue();
        await Assert.That(frontMatter.PublishedOn).IsEqualTo(new DateOnly(2026, 8, 30));
        await Assert.That(frontMatter.Optional).IsNull();
        await Assert.That(frontMatter.UnspecifiedDefault).IsEqualTo("kept");
        await Assert.That(frontMatter.Tags.SequenceEqual(["alpha", "beta"])).IsTrue();
        await Assert.That(frontMatter.Details.Label).IsEqualTo("Nested");
        await Assert.That(result.Collection.Entries.Single().Body).IsEqualTo("Body");
    }

    [Test]
    public async Task LoadAsync_RejectsUnknownFieldsAtTheirSourceLocationByDefault()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "unknown.md", """
            ---
            title: Known
            extra: rejected
            ---
            Body
            """);

        var result = await Loader<TypedFrontMatter>(workspace.Root).LoadAsync();

        await Assert.That(result.IsSuccess).IsFalse();
        var diagnostic = result.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo(ContentFrontMatterDiagnosticIds.UnknownField);
        await Assert.That(diagnostic.Message).Contains("extra");
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("unknown.md");
        await Assert.That(diagnostic.Location.Line).IsEqualTo(3);
        await Assert.That(diagnostic.Location.Column).IsEqualTo(1);
    }

    [Test]
    public async Task LoadAsync_RejectsDuplicateKeysAndAliases()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "duplicate.md", """
            ---
            title: First
            title: Second
            ---
            Body
            """);

        var duplicate = await Loader<TypedFrontMatter>(workspace.Root).LoadAsync();

        await Assert.That(duplicate.Diagnostics.Single().Id)
            .IsEqualTo(MarkdownContentDiagnosticIds.DuplicateKey);
        await Assert.That(duplicate.Diagnostics.Single().Location!.Line).IsEqualTo(3);

        File.Delete(Path.Combine(workspace.Root, "duplicate.md"));
        await WriteAsync(workspace.Root, "alias.md", """
            ---
            title: Value
            optional: *shared
            ---
            Body
            """);

        var alias = await Loader<TypedFrontMatter>(workspace.Root).LoadAsync();

        await Assert.That(alias.Diagnostics.Single().Id)
            .IsEqualTo(MarkdownContentDiagnosticIds.AliasNotAllowed);
        await Assert.That(alias.Diagnostics.Single().Location!.Line).IsEqualTo(3);
    }

    [Test]
    public async Task LoadAsync_RespectsNullabilityAndReportsMalformedYamlPosition()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "null.md", """
            ---
            title: null
            ---
            Body
            """);

        var nullResult = await Loader<TypedFrontMatter>(workspace.Root).LoadAsync();

        await Assert.That(nullResult.Diagnostics.Single().Id)
            .IsEqualTo(ContentFrontMatterDiagnosticIds.NullNotAllowed);
        await Assert.That(nullResult.Diagnostics.Single().Location!.Line).IsEqualTo(2);

        File.Delete(Path.Combine(workspace.Root, "null.md"));
        await WriteAsync(workspace.Root, "malformed.md", """
            ---
            title: [broken
            ---
            Body
            """);

        var malformed = await Loader<TypedFrontMatter>(workspace.Root).LoadAsync();

        var malformedDiagnostic = malformed.Diagnostics.Single();
        await Assert.That(malformedDiagnostic.Id)
            .IsEqualTo(MarkdownContentDiagnosticIds.InvalidYaml);
        var malformedLocation = malformedDiagnostic.Location!;
        await Assert.That(malformedLocation.FilePath)
            .IsEqualTo("malformed.md");
        await Assert.That(malformedLocation.Line).IsNotNull();
        await Assert.That(malformedLocation.Column).IsNotNull();
    }

    [Test]
    public async Task LoadAsync_ReportsUnterminatedFrontMatter()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "unterminated.md", """
            ---
            title: Broken
            Body
            """);

        var result = await Loader<TypedFrontMatter>(workspace.Root).LoadAsync();

        var diagnostic = result.Diagnostics.Single();
        await Assert.That(diagnostic.Id)
            .IsEqualTo(MarkdownContentDiagnosticIds.UnterminatedFrontMatter);
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("unterminated.md");
        await Assert.That(diagnostic.Location.Line).IsEqualTo(4);
        await Assert.That(diagnostic.Location.Column).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    public async Task LoadAsync_UsesInvariantScalarConversion()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            using var workspace = new TemporaryWorkspace();
            await WriteAsync(workspace.Root, "scalar.md", """
                ---
                title: Scalar
                ratio: 1234.5
                ---
                Body
                """);

            var result = await Loader<TypedFrontMatter>(workspace.Root).LoadAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Collection!.Entries.Single().FrontMatter.Ratio)
                .IsEqualTo(1234.5m);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public async Task LoadAsync_OrdersNormalizedUnicodePathsAndHashesExactBytes()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "あ.md", Document("Unicode", "Body"));
        await WriteAsync(workspace.Root, "z.md", Document("Latin", "Body"));

        var first = await Loader<TypedFrontMatter>(workspace.Root).LoadAsync();

        await Assert.That(first.Collection!.Entries.Select(static entry => entry.SourcePath)
            .SequenceEqual(["z.md", "あ.md"])).IsTrue();
        var fingerprint = first.Collection.Entries[0].SourceFingerprint;

        await WriteAsync(workspace.Root, "z.md", Document("Latin", "Body "));
        var second = await Loader<TypedFrontMatter>(workspace.Root).LoadAsync();

        await Assert.That(second.Collection!.Entries[0].SourceFingerprint)
            .IsNotEqualTo(fingerprint);
    }

    [Test]
    public async Task LoadAsync_DiscoversUppercaseMarkdownExtension()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "UPPER.MD", Document("Upper", "Body"));

        var result = await Loader<TypedFrontMatter>(workspace.Root).LoadAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Collection!.Entries.Select(static entry => entry.SourcePath))
            .IsEquivalentTo(["UPPER.MD"]);
    }

    [Test]
    public async Task LoadAsync_HonorsCancellationBeforeFileIo()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "post.md", Document("Post", "Body"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.That(async () =>
                await Loader<TypedFrontMatter>(workspace.Root).LoadAsync(cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task LegacyReader_UsesTypedLoaderWithoutChangingSuccessfulOutput()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "post.md", """
            ---
            title: Legacy
            date: 2026-08-30T10:20:30+09:00
            summary: Summary
            tags: [one, two]
            sidebar_position: 4
            ---

            Body
            """);

        var post = await new MarkdownPostReader().ReadAsync(
            Path.Combine(workspace.Root, "post.md"),
            workspace.Root);

        await Assert.That(post.FrontMatter.Title).IsEqualTo("Legacy");
        await Assert.That(post.FrontMatter.SidebarPosition).IsEqualTo(4);
        await Assert.That(post.MarkdownBody).IsEqualTo("Body");
        await Assert.That(post.RelativeOutputPath).IsEqualTo("posts/post.html");
    }

    [Test]
    public async Task LegacyReader_IgnoresUnknownFrontMatterFields()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "post.md", """
            ---
            title: Legacy
            date: 2026-08-30T10:20:30+09:00
            plugin_specific_value: retained-for-legacy-compatibility
            ---
            Body
            """);

        var post = await new MarkdownPostReader().ReadAsync(
            Path.Combine(workspace.Root, "post.md"),
            workspace.Root);

        await Assert.That(post.FrontMatter.Title).IsEqualTo("Legacy");
    }

    [Test]
    public async Task LegacyReader_PreservesMissingFrontMatterMessage()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "post.md");
        await File.WriteAllTextAsync(path, "Body");

        await Assert.That(async () => await new MarkdownPostReader().ReadAsync(path, workspace.Root))
            .Throws<InvalidOperationException>()
            .WithMessageContaining(
                $"Markdown document '{path}' must start with YAML front matter.");
    }

    private static MarkdownContentCollectionLoader<T> Loader<T>(string root)
        where T : notnull =>
        new(
            new ContentCollectionId("tests"),
            root,
            static entry => SiteRoute.ForFile(Path.ChangeExtension(entry.SourcePath, ".html")),
            static _ => new PageMetadata());

    private static Task WriteAsync(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return File.WriteAllTextAsync(path, content);
    }

    private static string Document(string title, string body) => $"""
        ---
        title: {title}
        ---
        {body}
        """;

    internal sealed class TypedFrontMatter
    {
        public TypedFrontMatter()
        {
        }

        public string Title { get; init; } = string.Empty;

        public int Count { get; init; } = 7;

        public decimal Ratio { get; init; }

        public bool Published { get; init; }

        public DateOnly PublishedOn { get; init; }

        public string? Optional { get; init; } = "default";

        public string UnspecifiedDefault { get; init; } = "kept";

        public List<string> Tags { get; init; } = [];

        public NestedFrontMatter Details { get; init; } = new();
    }

    internal sealed class NestedFrontMatter
    {
        public NestedFrontMatter()
        {
        }

        public string Label { get; init; } = string.Empty;
    }
}
