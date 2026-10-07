using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

public sealed class MdxWorkerReadinessTests
{
    private static Dictionary<string, object> Ready() => new()
    {
        ["protocol"] = 1, ["type"] = "ready", ["node"] = "24.13.0", ["mdx"] = "3.1.1",
        ["react"] = "19.2.4", ["esbuild"] = "0.28.2",
    };

    private static async Task<MdxInspectionSession> SessionAsync(TemporaryWorkspace workspace, Dictionary<string, object> ready)
    {
        var worker = Path.Combine(workspace.Root, "worker");
        Directory.CreateDirectory(worker);
        await File.WriteAllTextAsync(Path.Combine(worker, "worker.mjs"),
            "import {createInterface} from 'node:readline';\nconsole.log(JSON.stringify(" + JsonSerializer.Serialize(ready) + "));\n"
            + "for await(const line of createInterface({input:process.stdin})){const request=JSON.parse(line);console.log(JSON.stringify({protocol:1,requestId:request.requestId,success:true,result:{headings:[{depth:1,text:'Ready',id:'ready',line:1}],links:[],islands:[],imports:[],diagnostics:[],text:'Ready'}}));}\n");
        return new MdxInspectionSession(new(workspace.Root, worker) { Timeout = TimeSpan.FromSeconds(10) });
    }

    [Test]
    [Arguments("node", "25.0.0")]
    [Arguments("mdx", "3.2.0")]
    [Arguments("react", "20.0.0")]
    [Arguments("esbuild", "0.29.0")]
    public async Task WrongSupportedFieldRemainsRejectedAndReportsBoundedTuple(string field, string version)
    {
        using var workspace = new TemporaryWorkspace();
        var ready = Ready(); ready[field] = version;
        await using var session = await SessionAsync(workspace, ready);
        SiteBuildExtensionException? failure = null;
        try { await session.AnalyzeAsync("ready.mdx", "# Ready\n"); }
        catch (SiteBuildExtensionException error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        var diagnostic = failure!.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("LSMDX003");
        await Assert.That(diagnostic.Message).Contains("The worker protocol or toolchain does not match the supported locked versions.");
        await Assert.That(diagnostic.Message).Contains("expected(protocol=1,type=ready,node=24.13.0,mdx=3.1.1,react=19.2.4,esbuild=0.28.2)");
        await Assert.That(diagnostic.Message).Contains(field + "=" + version);
        await Assert.That(diagnostic.Message.Length).IsLessThan(400);
    }

    [Test]
    public async Task ExactSupportedTupleStillAnalyzes()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = await SessionAsync(workspace, Ready());
        var result = await session.AnalyzeAsync("ready.mdx", "# Ready\n");
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Title).IsEqualTo("Ready");
    }

    [Test]
    public async Task UnknownReadinessDataNeverExportsPathsSecretsOrArbitraryFields()
    {
        using var workspace = new TemporaryWorkspace();
        var ready = Ready();
        ready["protocol"] = 2;
        ready["type"] = "PRIVATE_TYPE_SENTINEL";
        ready["node"] = "/Users/PRIVATE_PATH_SENTINEL/node";
        ready["mdx"] = "SECRET_SENTINEL";
        ready["react"] = "1.2.3\nPRIVATE_LOG_SENTINEL";
        ready["esbuild"] = new string('9', 1000);
        ready["unknown"] = "PRIVATE_UNKNOWN_SENTINEL";
        await using var session = await SessionAsync(workspace, ready);
        SiteBuildExtensionException? failure = null;
        try { await session.AnalyzeAsync("ready.mdx", "# Ready\n"); }
        catch (SiteBuildExtensionException error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        var diagnostic = failure!.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("LSMDX003");
        await Assert.That(diagnostic.Message).Contains("reported(protocol=2,type=[invalid],node=[invalid],mdx=[invalid],react=[invalid],esbuild=[invalid])");
        await Assert.That(diagnostic.Message).DoesNotContain("PRIVATE");
        await Assert.That(diagnostic.Message).DoesNotContain("SECRET");
        await Assert.That(diagnostic.Message.Length).IsLessThan(400);
    }
}
