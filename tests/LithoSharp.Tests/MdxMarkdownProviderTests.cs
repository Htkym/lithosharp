using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Mdx;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class MdxMarkdownProviderTests
{
    [Test]
    [Arguments(null)]
    [Arguments("configuration:v1")]
    [NotInParallel("MdxToolchain")]
    public async Task PreparedMdxProvider_SharesEntryContextAndDependencies_WithoutAssumingBodyCompleteness(string? fingerprint)
    {
        using var workspace = new TemporaryWorkspace();
        var worker = await WriteWorkerAsync(workspace.Root);
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "---\ntitle: Title\n---\n<Unknown />\n");
        await using var site = new MdxSite(new(workspace.Root, worker) { Cacheable = true });
        ContentEntry<MdxIntegrationTests.FrontMatter, MdxDocument>? provided = null;
        ContentEntry<MdxIntegrationTests.FrontMatter, MdxDocument>? rendered = null;
        ContentPageRenderingContext? providedContext = null;
        ContentPageRenderingContext? renderedContext = null;
        var calls = 0;
        site.AddCollectionWithMarkdown(new MdxContentCollectionLoader<MdxIntegrationTests.FrontMatter>(new("mdx"), source,
            static _ => SiteRoute.ForFile("page.html"), static entry => new PageMetadata(entry.FrontMatter.Title))
        { TransformationFingerprint = "loader:v1" }, (entry, context) =>
        {
            calls++;
            provided = entry;
            providedContext = context;
            return "Complete explicit body";
        }, fingerprint, renderer: (entry, context) =>
        { rendered = entry; renderedContext = context; return entry.Body.ToHtmlString(); });
        var contribution = await site.PrepareAsync(Context(workspace.Root));
        var registration = (SiteContentCollection<MdxIntegrationTests.FrontMatter, MdxDocument>)contribution.ContentCollections.Single();
        var page = GeneratedMarkdownProviderTests.Page(registration);
        var generator = new SiteGenerator();
        var configuration = GeneratedMarkdownProviderTests.Configuration(page);
        await Assert.That(calls).IsEqualTo(0);
        var result = page.GetPublicMarkdown(generator, configuration, "Production", AssetRegistry.Empty, default);
        page.GetPublicMarkdown(generator, configuration, "Production", AssetRegistry.Empty, default);
        page.Render(generator, configuration, "Production", AssetRegistry.Empty, default);
        await Assert.That(ReferenceEquals(provided, rendered) && ReferenceEquals(provided, registration.Collection.Entries[0])).IsTrue();
        await Assert.That(ReferenceEquals(providedContext, renderedContext)).IsTrue();
        await Assert.That(provided!.Body.ToHtmlString()).Contains("Prepared HTML");
        await Assert.That(provided.Body.Body).Contains("<Unknown />");
        await Assert.That(result.Markdown).IsEqualTo("Complete explicit body");
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(page.CanCacheRendering).IsEqualTo(fingerprint is not null);
        await Assert.That(page.DeclaredDependencies.Count > 0).IsTrue();
    }

    [Test]
    [NotInParallel("MdxToolchain")]
    public async Task BlogProvider_IsDeferredUntilRequested_AndFailureDoesNotPreventHtml()
    {
        using var workspace = new TemporaryWorkspace();
        var worker = await WriteWorkerAsync(workspace.Root);
        var source = Path.Combine(workspace.Root, "blog");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "---\ntitle: Title\ndate: 2020-01-01\nsummary: Excerpt only\n---\n<Unknown />\n");
        await using var blog = new MdxBlogSite(new MdxOptions(workspace.Root, worker));
        var calls = 0;
        blog.AddCollectionWithMarkdown(new("news", source, "news"), (_, _) =>
        { calls++; throw new InvalidOperationException("private-secret"); });
        var contribution = await blog.PrepareAsync(Context(workspace.Root));
        var collection = contribution.ContentCollections.OfType<SiteContentCollection<MdxBlogFrontMatter, MdxDocument>>().Single();
        var page = collection.CreatePages("https://example.com/", DateTimeOffset.Parse("2021-01-01T00:00:00Z"),
            "Production", new SiteRouteTable(), [], default).Single();
        var generator = new SiteGenerator();
        var configuration = GeneratedMarkdownProviderTests.Configuration(page);
        page.Render(generator, configuration, "Production", AssetRegistry.Empty, default);
        await Assert.That(calls).IsEqualTo(0);
        var result = page.GetPublicMarkdown(generator, configuration, "Production", AssetRegistry.Empty, default);
        page.Render(generator, configuration, "Production", AssetRegistry.Empty, default);
        await Assert.That(result.Status).IsEqualTo(ContentPageMarkdownStatus.Failed);
        await Assert.That(result.ToString()).DoesNotContain("private-secret");
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(page.Content).Contains("Prepared HTML");
    }

    [Test]
    [NotInParallel("MdxToolchain")]
    public async Task PreparedMdxProvider_PreservesNullEmptyFailureAndCancellation()
    {
        using var workspace = new TemporaryWorkspace();
        var worker = await WriteWorkerAsync(workspace.Root);
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "---\ntitle: Title\n---\n<Unknown />\n");
        await using var site = new MdxSite(new(workspace.Root, worker));
        var mode = "null";
        site.AddCollectionWithMarkdown(new MdxContentCollectionLoader<MdxIntegrationTests.FrontMatter>(new("mdx"), source,
            static _ => SiteRoute.ForFile("page.html"), static entry => new PageMetadata(entry.FrontMatter.Title)),
            (_, _) => mode switch
            {
                "null" => null,
                "empty" => "",
                "failure" => throw new InvalidOperationException("private-secret"),
                _ => throw new OperationCanceledException(),
            });
        var contribution = await site.PrepareAsync(Context(workspace.Root));
        foreach (var next in new[] { "null", "empty", "failure", "cancel" })
        {
            mode = next;
            var page = GeneratedMarkdownProviderTests.Page(contribution.ContentCollections.Single());
            ContentPageMarkdownResult? result = null;
            var cancelled = false;
            try
            { result = page.GetPublicMarkdown(new SiteGenerator(), GeneratedMarkdownProviderTests.Configuration(page), "Production", AssetRegistry.Empty, default); }
            catch (OperationCanceledException) { cancelled = true; }
            if (mode == "cancel")
                await Assert.That(cancelled).IsTrue();
            else
            {
                await Assert.That(result!.Status).IsEqualTo(mode == "null" ? ContentPageMarkdownStatus.Unsupported
                    : mode == "empty" ? ContentPageMarkdownStatus.Provided : ContentPageMarkdownStatus.Failed);
                await Assert.That(result.ToString()).DoesNotContain("private-secret");
            }
        }
    }

    private static SiteBuildContext Context(string root) => new(new SiteSettings(), new SiteGenerationOptions(),
        Path.Combine(root, "out"), Path.Combine(root, "cache"), DateTimeOffset.Parse("2021-01-01T00:00:00Z"));

    // Exercises bridge preparation and captured inputs without executing arbitrary MDX modules.
    private static async Task<string> WriteWorkerAsync(string root)
    {
        var worker = Path.Combine(root, "worker");
        Directory.CreateDirectory(worker);
        await File.WriteAllTextAsync(Path.Combine(worker, "worker.mjs"), """
            import {createInterface} from 'node:readline';
            import {createHash} from 'node:crypto';
            import path from 'node:path';
            console.log(JSON.stringify({protocol:1,type:'ready',node:'24.13.0',mdx:'3.1.1',react:'19.2.4',esbuild:'0.28.2'}));
            for await(const line of createInterface({input:process.stdin,crlfDelay:Infinity})){
              const request=JSON.parse(line);
              if(request.type==='analyze'){
                console.log(JSON.stringify({protocol:1,requestId:request.requestId,success:true,
                  result:{headings:[],links:[],islands:[],text:'',imports:[],diagnostics:[]}}));continue;
              }
              console.log(JSON.stringify({protocol:1,requestId:request.requestId,success:true,result:{
                inputs:Object.entries(request.capturedInputs).map(([file,hash])=>({file:path.resolve(request.projectRoot,file),hash})),
                assets:[{path:'third-party-notices.txt',bytes:'',hash:createHash('sha256').update('').digest('hex'),imports:[]}],
                pages:request.pages.map(page=>({id:page.id,html:'<p>Prepared HTML</p>',text:'Excerpt only',entry:null,css:[],headings:[],links:[],islands:[],hydration:'static',fallback:null})),
                compiledModules:request.pages.length,renderedPages:request.pages.length,bundledPages:0,rebundledPages:[],
                timings:{totalMilliseconds:1,serverBundleMilliseconds:1,browserBundleMilliseconds:0,renderMilliseconds:0},memory:{heapUsed:1}}}));
            }
            """);
        return worker;
    }
}
