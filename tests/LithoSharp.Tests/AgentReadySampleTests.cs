using LithoSharp.Diagnostics;

namespace LithoSharp.Tests;

[NotInParallel]
public sealed class AgentReadySampleTests
{
    [Test]
    [Arguments("docs", false)]
    [Arguments("docs", true)]
    [Arguments("blog", false)]
    public async Task OfficialSamplesPublishCompleteBodiesAndReuseArtifacts(string kind, bool assets)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "LithoSharp.slnx")))
            repository = repository.Parent;
        if (repository is null)
            throw new InvalidOperationException("Repository root not found.");
        var root = Path.Combine(Path.GetTempPath(), "lithosharp-agent-ready-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, "out");
            ISiteFactory factory = kind == "docs" ? new DocsSampleFactory { OutputDirectory = output, AssetDemo = assets, Check = true }
                : new BlogSampleFactory { Check = true };
            var definition = await factory.CreateAsync(new SiteFactoryContext(Path.Combine(repository.FullName, "samples",
                kind == "docs" ? "LithoSharp.DocsSample" : "LithoSharp.Sample")));
            var customization = definition.Customization ?? throw new InvalidOperationException("The sample must explicitly configure Agent Ready.");
            await Assert.That(customization.AgentReady.Enabled).IsTrue();
            var generator = new SiteGenerator();
            var options = definition.Options with { MaxDegreeOfParallelism = 1 };
            var first = await generator.GenerateWithOptionsAsync(definition.Site, definition.Posts, output, true,
                customization, options, default);
            if (first.BuildReport.Diagnostics.Any(item => item.Id == "LSQ012" || item.Severity == SiteDiagnosticSeverity.Error))
                throw new InvalidOperationException(first.QualityReport.Format(LithoSharp.Quality.SiteDiagnosticFormat.Text));
            var markdownArtifacts = first.BuildPlan!.Artifacts.Where(artifact => artifact.OwnerNodeId.Value.StartsWith("agent-ready:page:", StringComparison.Ordinal)).ToArray();
            await Assert.That(markdownArtifacts.Length >= definition.Posts.Count).IsTrue();
            var before = new Dictionary<string, byte[]>();
            foreach (var artifact in markdownArtifacts)
                before.Add(artifact.RelativeOutputPath, await File.ReadAllBytesAsync(Path.Combine(output, artifact.RelativeOutputPath)));
            if (kind == "docs")
            {
                var article = await File.ReadAllTextAsync(Path.Combine(output, "articles/first/index.md"));
                await Assert.That(article).Contains("Version 3\\.0");
                await Assert.That(article).Contains("Read the related typed article");
                await Assert.That(article).Contains("Download sample source");
                await Assert.That(article).DoesNotContain(":::component");
                var topic = await File.ReadAllTextAsync(Path.Combine(output, "topics/collections/index.md"));
                await Assert.That(topic).Contains("https://example.com/articles/first/");
                var html = await File.ReadAllTextAsync(Path.Combine(output, "topics/collections/index.html"));
                await Assert.That(html).Contains("href=\"https://example.com/topics/collections/index.md\"");
                if (assets)
                {
                    await Assert.That(article).Contains("![LithoSharp sample icon]");
                    await Assert.That(article).Contains("https://example.com/images/");
                    await Assert.That(article).Contains("https://example.com/asset-demo.txt");
                }
            }
            var second = await generator.GenerateWithOptionsAsync(definition.Site, definition.Posts, output, false,
                customization, options, default);
            await Assert.That(second.BuildReport.Diagnostics.Any(item => item.Id == "LSQ012" || item.Severity == SiteDiagnosticSeverity.Error)).IsFalse();
            foreach (var pair in before)
                await Assert.That((await File.ReadAllBytesAsync(Path.Combine(output, pair.Key))).SequenceEqual(pair.Value)).IsTrue();
            await Assert.That(second.BuildReport.Nodes.Where(node => node.NodeId.StartsWith("agent-ready:page:", StringComparison.Ordinal)).All(node => node.CacheHit)).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
