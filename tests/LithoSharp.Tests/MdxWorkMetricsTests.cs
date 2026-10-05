using System.Security.Cryptography;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Mdx;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class MdxWorkMetricsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InternalAttemptMetricsRemainAvailableToExistingJsonOutput(bool camelCase)
    {
        var metrics = new MdxBuildMetrics
        {
            CompiledModules = 1,
            RebundledPages = 2,
            Work = new MdxWorkerWorkMetrics
            {
                RequestAttempts = 3,
                RequestMilliseconds = 12.5,
                MdxCompileInvocations = 4,
                EsbuildInvocations = 5,
                BrowserEntryBuildAttempts = 6,
                HasCompleteReports = false,
            },
        };
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = camelCase ? System.Text.Json.JsonNamingPolicy.CamelCase : null,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(metrics, options);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var work = document.RootElement.GetProperty(camelCase ? "work" : "Work");
        await Assert.That(work.GetProperty(camelCase ? "requestAttempts" : "RequestAttempts").GetInt32()).IsEqualTo(3);
        await Assert.That(work.GetProperty(camelCase ? "requestMilliseconds" : "RequestMilliseconds").GetDouble()).IsEqualTo(12.5);
        await Assert.That(work.GetProperty(camelCase ? "mdxCompileInvocations" : "MdxCompileInvocations").GetInt64()).IsEqualTo(4L);
        await Assert.That(work.GetProperty(camelCase ? "esbuildInvocations" : "EsbuildInvocations").GetInt64()).IsEqualTo(5L);
        await Assert.That(work.GetProperty(camelCase ? "browserEntryBuildAttempts" : "BrowserEntryBuildAttempts").GetInt64()).IsEqualTo(6L);
        await Assert.That(work.GetProperty(camelCase ? "hasCompleteReports" : "HasCompleteReports").GetBoolean()).IsFalse();
        await Assert.That(document.RootElement.GetProperty(camelCase ? "compiledModules" : "CompiledModules").GetInt32()).IsEqualTo(1);
        await Assert.That(document.RootElement.GetProperty(camelCase ? "rebundledPages" : "RebundledPages").GetInt32()).IsEqualTo(2);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<MdxBuildMetrics>(json, options)!;
        await Assert.That(roundTrip.Work).IsEqualTo(metrics.Work);
        await Assert.That(roundTrip.CompiledModules).IsEqualTo(metrics.CompiledModules);
    }

    // Scripted protocol replies test bridge arithmetic and primary-error preservation.
    // They do not certify actual native/compiler operation counts; the real worker regroup fixture does that.
    private const string Script = """
        import {createInterface} from 'node:readline';
        import {createHash} from 'node:crypto';
        import path from 'node:path';
        console.log(JSON.stringify({protocol:1,type:'ready',node:'24.13.0',mdx:'3.1.1',react:'19.2.4',esbuild:'0.28.2'}));
        const metrics=(n,complete)=>({schema:1,complete,compiledModules:n,renderedPages:n,mdxCompileInvocations:n,renderInvocations:n,
          esbuildInvocations:n,browserBuildInvocations:n,browserEntryBuildAttempts:n,totalMilliseconds:10*n,
          serverBundleMilliseconds:2*n,browserBundleMilliseconds:3*n,liveRuntimeMilliseconds:n,pluginBundleMilliseconds:n,renderMilliseconds:n});
        let round=0;
        for await(const line of createInterface({input:process.stdin,crlfDelay:Infinity})){
          const request=JSON.parse(line), first=++round===1, mode=process.env.LITHOSHARP_TEST_WORK_REPORT;
          if(mode==='primary'){
            console.log(JSON.stringify({protocol:1,requestId:request.requestId,success:false,requiredSources:[],
              workMetrics:{schema:1,complete:false,compiledModules:'malformed'},diagnostics:[{id:'LSMDX001',message:'OriginalPrimaryWorkerFailure'}]}));
            continue;
          }
          if(first){
            const workMetrics=mode==='missing'?undefined:mode==='malformed'?{...metrics(2,false),esbuildInvocations:-1}:metrics(2,false);
            console.log(JSON.stringify({protocol:1,requestId:request.requestId,success:false,
              requiredSources:[path.resolve(request.projectRoot,'content/_partial.mdx')],workMetrics,diagnostics:[]}));continue;
          }
          const page=request.pages[0], bytes=Buffer.alloc(0), hash=createHash('sha256').update(bytes).digest('hex');
          console.log(JSON.stringify({protocol:1,requestId:request.requestId,success:true,result:{
            workMetrics:metrics(1,true),inputs:Object.entries(request.capturedInputs).map(([file,hash])=>({file:path.resolve(request.projectRoot,file),hash})),
            assets:[{path:'third-party-notices.txt',bytes:'',hash,imports:[]}],
            pages:[{id:page.id,html:'<h1 id="metric">Metric</h1>',text:'Metric',entry:null,css:[],headings:[],links:[],islands:[],hydration:'selective',fallback:null}],
            compiledModules:1,renderedPages:1,bundledPages:1,rebundledPages:[page.id],
            timings:{totalMilliseconds:10,serverBundleMilliseconds:2,browserBundleMilliseconds:3,renderMilliseconds:1},memory:{heapUsed:1}}}));
        }
        """;

    [Test]
    [Arguments("reported")]
    [Arguments("missing")]
    [Arguments("malformed")]
    public async Task BridgeIncludesReportedRetryWorkWithoutChangingFinalSuccessOrUniqueMetrics(string mode)
    {
        using var workspace = new TemporaryWorkspace();
        var worker = await CreateFixtureAsync(workspace);
        await using var mdx = CreateSite(workspace, worker, mode);
        var output = Path.Combine(workspace.Root, "out");
        var generator = new SiteGenerator();
        var options = new SiteGenerationOptions { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch };
        await generator.GenerateWithOptionsAsync(new SiteSettings(), [], output, true, null, options, default);
        var expected = mode == "reported" ? 3L : 1L;
        await Assert.That(mdx.Metrics.CompiledModules).IsEqualTo(1);
        await Assert.That(mdx.Metrics.RenderedPages).IsEqualTo(1);
        await Assert.That(mdx.Metrics.RebundledPages).IsEqualTo(1);
        await Assert.That(mdx.Metrics.Work.RequestAttempts).IsEqualTo(2);
        await Assert.That(mdx.Metrics.Work.CompiledModules).IsEqualTo(expected);
        await Assert.That(mdx.Metrics.Work.RenderedPages).IsEqualTo(expected);
        await Assert.That(mdx.Metrics.Work.EsbuildInvocations).IsEqualTo(expected);
        await Assert.That(mdx.Metrics.Work.BrowserBuildInvocations).IsEqualTo(expected);
        await Assert.That(mdx.Metrics.Work.BrowserEntryBuildAttempts).IsEqualTo(expected);
        await Assert.That(mdx.Metrics.Work.WorkerMilliseconds).IsEqualTo(10.0 * expected);
        await Assert.That(mdx.Metrics.Work.ServerBundleMilliseconds).IsEqualTo(2.0 * expected);
        await Assert.That(mdx.Metrics.Work.BrowserBundleMilliseconds).IsEqualTo(3.0 * expected);
        await Assert.That(mdx.Metrics.Work.RenderMilliseconds).IsEqualTo((double)expected);
        await Assert.That(mdx.Metrics.Work.RequestMilliseconds > 0).IsTrue();
        await Assert.That(mdx.Metrics.Work.HasCompleteReports).IsFalse();
        var inspectedWork = mdx.GetInspection()!.Value.GetProperty("metrics").GetProperty("work");
        await Assert.That(inspectedWork.GetProperty("requestAttempts").GetInt32()).IsEqualTo(2);
        await Assert.That(inspectedWork.GetProperty("esbuildInvocations").GetInt64()).IsEqualTo(expected);
        await Assert.That(inspectedWork.GetProperty("hasCompleteReports").GetBoolean()).IsFalse();

        // All accumulators belong to one preparation; a following complete request resets coverage/counts.
        await generator.GenerateWithOptionsAsync(new SiteSettings(), [], output, false, null, options, default);
        await Assert.That(mdx.Metrics.Work.RequestAttempts).IsEqualTo(1);
        await Assert.That(mdx.Metrics.Work.CompiledModules).IsEqualTo(1L);
        await Assert.That(mdx.Metrics.Work.WorkerStarts).IsEqualTo(0);
        await Assert.That(mdx.Metrics.Work.HasCompleteReports).IsTrue();
    }

    [Test]
    public async Task MalformedWorkReportCannotReplacePrimaryWorkerErrorOrPublishedOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var worker = await CreateFixtureAsync(workspace);
        var output = Path.Combine(workspace.Root, "out");
        var generator = new SiteGenerator();
        await generator.GenerateWithOptionsAsync(new SiteSettings(), [], output, true, null,
            new() { BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
        var published = HashOutput(output);
        await using var mdx = CreateSite(workspace, worker, "primary");
        SiteBuildExtensionException? failure = null;
        try { await generator.GenerateWithOptionsAsync(new SiteSettings(), [], output, false, null,
            new() { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch }, default); }
        catch (SiteBuildExtensionException error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Diagnostics.Single().Message).IsEqualTo("OriginalPrimaryWorkerFailure");
        await Assert.That(mdx.Metrics.Work.RequestAttempts).IsEqualTo(1);
        await Assert.That(mdx.Metrics.Work.HasCompleteReports).IsFalse();
        await Assert.That(mdx.Metrics.Work.RequestMilliseconds > 0).IsTrue();
        await Assert.That(HashOutput(output)).IsEquivalentTo(published);
    }

    private static async Task<string> CreateFixtureAsync(TemporaryWorkspace workspace)
    {
        var content = Path.Combine(workspace.Root, "content");
        var worker = Path.Combine(workspace.Root, "worker");
        Directory.CreateDirectory(content); Directory.CreateDirectory(worker);
        await File.WriteAllTextAsync(Path.Combine(content, "page.mdx"), "---\ntitle: Metric\n---\n# Metric\n");
        await File.WriteAllTextAsync(Path.Combine(content, "_partial.mdx"), "# Partial\n");
        await File.WriteAllTextAsync(Path.Combine(worker, "worker.mjs"), Script);
        return worker;
    }
    private static MdxSite CreateSite(TemporaryWorkspace workspace, string worker, string mode)
    {
        var mdx = new MdxSite(new(workspace.Root, worker) { Environment = new Dictionary<string, string> { ["LITHOSHARP_TEST_WORK_REPORT"] = mode } });
        mdx.AddCollection(new MdxContentCollectionLoader<MdxIntegrationTests.FrontMatter>(new("metric"), Path.Combine(workspace.Root, "content"),
            _ => SiteRoute.ForDirectoryIndex("page"), entry => new PageMetadata(entry.FrontMatter.Title)));
        return mdx;
    }
    private static Dictionary<string, string> HashOutput(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(file => !Path.GetFileName(file).StartsWith('.')).ToDictionary(file => Path.GetRelativePath(directory, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
}
