using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
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
            var script = "import {createInterface} from 'node:readline';console.log(JSON.stringify({protocol:1,type:'ready',node:'24.13.0',mdx:'3.1.1',react:'19.2.4',esbuild:'0.28.2'}));for await(const line of createInterface({input:process.stdin})){const request=JSON.parse(line);" + action + "}";
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
    public async Task MdxSite_ForwardsLoaderWarningsWithoutStartingNode()
    {
        using var workspace = new TemporaryWorkspace();
        await using var mdx = new MdxSite(new(workspace.Root, Path.Combine(workspace.Root, "missing-worker")));
        mdx.AddCollection(new WarningLoader());
        var output = Path.Combine(workspace.Root, "out");
        var generation = await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], output, clean: true, null,
            new() { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch }, CancellationToken.None);

        await Assert.That(mdx.Metrics.WorkerStarts).IsEqualTo(0);
        await Assert.That(generation.BuildReport.Diagnostics.Any(diagnostic =>
            diagnostic.Id == "LSMIG004")).IsTrue();
    }

    [Test]
    public async Task MdxReportsActualBrowserWorkAndSkipsUnchangedGraphs()
    {
        using var workspace = new TemporaryWorkspace();
        var root = workspace.Root;
        var source = Path.Combine(root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "shared.css"), ".shared{color:#123456}");
        await File.WriteAllTextAsync(Path.Combine(source, "counter.mdx"),
            "---\ntitle: Counter\n---\nimport './shared.css';\nimport Counter from './Counter.jsx'\n\n# Counter\n\n<Island component={Counter} strategy=\"load\" />\n");
        await File.WriteAllTextAsync(Path.Combine(source, "toggle.mdx"),
            "---\ntitle: Toggle\n---\nimport './shared.css';\nimport Toggle from './Toggle.jsx'\n\n# Toggle\n\n<Island component={Toggle} strategy=\"load\" />\n");
        await File.WriteAllTextAsync(Path.Combine(source, "plain.mdx"),
            "---\ntitle: Plain\n---\nimport './shared.css';\n\n# Plain\n\nStatic body.\n");
        await File.WriteAllTextAsync(Path.Combine(source, "Counter.jsx"), "export default function Counter(){return <button>Count</button>}");
        await File.WriteAllTextAsync(Path.Combine(source, "Toggle.jsx"), "export default function Toggle(){return <button>Toggle</button>}");
        var repository = FindRepository();
        await using var mdx = new MdxSite(new(root, Path.Combine(repository, "src/LithoSharp.Mdx/worker"))
            { Cacheable = true, Hydration = "selective" });
        mdx.AddCollection(new MdxContentCollectionLoader<FrontMatter>(new("mdx"), source,
            entry => SiteRoute.ForDirectoryIndex(Path.ChangeExtension(entry.Id.Value, null)), entry => new PageMetadata(entry.FrontMatter.Title, draft: entry.FrontMatter.Draft))
            { TransformationFingerprint = "test-v1" });
        var output = Path.Combine(root, "out");
        var generator = new SiteGenerator();
        var settings = new SiteSettings { BaseUrl = "https://example.com/project/" };
        var options = new SiteGenerationOptions { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch };

        await generator.GenerateWithOptionsAsync(settings, [], output, true, null, options, default);
        await Assert.That(mdx.Metrics.BundledPages).IsEqualTo(2);
        await Assert.That(mdx.Metrics.RebundledPages).IsEqualTo(2);

        // A static body edit recompiles one module and rebundles no interactive entry.
        await File.AppendAllTextAsync(Path.Combine(source, "plain.mdx"), "\nEdited body.\n");
        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default);
        await Assert.That(mdx.Metrics.CompiledModules).IsEqualTo(1);
        await Assert.That(mdx.Metrics.RebundledPages).IsEqualTo(0);

        // An independent component edit processes only its page entry while
        // the other page retains byte-identical HTML and asset URLs.
        var previousToggle = await File.ReadAllTextAsync(Path.Combine(output, "toggle/index.html"));
        await File.WriteAllTextAsync(Path.Combine(source, "Counter.jsx"),
            "export default function Counter(){return <button>Changed</button>}");
        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default);
        await Assert.That(mdx.Metrics.RebundledPageIds.Count).IsEqualTo(1);
        var processedEntries = mdx.Metrics.RebundledPageIds.ToArray();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "counter/index.html"))).Contains("Changed");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "toggle/index.html"))).IsEqualTo(previousToggle);

        // Changing the other independent component selects a different page entry.
        var previousCounter = await File.ReadAllTextAsync(Path.Combine(output, "counter/index.html"));
        await File.WriteAllTextAsync(Path.Combine(source, "Toggle.jsx"),
            "export default function Toggle(){return <button>Changed toggle</button>}");
        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default);
        await Assert.That(mdx.Metrics.RebundledPageIds.Count).IsEqualTo(1);
        await Assert.That(mdx.Metrics.RebundledPageIds.SequenceEqual(processedEntries)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "counter/index.html"))).IsEqualTo(previousCounter);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "toggle/index.html"))).Contains("Changed toggle");

        // A shared stylesheet edit updates every interactive entry that references it.
        await File.AppendAllTextAsync(Path.Combine(source, "shared.css"), "\n.corpus{border:1px solid blue}");
        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default);
        await Assert.That(mdx.Metrics.RebundledPageIds.Count).IsEqualTo(2);
    }

    [Test]
    public async Task MdxRestartsTheWorkerWhenDeclaredInputsChange()
    {
        using var workspace = new TemporaryWorkspace();
        var root = workspace.Root;
        var source = Path.Combine(root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "---\ntitle: Page\n---\n# Page\n");
        var declared = Path.Combine(root, "declared.txt");
        await File.WriteAllTextAsync(declared, "one");
        var repository = FindRepository();
        await using var mdx = new MdxSite(new(root, Path.Combine(repository, "src/LithoSharp.Mdx/worker"))
            { Cacheable = true, DeclaredInputFiles = [declared] });
        mdx.AddCollection(new MdxContentCollectionLoader<FrontMatter>(new("mdx"), source,
            entry => SiteRoute.ForDirectoryIndex(Path.ChangeExtension(entry.Id.Value, null)), entry => new PageMetadata(entry.FrontMatter.Title, draft: entry.FrontMatter.Draft))
            { TransformationFingerprint = "test-v1" });
        var output = Path.Combine(root, "out");
        var generator = new SiteGenerator();
        var settings = new SiteSettings { BaseUrl = "https://example.com/project/" };
        var options = new SiteGenerationOptions { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch };

        await generator.GenerateWithOptionsAsync(settings, [], output, true, null, options, default);
        await Assert.That(mdx.Metrics.WorkerStarts).IsEqualTo(1);

        // A changed declared input changes the tool fingerprint and the warm worker is replaced.
        await File.WriteAllTextAsync(declared, "two");
        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default);
        await Assert.That(mdx.Metrics.WorkerStarts).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "page/index.html"))).Contains("Page");
    }

    [Test]
    public async Task TwoWorkspacesDoNotShareWorkerState()
    {
        using var workspace = new TemporaryWorkspace();
        var repository = FindRepository();
        var worker = Path.Combine(repository, "src/LithoSharp.Mdx/worker");
        var settings = new SiteSettings { BaseUrl = "https://example.com/project/" };

        async Task<string> BuildAsync(string name, string title)
        {
            var project = Path.Combine(workspace.Root, name);
            var source = Path.Combine(project, "content");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), $"---\ntitle: {title}\n---\n# {title}\n");
            await using var mdx = new MdxSite(new(project, worker) { Cacheable = true });
            mdx.AddCollection(new MdxContentCollectionLoader<FrontMatter>(new("mdx"), source,
                entry => SiteRoute.ForDirectoryIndex(Path.ChangeExtension(entry.Id.Value, null)), entry => new PageMetadata(entry.FrontMatter.Title, draft: entry.FrontMatter.Draft))
                { TransformationFingerprint = "test-v1" });
            var output = Path.Combine(project, "out");
            var generation = await new SiteGenerator().GenerateWithOptionsAsync(settings, [], output, true, null,
                new SiteGenerationOptions { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
            await Assert.That(generation.BuildReport.Diagnostics.Where(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error)).IsEmpty();
            return await File.ReadAllTextAsync(Path.Combine(output, "page/index.html"));
        }

        var alpha = await BuildAsync("alpha", "Alpha workspace");
        var beta = await BuildAsync("beta", "Beta workspace");
        await Assert.That(alpha).Contains("Alpha workspace");
        await Assert.That(alpha).DoesNotContain("Beta workspace");
        await Assert.That(beta).Contains("Beta workspace");
        await Assert.That(beta).DoesNotContain("Alpha workspace");
    }

    [Test]
    public async Task MdxRemovesStaleScratchLeftByAKilledHost()
    {
        using var workspace = new TemporaryWorkspace();
        var root = workspace.Root;
        var source = Path.Combine(root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "---\ntitle: Page\n---\n# Page\n");
        var cacheRoot = Path.Combine(root, ".lithosharp", "mdx");
        Directory.CreateDirectory(cacheRoot);
        var stale = Path.Combine(cacheRoot, "work-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(cacheRoot, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(stale, "leftover.txt"), "leftover");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        var staleTemporary = Path.Combine(cacheRoot, "stale.json.tmp");
        await File.WriteAllTextAsync(staleTemporary, "partial");
        File.SetLastWriteTimeUtc(staleTemporary, DateTime.UtcNow.AddDays(-2));

        var repository = FindRepository();
        await using var mdx = new MdxSite(new(root, Path.Combine(repository, "src/LithoSharp.Mdx/worker"))
            { Cacheable = true });
        mdx.AddCollection(new MdxContentCollectionLoader<FrontMatter>(new("mdx"), source,
            entry => SiteRoute.ForDirectoryIndex(Path.ChangeExtension(entry.Id.Value, null)), entry => new PageMetadata(entry.FrontMatter.Title, draft: entry.FrontMatter.Draft))
            { TransformationFingerprint = "test-v1" });
        var output = Path.Combine(root, "out");
        await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.com/project/" }, [], output, clean: true, null,
            new SiteGenerationOptions { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch }, default);

        await Assert.That(Directory.Exists(stale)).IsFalse();
        await Assert.That(File.Exists(staleTemporary)).IsFalse();
        await Assert.That(Directory.Exists(live)).IsTrue();
    }

    [Test]
    [Arguments("null-envelope")]
    [Arguments("array-envelope")]
    [Arguments("numeric-hash")]
    [Arguments("null-result")]
    [Arguments("array-result")]
    [Arguments("missing-result-fields")]
    [Arguments("null-inputs")]
    [Arguments("numeric-input-file")]
    [Arguments("numeric-input-hash")]
    [Arguments("numeric-page-id")]
    [Arguments("object-page-css")]
    [Arguments("numeric-asset-bytes")]
    [Arguments("invalid-base64-asset-bytes")]
    [Arguments("inner-asset-hash-mismatch")]
    [Arguments("numeric-asset-import")]
    [Arguments("truncated")]
    [Arguments("hash-mismatch")]
    public async Task CorruptOptionalMdxCacheRebuildsWithoutChangingPublishedBytes(string corruption)
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "---\ntitle: Page\n---\n# Page\n");
        await using var mdx = new MdxSite(new(workspace.Root, Path.Combine(FindRepository(), "src/LithoSharp.Mdx/worker"))
            { Cacheable = true, Hydration = "selective" });
        mdx.AddCollection(new MdxContentCollectionLoader<FrontMatter>(new("mdx"), source,
            _ => SiteRoute.ForDirectoryIndex("page"), entry => new PageMetadata(entry.FrontMatter.Title))
            { TransformationFingerprint = "cache-corruption-test" });
        var generator = new SiteGenerator();
        var output = Path.Combine(workspace.Root, "out");
        var settings = new SiteSettings { BaseUrl = "https://example.com/project/" };
        var options = new SiteGenerationOptions { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch };
        await generator.GenerateWithOptionsAsync(settings, [], output, true, null, options, default);
        var before = HashOutput(output);
        var file = Directory.GetFiles(Path.Combine(workspace.Root, ".lithosharp", "mdx"), "*.json").Single();
        await File.WriteAllTextAsync(file, CorruptCache(await File.ReadAllTextAsync(file), corruption));

        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default);
        await Assert.That(mdx.Metrics.CacheHit).IsFalse();
        await Assert.That(HashOutput(output)).IsEquivalentTo(before);
        // Recovery rewrites the bad optional artifact; a third identical build
        // must hit it rather than silently disabling or repeatedly missing cache.
        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default);
        await Assert.That(mdx.Metrics.CacheHit).IsTrue();
        await Assert.That(HashOutput(output)).IsEquivalentTo(before);
    }

    [Test]
    public async Task CorruptCacheRebuildFailurePreservesPublishedOutputAndCanRecover()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"),
            "---\ntitle: Page\n---\nimport Counter from './Counter.jsx'\n\n# Page\n\n<Counter />\n");
        var component = Path.Combine(source, "Counter.jsx");
        const string good = "export default function Counter(){return <button>Ready</button>}";
        await File.WriteAllTextAsync(component, good);
        await using var mdx = new MdxSite(new(workspace.Root, Path.Combine(FindRepository(), "src/LithoSharp.Mdx/worker"))
            { Cacheable = true });
        mdx.AddCollection(new MdxContentCollectionLoader<FrontMatter>(new("mdx"), source,
            _ => SiteRoute.ForDirectoryIndex("page"), entry => new PageMetadata(entry.FrontMatter.Title))
            { TransformationFingerprint = "cache-corruption-test" });
        var generator = new SiteGenerator();
        var output = Path.Combine(workspace.Root, "out");
        var settings = new SiteSettings { BaseUrl = "https://example.com/project/" };
        var options = new SiteGenerationOptions { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch };
        await generator.GenerateWithOptionsAsync(settings, [], output, true, null, options, default);
        var before = HashOutput(output);
        var file = Directory.GetFiles(Path.Combine(workspace.Root, ".lithosharp", "mdx"), "*.json").Single();
        await File.WriteAllTextAsync(file, "[]");
        // Only imported component bytes change. Entry request/signature stays
        // the same, forcing the corrupt artifact read before actual compilation.
        await File.WriteAllTextAsync(component, "export default function Counter( {");
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output,
            false, null, options, default)).Throws<LithoSharp.Build.SiteBuildExtensionException>();
        await Assert.That(HashOutput(output)).IsEquivalentTo(before);
        await File.WriteAllTextAsync(component, good);
        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default);
        await Assert.That(HashOutput(output)).IsEquivalentTo(before);
        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default);
        await Assert.That(mdx.Metrics.CacheHit).IsTrue();
    }


    [Test]
    [Arguments("invalid-base64")]
    [Arguments("inner-hash-mismatch")]
    public async Task InvalidLiveWorkerAssetFailsWithoutPublishingOrBecomingAnOptionalCacheHit(string corruption)
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        var worker = Path.Combine(workspace.Root, "worker");
        Directory.CreateDirectory(source); Directory.CreateDirectory(worker);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "---\ntitle: Page\n---\n# Page\n");
        // Scripted successful protocol reply; these cases test bridge validation,
        // not actual MDX/esbuild operation counts or compiler failure behavior.
        await File.WriteAllTextAsync(Path.Combine(worker, "worker.mjs"), """
            import {createInterface} from 'node:readline';
            import {createHash} from 'node:crypto';
            import path from 'node:path';
            console.log(JSON.stringify({protocol:1,type:'ready',node:'24.13.0',mdx:'3.1.1',react:'19.2.4',esbuild:'0.28.2'}));
            for await(const line of createInterface({input:process.stdin,crlfDelay:Infinity})){
              const request=JSON.parse(line),page=request.pages[0],badEncoding=process.env.LITHOSHARP_TEST_ASSET_CORRUPTION==='invalid-base64';
              console.log(JSON.stringify({protocol:1,requestId:request.requestId,success:true,result:{
                inputs:Object.entries(request.capturedInputs).map(([file,hash])=>({file:path.resolve(request.projectRoot,file),hash})),
                assets:[{path:'asset.txt',bytes:badEncoding?'!':'',hash:badEncoding?createHash('sha256').update('').digest('hex'):'0'.repeat(64),imports:[]}],
                pages:[{id:page.id,html:'<h1>UntrustedLiveAsset</h1>',text:'Page',entry:null,css:[],headings:[],links:[],islands:[],hydration:'selective',fallback:null}],
                compiledModules:1,renderedPages:1,bundledPages:0,rebundledPages:[],
                timings:{totalMilliseconds:1,serverBundleMilliseconds:1,browserBundleMilliseconds:0,renderMilliseconds:0},memory:{heapUsed:1}}}));
            }
            """);
        var generator = new SiteGenerator();
        var settings = new SiteSettings();
        var output = Path.Combine(workspace.Root, "out");
        await generator.GenerateWithOptionsAsync(settings, [], output, true, null,
            new() { BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
        var before = HashOutput(output);
        await using var mdx = new MdxSite(new(workspace.Root, worker)
        {
            Cacheable = true,
            Hydration = "selective",
            Environment = new Dictionary<string, string> { ["LITHOSHARP_TEST_ASSET_CORRUPTION"] = corruption },
        });
        mdx.AddCollection(new MdxContentCollectionLoader<FrontMatter>(new("mdx"), source,
            _ => SiteRoute.ForDirectoryIndex("page"), entry => new PageMetadata(entry.FrontMatter.Title))
            { TransformationFingerprint = "invalid-live-asset-test" });
        Exception? failure = null;
        try
        {
            await generator.GenerateWithOptionsAsync(settings, [], output, false, null,
                new() { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
        }
        catch (SiteBuildExtensionException error) when (corruption == "inner-hash-mismatch") { failure = error; }
        catch (FormatException error) when (corruption == "invalid-base64") { failure = error; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(HashOutput(output)).IsEquivalentTo(before);
        await Assert.That(mdx.Metrics.Work.RequestAttempts).IsEqualTo(1);
        await Assert.That(mdx.Metrics.CacheHit).IsFalse();
        await Assert.That(Directory.GetFiles(Path.Combine(workspace.Root, ".lithosharp", "mdx"), "*.json").Length).IsEqualTo(0);
    }

    private static string CorruptCache(string original, string corruption)
    {
        if (corruption == "null-envelope") return "null";
        if (corruption == "array-envelope") return "[]";
        if (corruption == "truncated") return "{\"result\":";
        var envelope = JsonNode.Parse(original)!.AsObject();
        var result = envelope["result"]!.AsObject();
        switch (corruption)
        {
            case "numeric-hash": envelope["hash"] = 123; break;
            case "null-result": envelope["result"] = null; break;
            case "array-result": envelope["result"] = new JsonArray(); break;
            case "missing-result-fields": envelope["result"] = new JsonObject { ["inputs"] = new JsonArray() }; break;
            case "null-inputs": result["inputs"] = null; break;
            case "numeric-input-file": result["inputs"]![0]!["file"] = 123; break;
            case "numeric-input-hash": result["inputs"]![0]!["hash"] = 123; break;
            case "numeric-page-id": result["pages"]![0]!["id"] = 123; break;
            case "object-page-css": result["pages"]![0]!["css"] = new JsonObject(); break;
            case "numeric-asset-bytes": result["assets"]![0]!["bytes"] = 123; break;
            case "invalid-base64-asset-bytes": result["assets"]![0]!["bytes"] = "!"; break;
            case "inner-asset-hash-mismatch": result["assets"]![0]!["hash"] = new string('0', 64); break;
            case "numeric-asset-import": result["assets"]![0]!["imports"] = new JsonArray(JsonValue.Create(123)); break;
            case "hash-mismatch": envelope["hash"] = new string('0', 64); break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        if (corruption is not ("numeric-hash" or "hash-mismatch"))
        {
            // A matching outer checksum must not make a malformed result shape
            // usable. Serialize JsonElement exactly as the production bridge does.
            using var candidate = JsonDocument.Parse(envelope["result"]?.ToJsonString() ?? "null");
            envelope["hash"] = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(candidate.RootElement)));
        }
        return envelope.ToJsonString();
    }

    private sealed class WarningLoader : IContentCollectionLoader<FrontMatter, MdxDocument>
    {
        public ValueTask<ContentLoadResult<FrontMatter, MdxDocument>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ContentLoadResult<FrontMatter, MdxDocument>.Success(
                new(new("warnings"), Path.GetTempPath(), [], entry => SiteRoute.ForDirectoryIndex("unused"),
                    entry => new(entry.FrontMatter.Title)),
                [new("LSMIG004", SiteDiagnosticSeverity.Warning, "Synthetic loader warning.")]));
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
