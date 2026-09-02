using LithoSharp;
using LithoSharp.Build;
using LithoSharp.Diagnostics;

namespace LithoSharp.Tests;

public sealed class SiteGenerationResultTests
{
    [Test]
    public async Task EqualityAndHashCode_IgnoreBuildPlan()
    {
        IReadOnlyList<string> generatedFiles = new[] { "index.html" };
        var first = new SiteGenerationResult("output", 1, generatedFiles);
        var second = new SiteGenerationResult("output", 1, generatedFiles);

        await Assert.That(ReferenceEquals(first.BuildPlan, second.BuildPlan)).IsFalse();
        await Assert.That(first.Equals(second)).IsTrue();
        await Assert.That(((object)first).Equals(second)).IsTrue();
        await Assert.That(first == second).IsTrue();
        await Assert.That(first.GetHashCode()).IsEqualTo(second.GetHashCode());

        var differentPlan = second with
        {
            BuildPlan = SiteBuildPlan.Create(
            [
                new BuildNode(new BuildNodeId("different")),
            ]),
        };
        await Assert.That(first.Equals(differentPlan)).IsTrue();
        await Assert.That(first.GetHashCode()).IsEqualTo(differentPlan.GetHashCode());
    }

    [Test]
    public async Task Equality_PreservesOriginalPositionalMemberSemantics()
    {
        IReadOnlyList<string> generatedFiles = new[] { "index.html" };
        var baseline = new SiteGenerationResult("output", 1, generatedFiles);

        await Assert.That(baseline.Equals(
            new SiteGenerationResult("other", 1, generatedFiles))).IsFalse();
        await Assert.That(baseline.Equals(
            new SiteGenerationResult("output", 2, generatedFiles))).IsFalse();
        await Assert.That(baseline.Equals(
            new SiteGenerationResult("output", 1, new[] { "index.html" }))).IsFalse();
        await Assert.That(baseline.Equals(null)).IsFalse();
    }

    [Test]
    public async Task BuildReport_IsDeterministicAndDoesNotAffectResultEquality()
    {
        var first = new SiteBuildReport(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            "Production",
            "Template",
            nodes:
            [
                new SiteBuildReportNode("node", ["z.html", "a.html"]),
            ],
            generatedArtifacts: ["z.html", "a.html"],
            diagnostics:
            [
                new SiteDiagnostic("X", SiteDiagnosticSeverity.Info, "message"),
            ]);
        var second = new SiteBuildReport(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            "Production",
            "Template",
            nodes:
            [
                new SiteBuildReportNode("node", ["z.html", "a.html"]),
            ],
            generatedArtifacts: ["z.html", "a.html"],
            diagnostics:
            [
                new SiteDiagnostic("X", SiteDiagnosticSeverity.Info, "message"),
            ]);

        await Assert.That(first.Equals(second)).IsTrue();
        await Assert.That(first.GeneratedArtifacts).IsEquivalentTo(["a.html", "z.html"]);
        await Assert.That(first.Nodes[0].OwnedArtifacts).IsEquivalentTo(["a.html", "z.html"]);
        IReadOnlyList<string> generatedFiles = ["index.html"];
        await Assert.That(new SiteGenerationResult("output", 1, generatedFiles)
            with { BuildReport = first }
            == new SiteGenerationResult("output", 1, generatedFiles)
            with { BuildReport = second }).IsTrue();
    }

    [Test]
    public async Task BuildReport_DefensivelyCopiesAndCanonicallyOrdersNestedValues()
    {
        var artifacts = new List<string> { "z.html", "a.html", "a.html" };
        var reasons = new List<string> { "z", "a", "a" };
        var firstDiagnostic = new SiteDiagnostic("Z", SiteDiagnosticSeverity.Warning, "z");
        var diagnostics = new List<SiteDiagnostic>
        {
            firstDiagnostic,
            new("A", SiteDiagnosticSeverity.Info, "a"),
            new("A", SiteDiagnosticSeverity.Info, "a"),
        };
        var report = new SiteBuildReport(
            DateTimeOffset.UnixEpoch,
            "Production",
            "Template",
            nodes:
            [
                new SiteBuildReportNode("z", artifacts),
                new SiteBuildReportNode("a", ["b", "a", "a"]),
            ],
            invalidations:
            [
                new SiteBuildReportInvalidation("node", reasons),
            ],
            generatedArtifacts: artifacts,
            diagnostics: diagnostics);

        artifacts.Add("later.html");
        reasons.Add("later");
        diagnostics.Clear();

        await Assert.That(report.Nodes.Select(node => node.NodeId).SequenceEqual(["a", "z"])).IsTrue();
        await Assert.That(report.Nodes[0].OwnedArtifacts.SequenceEqual(["a", "b"])).IsTrue();
        await Assert.That(report.Nodes[1].OwnedArtifacts.SequenceEqual(["a.html", "z.html"])).IsTrue();
        await Assert.That(report.Invalidations[0].Reasons.SequenceEqual(["a", "z"])).IsTrue();
        await Assert.That(report.GeneratedArtifacts.SequenceEqual(["a.html", "z.html"])).IsTrue();
        await Assert.That(report.Diagnostics.Select(diagnostic => diagnostic.Id).SequenceEqual(["A", "Z"]))
            .IsTrue();
        await Assert.That(ReferenceEquals(report.Diagnostics[1], firstDiagnostic)).IsFalse();
        await Assert.That(report.Equals(new SiteBuildReport(
            DateTimeOffset.UnixEpoch,
            "Production",
            "Template",
            nodes:
            [
                new SiteBuildReportNode("a", ["a", "b"]),
                new SiteBuildReportNode("z", ["a.html", "z.html"]),
            ],
            invalidations:
            [
                new SiteBuildReportInvalidation("node", ["a", "z"]),
            ],
            generatedArtifacts: ["a.html", "z.html"],
            diagnostics:
            [
                new SiteDiagnostic("A", SiteDiagnosticSeverity.Info, "a"),
                new SiteDiagnostic("Z", SiteDiagnosticSeverity.Warning, "z"),
            ]))).IsTrue();
        await Assert.That(() => ((IList<SiteBuildReportNode>)report.Nodes).Clear())
            .Throws<NotSupportedException>();
        await Assert.That(() => ((IList<string>)report.GeneratedArtifacts).Add("mutation"))
            .Throws<NotSupportedException>();
        await Assert.That(() => ((IList<string>)report.Nodes[0].OwnedArtifacts).Add("mutation"))
            .Throws<NotSupportedException>();
        await Assert.That(() => ((IList<SiteDiagnostic>)report.Diagnostics).Clear())
            .Throws<NotSupportedException>();
    }
}
