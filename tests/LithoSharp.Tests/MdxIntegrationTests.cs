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
    public async Task WorkerFailuresTimeoutAndCancellationPreserveThePublishedSite()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        var worker = Path.Combine(workspace.Root, "worker");
        Directory.CreateDirectory(source); Directory.CreateDirectory(worker);
        await File.WriteAllTextAsync(Path.Combine(source, "hello.mdx"), "---\ntitle: Hello\n---\n# Hello\n");
        var output = Path.Combine(workspace.Root, "out");
        var generator = new SiteGenerator();
        await generator.GenerateWithOptionsAsync(new SiteSettings(), [], output, true, null, new() { BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
        var snapshot = HashOutput(output);
        foreach (var mode in new[] { "crash", "invalid-json", "oversize", "request-id", "path", "timeout", "cancel" })
        {
            var action = mode switch
            {
                "crash" => "process.exit(23);",
                "invalid-json" => "console.log('not json');",
                "oversize" => "console.log('x'.repeat(9000));",
                "request-id" => "console.log(JSON.stringify({protocol:1,requestId:'wrong'}));",
                "path" => "console.log(JSON.stringify({protocol:1,requestId:request.requestId,success:true,result:{inputs:[],assets:[{path:'../escape.js',bytes:'',hash:'',imports:[]}],pages:[]}}));",
                _ => "setInterval(()=>{},1000);"
            };
            var script = "import {createInterface} from 'node:readline';console.log(JSON.stringify({protocol:1,type:'ready',node:'24.13.0',mdx:'3.1.1',react:'19.2.4',esbuild:'0.25.12'}));for await(const line of createInterface({input:process.stdin})){const request=JSON.parse(line);" + action + "}";
            await File.WriteAllTextAsync(Path.Combine(worker, "worker.mjs"), script);
            await using var mdx = new MdxSite(new(workspace.Root, worker) { Timeout = TimeSpan.FromSeconds(1), MaximumMessageBytes = 8192 });
            mdx.AddCollection(new MdxContentCollectionLoader<FrontMatter>(new("fault"), source, _ => SiteRoute.ForDirectoryIndex("hello"), entry => new(entry.FrontMatter.Title)));
            using var cancellation = new CancellationTokenSource();
            if (mode == "cancel") cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));
            await Assert.That(async () => await generator.GenerateWithOptionsAsync(new SiteSettings(), [], output, false, null,
                new() { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch }, cancellation.Token)).ThrowsException();
            await Assert.That(HashOutput(output)).IsEquivalentTo(snapshot);
            await Assert.That(File.Exists(Path.Combine(workspace.Root, "escape.js"))).IsFalse();
        }
    }
    [Test]
    public async Task BlogsShareAuthorsPaginationAndStaticFeedsWithPublicationFiltering()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "first.mdx"), "---\ntitle: First\ndate: 2020-01-01\nsummary: Safe excerpt\nauthors: [alice, bob]\ntags: [dotnet]\n---\n# Body\n");
        await File.WriteAllTextAsync(Path.Combine(source, "second.mdx"), "---\ntitle: Second\ndate: 2020-01-02\nsummary: Next excerpt\nauthors: [alice]\n---\n# Second\n");
        await File.WriteAllTextAsync(Path.Combine(source, "hidden.mdx"), "---\ntitle: Hidden\ndate: 2020-01-02\nunlisted: true\nsummary: hidden-canary\n---\n# Hidden\n");
        var docsSource = Path.Combine(workspace.Root, "docs");
        Directory.CreateDirectory(docsSource);
        await File.WriteAllTextAsync(Path.Combine(docsSource, "intro.mdx"), "---\ntitle: Guide\n---\n# Guide\n");
        await using var blog = new DocumentationSite(new(workspace.Root, Path.Combine(FindRepository(), "src/LithoSharp.Mdx/worker")) { Cacheable = true, Hydration = "selective" });
        blog.AddCollection(new("guide", [new("current", "en", docsSource, "guide")]) { UseMdx = true });
        blog.AddBlog(new("news", source, "news") { PageSize = 1, Authors = new Dictionary<string, BlogAuthor> { ["alice"] = new("Alice", "Engineer"), ["bob"] = new("Bob") } });
        var output = Path.Combine(workspace.Root, "out");
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.com/project/" }, [], output, true, null,
            new() { Extensions = [blog], BuildTimestamp = DateTimeOffset.Parse("2021-01-01T00:00:00Z") }, default);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "news/index.html"))).Contains("/project/news/second/");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "news/page/2/index.html"))).Contains("Safe excerpt");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "news/authors/alice/index.html"))).Contains("Engineer");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "news/first/index.html"))).Contains("min read");
        await Assert.That(File.Exists(Path.Combine(output, "guide/intro/index.html"))).IsTrue();
        await Assert.That(blog.MdxMetrics.WorkerStarts).IsEqualTo(1);
        foreach (var file in new[] { "rss.xml", "atom.xml", "feed.json" })
        {
            var feed = await File.ReadAllTextAsync(Path.Combine(output, "news", file));
            await Assert.That(feed).Contains("Safe excerpt");
            await Assert.That(feed).DoesNotContain("hidden-canary");
            await Assert.That(feed).DoesNotContain("<script");
        }
    }
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
        await File.WriteAllTextAsync(Path.Combine(source, "hello.mdx"), "---\ntitle: Hello\n---\nimport Draft from './draft.mdx'\n\n<Draft />\n");
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default)).Throws<LithoSharp.Build.SiteBuildExtensionException>();
        await Assert.That(HashOutput(output)).IsEquivalentTo(before);
        await File.WriteAllTextAsync(Path.Combine(source, "hello.mdx"), "---\ntitle: Hello\n---\nimport Counter from './Counter.jsx'\n\n# Hello\n\n<Counter />\n");
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
