using LithoSharp.OpenKnowledge;

namespace LithoSharp.Tests;

public sealed class OkfPathReferenceTests
{
    [Test]
    public async Task Version03ResolvesExplicitPathsWithoutRootFallbackOrReadingExternalTargets()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "index.md", "---\nokf_version: \"0.3\"\n---\n# Bundle\n");
        await WriteAsync(workspace.Root, "root.md", "---\ntype: Reference\n---\n# Root\n");
        await WriteAsync(workspace.Root, "sub/neighbor.md", "---\ntype: Reference\n---\n# Neighbor\n");
        await WriteAsync(workspace.Root, "sub/README.md", "# Not a concept\n");
        await WriteAsync(workspace.Root, "sub/calc.sql", "SELECT 1;");
        await WriteAsync(workspace.Root, "sub/space name.md", "---\ntype: Reference\n---\n# Space\n");
        await WriteAsync(workspace.Root, "sub/source.md", """
            ---
            type: Attested Computation
            runtime: sql
            status: deprecated
            resource: neighbor.md
            computation: calc.sql
            executor: {resource: /root.md}
            attester: {resource: root.md}
            superseded_by: /sub/neighbor.md
            sources:
              - {resource: all queries in project X}
              - {resource: README.md}
              - {resource: ../%2e%2e/outside.md}
              - {resource: file:///not-an-okf-path.md}
              - {resource: space%20name.md}
              - {resource: 'https://example.test/resource'}
            ---
            [Neighbor](neighbor.md#unknown) [wrong case](Neighbor.md) [not a concept](README.md)
            [outside](../%2e%2e/outside.md) [encoded slash](space%2fname.md) [reserved](/index.md)
            """);

        var bundle = await OkfBundle.ReadAsync(workspace.Root);
        await Assert.That(bundle.IsConformant).IsTrue();
        var refs = bundle.PathReferences.ToDictionary(reference => reference.Field);
        await Assert.That(refs["resource"].TargetConceptId).IsEqualTo("sub/neighbor");
        await Assert.That(refs["resource"].IsConceptEdge).IsTrue();
        await Assert.That(refs["computation"].Kind).IsEqualTo(OkfLinkKind.Auxiliary);
        await Assert.That(refs["computation"].IsConceptEdge).IsFalse();
        await Assert.That(refs["executor.resource"].TargetConceptId).IsEqualTo("root");
        await Assert.That(refs["attester.resource"].Kind).IsEqualTo(OkfLinkKind.Unresolved);
        await Assert.That(refs["attester.resource"].TargetPath).IsEqualTo("sub/root.md");
        await Assert.That(refs["superseded_by"].TargetConceptId).IsEqualTo("sub/neighbor");
        await Assert.That(refs["sources[0].resource"].Kind).IsEqualTo(OkfLinkKind.ScopeDescriptor);
        await Assert.That(refs["sources[0].resource"].TargetPath).IsNull();
        await Assert.That(refs["sources[1].resource"].Kind).IsEqualTo(OkfLinkKind.NonConceptDocument);
        await Assert.That(refs["sources[1].resource"].PublicUrl).IsNull();
        await Assert.That(refs["sources[2].resource"].Kind).IsEqualTo(OkfLinkKind.OutsideBundle);
        await Assert.That(refs["sources[3].resource"].Kind).IsEqualTo(OkfLinkKind.OutsideBundle);
        await Assert.That(refs["sources[4].resource"].TargetConceptId).IsEqualTo("sub/space name");
        await Assert.That(refs["sources[5].resource"].Kind).IsEqualTo(OkfLinkKind.External);

        var links = bundle.Links.ToDictionary(link => link.OriginalTarget!);
        await Assert.That(links["neighbor.md#unknown"].IsConceptEdge).IsTrue();
        await Assert.That(links["Neighbor.md"].IsConceptEdge).IsFalse();
        await Assert.That(links["README.md"].Kind).IsEqualTo(OkfLinkKind.NonConceptDocument);
        await Assert.That(links["../%2e%2e/outside.md"].Kind).IsEqualTo(OkfLinkKind.OutsideBundle);
        await Assert.That(links["space%2fname.md"].Kind).IsEqualTo(OkfLinkKind.OutsideBundle);
        await Assert.That(links["/index.md"].Kind).IsEqualTo(OkfLinkKind.ReservedDocument);
    }

    [Test]
    public async Task ExplicitVersion02KeepsBodyLinkCompatibility()
    {
        using var workspace = new TemporaryWorkspace();
        await WriteAsync(workspace.Root, "index.md", "---\nokf_version: '0.2'\n---\n[Concept](target.md)\n");
        await WriteAsync(workspace.Root, "target.md", "---\ntype: Reference\nresource: missing.md\n---\n[Missing](missing.md)\n");

        var bundle = await OkfBundle.ReadAsync(workspace.Root);
        await Assert.That(bundle.IsConformant).IsTrue();
        await Assert.That(bundle.PathReferences.Count).IsEqualTo(0);
        await Assert.That(bundle.Links.Single(link => link.SourcePath == "index.md").IsConceptEdge).IsTrue();
        await Assert.That(bundle.Links.Single(link => link.SourcePath == "target.md").TargetConceptId).IsEqualTo("missing");
    }

    private static async Task WriteAsync(string root, string path, string text)
    {
        var file = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, text);
    }
}
