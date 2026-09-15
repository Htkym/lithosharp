using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.Mdx;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

/// <summary>
/// R23: installation media varies, so Markdown-only sites must build with no
/// Node.js on PATH, while MDX sites must fail with a missing-dependency
/// diagnostic instead of an execution crash.
/// </summary>
[NotInParallel]
public sealed class NodeRequirementTests
{
    [Test]
    public async Task MarkdownOnlySiteBuildsWithoutNodeOnPath()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "intro.md"), "---\ntitle: Intro\n---\n# Intro\n");
        var emptyBin = Path.Combine(workspace.Root, "empty-bin");
        Directory.CreateDirectory(emptyBin);
        var previous = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", emptyBin);
        try
        {
            await using var docs = new DocumentationSite(new(workspace.Root));
            docs.AddCollection(new DocumentationCollection("guide", [new("current", "en", source, "guide")]));
            var output = Path.Combine(workspace.Root, "out");
            await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], output, true,
                new() { Template = new DocsSiteTemplate() }, new() { Extensions = [docs] }, default);
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "guide/intro/index.html"))).Contains("Intro");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previous);
        }
    }

    [Test]
    public async Task MdxSiteWithoutNodeReportsMissingDependency()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "hello.mdx"), "---\ntitle: Hello\n---\n# Hello\n");
        await using var mdx = new MdxSite(new(workspace.Root, WorkerDirectory())
        {
            NodeExecutable = Path.Combine(workspace.Root, "no-such-node"),
        });
        mdx.AddCollection(new MdxContentCollectionLoader<FrontMatter>(new("fault"), source,
            _ => SiteRoute.ForDirectoryIndex("hello"), entry => new(entry.FrontMatter.Title)));
        var output = Path.Combine(workspace.Root, "out");
        var exception = await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], output, true, null,
            new() { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch }, default))
            .Throws<LithoSharp.Build.SiteBuildExtensionException>();
        await Assert.That(exception!.Diagnostics.Any(diagnostic => diagnostic.Id == "LSMDX002")).IsTrue();
    }

    private static string WorkerDirectory()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
                return Path.Combine(path.FullName, "src", "LithoSharp.Mdx", "worker");
        throw new DirectoryNotFoundException("The MDX integration fixture requires the repository's restored worker.");
    }

    public sealed class FrontMatter
    {
        public string Title { get; set; } = "";
    }
}
