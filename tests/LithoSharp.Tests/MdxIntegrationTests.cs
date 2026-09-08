using System.Security.Cryptography;
using System.Text.Json;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Mdx;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class MdxIntegrationTests
{
    [Test]
    public async Task MdxRendersHydratesCachesAndPreservesPublishedOutputOnFailure()
    {
        using var workspace = new TemporaryWorkspace();
        var root = workspace.Root;
        var source = Path.Combine(root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "hello.mdx"), "---\ntitle: Hello\n---\nimport Counter from './Counter.jsx'\n\n# Hello\n\n<Counter />\n");
        await File.WriteAllTextAsync(Path.Combine(source, "Counter.jsx"), "import {useState} from 'react';export default function Counter(){const [n,set]=useState(0);return <button onClick={()=>set(n+1)}>Count {n}</button>}");
        await File.WriteAllTextAsync(Path.Combine(source, "draft.mdx"), "---\ntitle: Draft\ndraft: true\n---\nexport const secret = (() => {throw new Error('Draft was executed')})()\n\n# Draft\n");
        var repository = FindRepository();
        await using var mdx = new MdxSite(new(root, Path.Combine(repository, "src/LithoSharp.Mdx/worker")) { Cacheable = true });
        mdx.AddCollection(new MdxContentCollectionLoader<FrontMatter>(new("mdx"), source,
            entry => SiteRoute.ForDirectoryIndex(Path.ChangeExtension(entry.Id.Value, null)), entry => new PageMetadata(entry.FrontMatter.Title, draft: entry.FrontMatter.Draft))
            { TransformationFingerprint = "test-v1" });
        var output = Path.Combine(root, "out");
        var generator = new SiteGenerator();
        var settings = new SiteSettings { BaseUrl = "https://example.com/project/" };
        var options = new SiteGenerationOptions { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch };
        var first = await generator.GenerateWithOptionsAsync(settings, [], output, true, null, options, default);
        var page = Path.Combine(output, "hello/index.html");
        var html = await File.ReadAllTextAsync(page);
        await Assert.That(html).Contains("Count ");
        await Assert.That(html).Contains("/project/_mdx/pages/");
        await Assert.That(mdx.Metrics.RenderedPages).IsEqualTo(1);
        await Assert.That(Directory.Exists(Path.Combine(output, "draft"))).IsFalse();
        var before = HashOutput(output);
        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options with { PreviousBuildPlan = first.BuildPlan }, default);
        await Assert.That(mdx.Metrics.CacheHit).IsTrue();
        await Assert.That(mdx.Metrics.CompiledModules + mdx.Metrics.RenderedPages + mdx.Metrics.BundledPages).IsEqualTo(0);
        await Assert.That(HashOutput(output)).IsEquivalentTo(before);
        await File.WriteAllTextAsync(Path.Combine(source, "Counter.jsx"), "export default function Counter(){return <button>Changed</button>}");
        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default);
        await Assert.That(await File.ReadAllTextAsync(page)).Contains("Changed");
        before = HashOutput(output);
        await File.WriteAllTextAsync(Path.Combine(source, "Counter.jsx"), "export default !!!");
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default)).ThrowsException();
        await Assert.That(HashOutput(output)).IsEquivalentTo(before);
    }

    [Test]
    public async Task PublicPropsRejectUndeclaredSecretsAndPrototypeKeys()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { title = new { type = "string" } }, additionalProperties = false });
        await Assert.That(() => new MdxPublicData(JsonSerializer.SerializeToElement(new { title = "Hi", secret = "private" }), schema)).Throws<ArgumentException>();
        await Assert.That(() => new MdxPublicData(JsonDocument.Parse("{\"__proto__\":{}}").RootElement, schema)).Throws<ArgumentException>();
    }

    private static Dictionary<string, string> HashOutput(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(file => !Path.GetFileName(file).StartsWith('.')).ToDictionary(file => Path.GetRelativePath(directory, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    private static string FindRepository()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx"))) return path.FullName;
        throw new DirectoryNotFoundException("The MDX integration fixture requires the repository's restored worker.");
    }
    public sealed class FrontMatter
    {
        public string Title { get; set; } = "";
        public bool Draft { get; set; }
    }
}
