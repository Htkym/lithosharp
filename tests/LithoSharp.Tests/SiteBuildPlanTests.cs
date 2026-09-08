using LithoSharp.Build;

namespace LithoSharp.Tests;

public sealed class SiteBuildPlanTests
{
    [Test]
    public async Task Identifiers_AreNormalizedAndUseOrdinalEquality()
    {
        var composedNode = new BuildNodeId("caf\u00e9");
        var decomposedNode = new BuildNodeId("cafe\u0301");
        var lowerArtifact = new BuildArtifactId("artifact");
        var upperArtifact = new BuildArtifactId("Artifact");

        await Assert.That(composedNode.Equals(decomposedNode)).IsTrue();
        await Assert.That(lowerArtifact.Equals(upperArtifact)).IsFalse();
        await Assert.That(() => new BuildNodeId(" node"))
            .Throws<ArgumentException>();
        await Assert.That(() => new BuildArtifactId(""))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Values_PreserveCodePointsAndRemainDistinctInPlanDescription()
    {
        const string decomposed = "e\u0301";
        const string composed = "\u00E9";
        var decomposedInput = BuildInput.FromValue("title", decomposed);
        var composedInput = BuildInput.FromValue("title", composed);
        var decomposedConfiguration = BuildInput.FromConfiguration("title", decomposed);
        var composedConfiguration = BuildInput.FromConfiguration("title", composed);

        await Assert.That(decomposedInput.Value).IsEqualTo(decomposed);
        await Assert.That(composedInput.Value).IsEqualTo(composed);
        await Assert.That(decomposedInput.Value).IsNotEqualTo(composedInput.Value);
        await Assert.That(decomposedConfiguration.Value).IsEqualTo(decomposed);
        await Assert.That(composedConfiguration.Value).IsEqualTo(composed);

        var decomposedPlan = SiteBuildPlan.Create(
            [new BuildNode(new BuildNodeId("page"), [decomposedInput])]);
        var composedPlan = SiteBuildPlan.Create(
            [new BuildNode(new BuildNodeId("page"), [composedInput])]);

        await Assert.That(Describe(decomposedPlan).Single())
            .IsNotEqualTo(Describe(composedPlan).Single());
    }

    [Test]
    public async Task FingerprintedFileAndCollectionInputs_PreservePayloadAndInvalidateSameKey()
    {
        const string before = "\0e\u0301\r\n";
        const string after = "\0\u00E9\r\n";
        var file = BuildInput.FromFile("content/post.md", before);
        var collection = BuildInput.FromCollection("posts", before);
        var previous = SiteBuildPlan.Create(
        [
            new BuildNode(new BuildNodeId("file"), [file]),
            new BuildNode(new BuildNodeId("collection"), [collection]),
        ]);
        var current = SiteBuildPlan.Create(
        [
            new BuildNode(
                new BuildNodeId("file"),
                [BuildInput.FromFile("content/post.md", after)]),
            new BuildNode(
                new BuildNodeId("collection"),
                [BuildInput.FromCollection("posts", after)]),
        ]);

        await Assert.That(file.Value).IsEqualTo(before);
        await Assert.That(collection.Value).IsEqualTo(before);
        await Assert.That(current.GetInvalidatedNodes(previous).Select(item => item.NodeId.Value))
            .IsEquivalentTo(["collection", "file"]);
        await Assert.That(() => BuildInput.FromFile("content/post.md", ""))
            .Throws<ArgumentException>();
        await Assert.That(() => BuildInput.FromCollection("posts", ""))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetInvalidatedNodes_ReturnsDeterministicDirectAndDependencyReasons()
    {
        var sourceId = new BuildNodeId("source");
        var dependentId = new BuildNodeId("dependent");
        var previous = SiteBuildPlan.Create(
        [
            new BuildNode(sourceId, [BuildInput.FromValue("body", "before")]),
            new BuildNode(dependentId, dependencies: [sourceId]),
        ]);
        var current = SiteBuildPlan.Create(
        [
            new BuildNode(dependentId, dependencies: [sourceId]),
            new BuildNode(sourceId, [BuildInput.FromValue("body", "after")]),
        ]);

        var invalidations = current.GetInvalidatedNodes(previous);

        await Assert.That(invalidations.Select(item => item.NodeId.Value))
            .IsEquivalentTo(["dependent", "source"]);
        await Assert.That(invalidations.Single(item => item.NodeId.Equals(sourceId)).Reasons)
            .IsEquivalentTo(["入力 'Value:body' が変更されました。"]);
        await Assert.That(invalidations.Single(item => item.NodeId.Equals(dependentId)).Reasons)
            .IsEquivalentTo(["依存ノード 'source' が無効化されました。"]);
    }

    [Test]
    public async Task GetInvalidatedNodes_NoChangeReturnsEmptyUnlessNodeAlwaysRebuilds()
    {
        var stable = SiteBuildPlan.Create([Node("stable")]);
        var opaque = SiteBuildPlan.Create(
        [
            new BuildNode(
                new BuildNodeId("opaque"),
                [BuildInput.FromValue("template.cachePolicy", "always-rebuild")]),
        ]);

        await Assert.That(stable.GetInvalidatedNodes(stable)).IsEmpty();
        await Assert.That(opaque.GetInvalidatedNodes(opaque).Single().Reasons)
            .IsEquivalentTo(
            [
                "入力 'Value:template.cachePolicy' が常時再ビルドを要求します。",
            ]);
    }

    [Test]
    public async Task Create_ExposesArtifactOwnerInputsAndDependencies()
    {
        var content = Node("content");
        var pageId = new BuildNodeId("page");
        var page = new BuildNode(
            pageId,
            [
                BuildInput.FromValue("body", "# Guide"),
                BuildInput.FromCollection("posts"),
                BuildInput.FromConfiguration("site.title", "Docs"),
                BuildInput.FromFile(@"content\guide.md"),
            ],
            [content.Id],
            [new BuildArtifact(new BuildArtifactId("page:guide"), pageId, @"guide\index.html")]);

        var plan = SiteBuildPlan.Create([page, content]);
        var owner = plan.GetArtifactOwner(new BuildArtifactId("page:guide"));

        await Assert.That(owner.Id.Value).IsEqualTo("page");
        await Assert.That(owner.Dependencies.Select(id => id.Value)).IsEquivalentTo(["content"]);
        await Assert.That(owner.Inputs.Select(input => input.Kind)).IsEquivalentTo(
        [
            BuildInputKind.Value,
            BuildInputKind.Collection,
            BuildInputKind.Configuration,
            BuildInputKind.File,
        ]);
        await Assert.That(plan.Artifacts[0].RelativeOutputPath).IsEqualTo("guide/index.html");
    }

    [Test]
    public async Task Validate_ReportsEveryStructuralDiagnosticAndCollectsAllErrors()
    {
        var duplicateId = new BuildNodeId("duplicate");
        var otherId = new BuildNodeId("other");
        var duplicatedArtifactId = new BuildArtifactId("artifact");
        var nodes = new[]
        {
            new BuildNode(
                duplicateId,
                dependencies: [new BuildNodeId("missing")],
                artifacts:
                [
                    new BuildArtifact(duplicatedArtifactId, otherId, "../outside.html"),
                ]),
            new BuildNode(
                duplicateId,
                artifacts:
                [
                    new BuildArtifact(duplicatedArtifactId, duplicateId, "valid.html"),
                ]),
        };

        var result = SiteBuildPlan.Validate(nodes);

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Plan).IsNull();
        await Assert.That(result.Diagnostics.All(
            diagnostic => diagnostic.Severity == Diagnostics.SiteDiagnosticSeverity.Error)).IsTrue();
        await Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Id)).IsEquivalentTo(
        [
            SiteBuildPlanDiagnosticIds.DuplicateNodeId,
            SiteBuildPlanDiagnosticIds.DuplicateArtifactId,
            SiteBuildPlanDiagnosticIds.InvalidArtifactPath,
            SiteBuildPlanDiagnosticIds.ArtifactOwnerMismatch,
            SiteBuildPlanDiagnosticIds.MissingDependency,
        ]);
        var exception = await Assert.That(() => SiteBuildPlan.Create(nodes))
            .Throws<SiteBuildPlanValidationException>();
        await Assert.That(exception!.Diagnostics.Count).IsEqualTo(result.Diagnostics.Count);
    }

    [Test]
    public async Task Validate_ReportsMissingDependency()
    {
        var result = SiteBuildPlan.Validate(
        [
            new BuildNode(new BuildNodeId("page"), dependencies: [new BuildNodeId("content")]),
        ]);

        await Assert.That(result.Diagnostics.Single().Id)
            .IsEqualTo(SiteBuildPlanDiagnosticIds.MissingDependency);
    }

    [Test]
    public async Task Validate_ReusedArtifactDeclarationCollectsAllApplicableDiagnostics()
    {
        var ownerId = new BuildNodeId("owner");
        var artifact = new BuildArtifact(new BuildArtifactId("artifact"), ownerId, "index.html");

        var result = SiteBuildPlan.Validate(
        [
            new BuildNode(ownerId, artifacts: [artifact, artifact]),
        ]);

        await Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Id)).IsEquivalentTo(
        [
            SiteBuildPlanDiagnosticIds.DuplicateArtifactId,
            SiteBuildPlanDiagnosticIds.DuplicateOutputPath,
        ]);
    }

    [Test]
    public async Task Validate_ReportsStableCyclePath()
    {
        var forward = SiteBuildPlan.Validate(CreateCycleNodes(reverse: false));
        var reverse = SiteBuildPlan.Validate(CreateCycleNodes(reverse: true));

        var diagnostic = forward.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo(SiteBuildPlanDiagnosticIds.DependencyCycle);
        await Assert.That(diagnostic.Message).Contains("'a' -> 'b' -> 'c' -> 'a'");
        await Assert.That(reverse.Diagnostics.Single().Message).IsEqualTo(diagnostic.Message);
    }

    [Test]
    public async Task Validate_ReportsSelfCycle()
    {
        var id = new BuildNodeId("self");

        var result = SiteBuildPlan.Validate([new BuildNode(id, dependencies: [id])]);

        await Assert.That(result.Diagnostics.Single().Id)
            .IsEqualTo(SiteBuildPlanDiagnosticIds.DependencyCycle);
        await Assert.That(result.Diagnostics.Single().Message).Contains("'self' -> 'self'");
    }

    [Test]
    public async Task Validate_HandlesLongAcyclicDependencyChain()
    {
        const int nodeCount = 25_000;
        var nodes = Enumerable.Range(0, nodeCount)
            .Select(index => new BuildNode(
                new BuildNodeId($"node:{index:D5}"),
                dependencies: index == 0
                    ? null
                    : [new BuildNodeId($"node:{index - 1:D5}")]))
            .Reverse()
            .ToArray();

        var result = SiteBuildPlan.Validate(nodes);

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Plan!.Nodes.Count).IsEqualTo(nodeCount);
    }

    [Test]
    public async Task Validate_HandlesLargeDependencyCycleWithDeterministicPath()
    {
        const int nodeCount = 25_000;
        var nodes = Enumerable.Range(0, nodeCount)
            .Select(index => new BuildNode(
                new BuildNodeId($"node:{index:D5}"),
                dependencies:
                [
                    new BuildNodeId($"node:{(index + 1) % nodeCount:D5}"),
                ]))
            .Reverse()
            .ToArray();

        var result = SiteBuildPlan.Validate(nodes);

        var diagnostic = result.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo(SiteBuildPlanDiagnosticIds.DependencyCycle);
        await Assert.That(diagnostic.Message).StartsWith(
            "ビルドノード依存関係に循環があります: 'node:00000' -> 'node:00001'");
        await Assert.That(diagnostic.Message).EndsWith(
            "'node:24998' -> 'node:24999' -> 'node:00000'。");
    }

    [Test]
    public async Task Validate_ReportsExactAndCaseInsensitiveOutputCollisions()
    {
        var first = new BuildNodeId("first");
        var second = new BuildNodeId("second");
        var third = new BuildNodeId("third");
        var result = SiteBuildPlan.Validate(
        [
            NodeWithArtifact(first, "first-artifact", "assets/search.json"),
            NodeWithArtifact(second, "second-artifact", "assets/search.json"),
            NodeWithArtifact(third, "third-artifact", "Assets/Search.json"),
        ]);

        await Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Id)).IsEquivalentTo(
        [
            SiteBuildPlanDiagnosticIds.DuplicateOutputPath,
            SiteBuildPlanDiagnosticIds.OutputPathCaseCollision,
            SiteBuildPlanDiagnosticIds.OutputPathCaseCollision,
        ]);
    }

    [Test]
    public async Task Validate_ReportsFileDirectoryAncestorConflictIgnoringCase()
    {
        var result = SiteBuildPlan.Validate(
        [
            NodeWithArtifact(new BuildNodeId("file"), "file-artifact", "Assets/Search"),
            NodeWithArtifact(new BuildNodeId("child"), "child-artifact", "assets/search/index.json"),
        ]);

        await Assert.That(result.Diagnostics.Single().Id)
            .IsEqualTo(SiteBuildPlanDiagnosticIds.OutputPathAncestorConflict);
    }

    [Test]
    public async Task Create_OrdersAllCollectionsOrdinallyRegardlessOfRegistrationOrder()
    {
        var forward = SiteBuildPlan.Create(CreateOrderedNodes(reverse: false));
        var reverse = SiteBuildPlan.Create(CreateOrderedNodes(reverse: true));

        await Assert.That(Describe(forward).SequenceEqual(Describe(reverse))).IsTrue();
        await Assert.That(forward.Nodes.Select(node => node.Id.Value)
            .SequenceEqual(["a", "m", "z"])).IsTrue();
        await Assert.That(forward.Artifacts.Select(artifact => artifact.Id.Value)
            .SequenceEqual(["artifact:a", "artifact:z"])).IsTrue();
        await Assert.That(forward.Nodes[2].Dependencies.Select(id => id.Value)
            .SequenceEqual(["a", "m"])).IsTrue();
        await Assert.That(forward.Nodes[2].Inputs
            .Select(input => $"{input.Kind}:{input.Key}:{input.Value}")
            .SequenceEqual(
            [
                "Value:a:value",
                "Configuration:z:value",
                "File:content/z.md:",
            ])).IsTrue();
    }

    [Test]
    public async Task Constructors_DefensivelyCopyInputCollections()
    {
        var dependencyIds = new List<BuildNodeId> { new("dependency") };
        var inputs = new List<BuildInput> { BuildInput.FromValue("body", "before") };
        var ownerId = new BuildNodeId("owner");
        var artifacts = new List<BuildArtifact>
        {
            new(new BuildArtifactId("artifact"), ownerId, "index.html"),
        };
        var node = new BuildNode(ownerId, inputs, dependencyIds, artifacts);
        var nodes = new List<BuildNode> { Node("dependency"), node };
        var plan = SiteBuildPlan.Create(nodes);

        dependencyIds.Clear();
        inputs.Clear();
        artifacts.Clear();
        nodes.Clear();

        var owner = plan.GetArtifactOwner(new BuildArtifactId("artifact"));
        await Assert.That(plan.Nodes.Count).IsEqualTo(2);
        await Assert.That(owner.Dependencies.Count).IsEqualTo(1);
        await Assert.That(owner.Inputs.Count).IsEqualTo(1);
        await Assert.That(owner.Artifacts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Validate_ReturnsDiagnosticsInDeterministicOrder()
    {
        var forward = SiteBuildPlan.Validate(CreateInvalidNodes(reverse: false));
        var reverse = SiteBuildPlan.Validate(CreateInvalidNodes(reverse: true));

        await Assert.That(forward.Diagnostics.Select(DescribeDiagnostic)
            .SequenceEqual(reverse.Diagnostics.Select(DescribeDiagnostic))).IsTrue();
    }

    private static BuildNode Node(string id) => new(new BuildNodeId(id));

    private static BuildNode NodeWithArtifact(
        BuildNodeId nodeId,
        string artifactId,
        string path) =>
        new(
            nodeId,
            artifacts: [new BuildArtifact(new BuildArtifactId(artifactId), nodeId, path)]);

    private static IEnumerable<BuildNode> CreateCycleNodes(bool reverse)
    {
        var nodes = new[]
        {
            new BuildNode(new BuildNodeId("a"), dependencies: [new BuildNodeId("b")]),
            new BuildNode(new BuildNodeId("b"), dependencies: [new BuildNodeId("c")]),
            new BuildNode(new BuildNodeId("c"), dependencies: [new BuildNodeId("a")]),
        };
        return reverse ? nodes.Reverse() : nodes;
    }

    private static IEnumerable<BuildNode> CreateOrderedNodes(bool reverse)
    {
        var a = new BuildNodeId("a");
        var m = new BuildNodeId("m");
        var z = new BuildNodeId("z");
        var nodes = new[]
        {
            new BuildNode(
                z,
                [
                    BuildInput.FromFile(@"content\z.md"),
                    BuildInput.FromConfiguration("z", "value"),
                    BuildInput.FromValue("a", "value"),
                ],
                [m, a],
                [new BuildArtifact(new BuildArtifactId("artifact:z"), z, "z.html")]),
            new BuildNode(
                a,
                artifacts: [new BuildArtifact(new BuildArtifactId("artifact:a"), a, "a.html")]),
            new BuildNode(m),
        };
        return reverse ? nodes.Reverse() : nodes;
    }

    private static IEnumerable<BuildNode> CreateInvalidNodes(bool reverse)
    {
        var first = new BuildNodeId("first");
        var second = new BuildNodeId("second");
        var nodes = new[]
        {
            new BuildNode(
                first,
                dependencies: [new BuildNodeId("missing")],
                artifacts:
                [
                    new BuildArtifact(new BuildArtifactId("z"), first, "Same.html"),
                ]),
            new BuildNode(
                second,
                artifacts:
                [
                    new BuildArtifact(new BuildArtifactId("a"), second, "same.html"),
                ]),
        };
        return reverse ? nodes.Reverse() : nodes;
    }

    private static IEnumerable<string> Describe(SiteBuildPlan plan) =>
        plan.Nodes.Select(node =>
            $"{node.Id}|{string.Join(',', node.Inputs.Select(input => $"{input.Kind}:{input.Key}:{input.Value}"))}|{string.Join(',', node.Dependencies)}|{string.Join(',', node.Artifacts.Select(artifact => $"{artifact.Id}:{artifact.RelativeOutputPath}"))}");

    private static string DescribeDiagnostic(Diagnostics.SiteDiagnostic diagnostic) =>
        $"{diagnostic.Id}|{diagnostic.Message}";
}
