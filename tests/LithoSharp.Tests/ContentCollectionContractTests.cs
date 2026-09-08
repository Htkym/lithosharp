using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class ContentCollectionContractTests
{
    [Test]
    public async Task Identifiers_UseNormalizedOrdinalEquality()
    {
        var collection = new ContentCollectionId("docs:caf\u00E9");
        var equivalentCollection = new ContentCollectionId("docs:cafe\u0301");
        var entry = new ContentEntryId("page:caf\u00E9");
        var equivalentEntry = new ContentEntryId("page:cafe\u0301");
        var layout = new ContentLayoutId("layout:caf\u00E9");
        var equivalentLayout = new ContentLayoutId("layout:cafe\u0301");

        await Assert.That(collection.Equals(equivalentCollection)).IsTrue();
        await Assert.That(collection.GetHashCode()).IsEqualTo(equivalentCollection.GetHashCode());
        await Assert.That(entry.Equals(equivalentEntry)).IsTrue();
        await Assert.That(entry.GetHashCode()).IsEqualTo(equivalentEntry.GetHashCode());
        await Assert.That(layout.Equals(equivalentLayout)).IsTrue();
        await Assert.That(layout.GetHashCode()).IsEqualTo(equivalentLayout.GetHashCode());
        await Assert.That(collection.Equals(new ContentCollectionId("docs:Caf\u00E9"))).IsFalse();
    }

    [Test]
    public async Task Entry_RetainsTypedValuesAndNormalizedSourceIdentity()
    {
        var frontMatter = new TestFrontMatter(2, "Guide");
        var body = new TestBody("Body");
        var location = new SiteSourceLocation("content/guide.md", 4, 2);

        var entry = new ContentEntry<TestFrontMatter, TestBody>(
            new ContentEntryId("guide"),
            "guides\\cafe\u0301.md",
            "sha256:cafe\u0301",
            frontMatter,
            body,
            location);

        await Assert.That(entry.SourcePath).IsEqualTo("guides/caf\u00E9.md");
        await Assert.That(entry.SourceFingerprint).IsEqualTo("sha256:caf\u00E9");
        await Assert.That(entry.FrontMatter).IsSameReferenceAs(frontMatter);
        await Assert.That(entry.Body).IsSameReferenceAs(body);
        await Assert.That(entry.SourceLocation).IsSameReferenceAs(location);
    }

    [Test]
    public async Task Collection_DefensivelyCopiesAndOrdersEntriesDeterministically()
    {
        var source = new[]
        {
            Entry("z", 1),
            Entry("a", 1),
            Entry("m", 0),
        };
        var dependencies = new[]
        {
            ContentDependency.FromFile("shared/z.json"),
            ContentDependency.FromValue("locale", "ja"),
            ContentDependency.FromFile(@"shared\a.json"),
        };

        var collection = Collection(
            source,
            orderingComparer: Comparer<ContentEntry<TestFrontMatter, TestBody>>.Create(
                static (left, right) => left.FrontMatter.Order.CompareTo(right.FrontMatter.Order)),
            dependencies: dependencies);

        source[0] = Entry("changed", 9);
        dependencies[0] = ContentDependency.FromValue("changed", "value");

        await Assert.That(collection.Entries.Select(static entry => entry.Id.Value)
            .SequenceEqual(["m", "a", "z"])).IsTrue();
        await Assert.That(collection.DeclaredDependencies.Select(static dependency => dependency.Key)
            .SequenceEqual(["locale", "shared/a.json", "shared/z.json"])).IsTrue();
        await Assert.That(collection.Entries).IsAssignableTo<IReadOnlyList<ContentEntry<TestFrontMatter, TestBody>>>();
        await Assert.That(collection.DeclaredDependencies).IsAssignableTo<IReadOnlyList<ContentDependency>>();
    }

    [Test]
    public async Task Loader_ReceivesCancellationAndReturnsDiagnostics()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        IContentCollectionLoader<TestFrontMatter, TestBody> loader = new CancelAwareLoader();

        await Assert.That(async () => await loader.LoadAsync(cancellation.Token))
            .Throws<OperationCanceledException>();

        var location = new SiteSourceLocation("content/broken.md", 7, 3);
        var diagnostics = new[]
        {
            new SiteDiagnostic(
                "LSC002",
                SiteDiagnosticSeverity.Error,
                "Second invalid input.",
                new SiteSourceLocation("content/z.md", 1, 1)),
            new SiteDiagnostic("LSC001", SiteDiagnosticSeverity.Error, "Invalid input.", location),
        };
        var result = ContentLoadResult<TestFrontMatter, TestBody>.Failure(diagnostics);
        diagnostics[0] = new SiteDiagnostic("changed", SiteDiagnosticSeverity.Info, "Changed.");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Collection).IsNull();
        await Assert.That(result.Diagnostics[0].Location).IsSameReferenceAs(location);
        await Assert.That(result.Diagnostics[0].Location!.Line).IsEqualTo(7);
        await Assert.That(result.Diagnostics[0].Location!.Column).IsEqualTo(3);
        await Assert.That(result.Diagnostics.Select(static diagnostic => diagnostic.Id)
            .SequenceEqual(["LSC001", "LSC002"])).IsTrue();
    }

    [Test]
    public async Task Binder_IsFormatIndependentAndRetainsTypedResult()
    {
        IContentFrontMatterBinder<TestFrontMatter> binder = new TestBinder();
        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
        {
            ["order"] = 3,
            ["title"] = "Reference",
        };

        var result = binder.Bind(values, new SiteSourceLocation("content/reference.data", 1, 1));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value!.Order).IsEqualTo(3);
        await Assert.That(result.Value.Title).IsEqualTo("Reference");
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Dependencies_PreserveValuesNormalizeKeysAndCachePolicyIsExplicit()
    {
        var first = ContentDependency.FromValue("locale:cafe\u0301", "ja\u0301");
        var second = ContentDependency.FromValue("locale:caf\u00E9", "j\u00E1");
        var collection = Collection(
            [Entry("entry", 0)],
            dependencies: [ContentDependency.FromFile(@"shared\data.json"), first, second]);

        await Assert.That(first.Key).IsEqualTo(second.Key);
        await Assert.That(first.Value).IsEqualTo("ja\u0301");
        await Assert.That(second.Value).IsEqualTo("j\u00E1");
        await Assert.That(first.Equals(second)).IsFalse();
        await Assert.That(collection.DeclaredDependencies.Count).IsEqualTo(3);
        await Assert.That(collection.DeclaredDependencies[0].Key).IsEqualTo("locale:café");
        await Assert.That(collection.DeclaredDependencies[2].Key).IsEqualTo("shared/data.json");
        await Assert.That(collection.IsCacheable).IsFalse();
    }

    [Test]
    public async Task Collection_IsNotCacheableByDefault()
    {
        var collection = Collection([Entry("entry", 0)]);

        await Assert.That(collection.IsCacheable).IsFalse();
        await Assert.That(collection.TransformationId).IsNull();
    }

    [Test]
    public async Task Collection_AllowsExplicitCacheabilityWithStableTransformationIdentity()
    {
        var transformationId = new ContentTransformationId("docs-routing:v1");
        var collection = Collection(
            [Entry("entry", 0)],
            dependencies: [ContentDependency.FromValue("site-base-path", "/docs/")],
            transformationId: transformationId,
            isCacheable: true);

        await Assert.That(collection.IsCacheable).IsTrue();
        await Assert.That(collection.TransformationId).IsSameReferenceAs(transformationId);
        await Assert.That(collection.DeclaredDependencies.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Collection_RejectsUnsafeCacheabilityWithoutTransformationIdentity()
    {
        await Assert.That(() => Collection(
            [Entry("entry", 0)],
            dependencies: [ContentDependency.FromValue("site-base-path", "/docs/")],
            isCacheable: true)).Throws<ArgumentException>();
    }

    [Test]
    public async Task InvalidConfiguration_ThrowsSpecificExceptions()
    {
        var entries = new[] { Entry("same", 0), Entry("same", 1) };

        await Assert.That(() => Collection(entries)).Throws<ArgumentException>();
        await Assert.That(() => Collection([Entry("entry", 0)], inputRoot: " "))
            .Throws<ArgumentException>();
        await Assert.That(() => new ContentEntry<TestFrontMatter, TestBody>(
            new ContentEntryId("entry"),
            "../outside.md",
            "fingerprint",
            new TestFrontMatter(0, "Title"),
            new TestBody("Body"))).Throws<ArgumentException>();
        await Assert.That(() => ContentParseResult<TestFrontMatter>.Failure(
            [new SiteDiagnostic("LSC002", SiteDiagnosticSeverity.Warning, "Warning.")]))
            .Throws<ArgumentException>();
        await Assert.That(() => ContentLoadResult<TestFrontMatter, TestBody>.Success(
            Collection([Entry("entry", 0)]),
            [new SiteDiagnostic("LSC003", SiteDiagnosticSeverity.Error, "Error.")]))
            .Throws<ArgumentException>();
    }

    private static ContentEntry<TestFrontMatter, TestBody> Entry(string id, int order) =>
        new(
            new ContentEntryId(id),
            $"{id}.md",
            $"sha256:{id}",
            new TestFrontMatter(order, id),
            new TestBody(id));

    private static ContentCollection<TestFrontMatter, TestBody> Collection(
        IEnumerable<ContentEntry<TestFrontMatter, TestBody>> entries,
        string inputRoot = "content",
        IComparer<ContentEntry<TestFrontMatter, TestBody>>? orderingComparer = null,
        IEnumerable<ContentDependency>? dependencies = null,
        ContentTransformationId? transformationId = null,
        bool isCacheable = false) =>
        new(
            new ContentCollectionId("docs"),
            inputRoot,
            entries,
            static entry => SiteRoute.ForDirectoryIndex(entry.Id.Value),
            static entry => new PageMetadata(title: entry.FrontMatter.Title),
            new ContentLayoutId("docs"),
            orderingComparer,
            dependencies,
            transformationId,
            isCacheable);

    private sealed class CancelAwareLoader : IContentCollectionLoader<TestFrontMatter, TestBody>
    {
        public ValueTask<ContentLoadResult<TestFrontMatter, TestBody>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                ContentLoadResult<TestFrontMatter, TestBody>.Success(
                    Collection([Entry("entry", 0)])));
        }
    }

    private sealed class TestBinder : IContentFrontMatterBinder<TestFrontMatter>
    {
        public ContentParseResult<TestFrontMatter> Bind(
            IReadOnlyDictionary<string, object?> values,
            SiteSourceLocation? sourceLocation = null) =>
            ContentParseResult<TestFrontMatter>.Success(
                new TestFrontMatter((int)values["order"]!, (string)values["title"]!));
    }

    private sealed record TestFrontMatter(int Order, string Title);

    private sealed record TestBody(string Value);
}
