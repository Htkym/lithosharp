using System.Diagnostics;
using System.Text.Json;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

public sealed class MdxInspectionLifetimeTests
{
    private const string Worker = """
        import {createInterface} from 'node:readline';
        import {writeFileSync,renameSync} from 'node:fs';
        console.log(JSON.stringify({protocol:1,type:'ready',node:'24.13.0',mdx:'3.1.1',react:'19.2.4',esbuild:'0.28.2'}));
        let announced=false;
        createInterface({input:process.stdin,crlfDelay:Infinity}).on('line',line=>{
          const request=JSON.parse(line);
          if(!announced){const marker=process.env.LITHOSHARP_TEST_ANALYSIS_STARTED;writeFileSync(marker+'.tmp',JSON.stringify({pid:process.pid}),{flag:'wx'});renameSync(marker+'.tmp',marker);announced=true;}
          if(process.env.LITHOSHARP_TEST_HOLD_ANALYSIS==='1')return;
          console.log(JSON.stringify({protocol:1,requestId:request.requestId,success:true,result:{
            headings:[{depth:1,text:'Other',id:'other',line:1}],links:[],islands:[],imports:[],diagnostics:[],text:'Other'
          }}));
        });
        """;

    private static async Task<int> WaitForStartedAsync(string marker)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(marker)) await Task.Delay(10, deadline.Token);
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(marker, deadline.Token));
        return data.RootElement.GetProperty("pid").GetInt32();
    }

    private static async Task<MdxInspectionSession> CreateSessionAsync(TemporaryWorkspace workspace, string name, bool hold)
    {
        var worker = Path.Combine(workspace.Root, name);
        Directory.CreateDirectory(worker);
        await File.WriteAllTextAsync(Path.Combine(worker, "worker.mjs"), Worker);
        return new MdxInspectionSession(new MdxOptions(workspace.Root, worker)
        {
            // Old disposal would wait this timeout. The test requires cancellation
            // and owned cleanup within5s, independently of the worker deadline.
            Timeout = TimeSpan.FromMinutes(5),
            Environment = new Dictionary<string, string>
            {
                ["LITHOSHARP_TEST_ANALYSIS_STARTED"] = Path.Combine(worker, "started.json"),
                ["LITHOSHARP_TEST_HOLD_ANALYSIS"] = hold ? "1" : "0",
            },
        });
    }

    [Test]
    public async Task DisposalCancelsActiveAndQueuedAnalysisAndLeavesOtherSessionAlive()
    {
        using var workspace = new TemporaryWorkspace();
        using var abort = new CancellationTokenSource();
        var owner = await CreateSessionAsync(workspace, "owner", hold: true);
        await using var other = await CreateSessionAsync(workspace, "other", hold: false);
        Task<MdxAnalysisResult>? active = null;
        Task<MdxAnalysisResult>? queued = null;
        Task? disposal = null;
        Task? repeated = null;
        try
        {
            active = owner.AnalyzeAsync("owner.mdx", "# Owner", cancellationToken: abort.Token);
            var ownerPid = await WaitForStartedAsync(Path.Combine(workspace.Root, "owner", "started.json"));
            using var ownerProcess = Process.GetProcessById(ownerPid);
            _ = ownerProcess.SafeHandle; // Preserve this exact worker incarnation for the exit assertion.
            var otherResult = await other.AnalyzeAsync("other.mdx", "# Other");
            await Assert.That(otherResult.Title).IsEqualTo("Other");
            var otherPid = await WaitForStartedAsync(Path.Combine(workspace.Root, "other", "started.json"));
            using var otherProcess = Process.GetProcessById(otherPid);
            _ = otherProcess.SafeHandle;

            queued = owner.AnalyzeAsync("queued.mdx", "# Queued", cancellationToken: abort.Token);
            disposal = owner.DisposeAsync().AsTask();
            repeated = owner.DisposeAsync().AsTask();
            await Task.WhenAll(disposal, repeated).WaitAsync(TimeSpan.FromSeconds(5));

            var activeCanceled = false;
            try { await active; } catch (OperationCanceledException) { activeCanceled = true; }
            var queuedCanceled = false;
            try { await queued; } catch (OperationCanceledException) { queuedCanceled = true; }
            await Assert.That(activeCanceled).IsTrue();
            await Assert.That(queuedCanceled).IsTrue();
            await Assert.That(ownerProcess.HasExited).IsTrue();
            await Assert.That(owner.WorkerStarts).IsEqualTo(1);
            await Assert.That(otherProcess.HasExited).IsFalse();
            var after = await other.AnalyzeAsync("after.mdx", "# Other");
            await Assert.That(after.Title).IsEqualTo("Other");
            await Assert.That(other.WorkerStarts).IsEqualTo(1);

            var rejected = false;
            try { await owner.AnalyzeAsync("later.mdx", "# Later"); }
            catch (ObjectDisposedException) { rejected = true; }
            await Assert.That(rejected).IsTrue();
            await owner.DisposeAsync();
        }
        finally
        {
            // Also clean the OLD implementation after its expected5s failure:
            // caller cancellation releases its blocked analysis before disposal.
            abort.Cancel();
            foreach (var analysis in new[] { active, queued })
            {
                if (analysis is null) continue;
                try { await analysis.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
            }
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            if (repeated is not null) await repeated.WaitAsync(TimeSpan.FromSeconds(10));
            await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task DisposingUnusedSessionIsRepeatableAndNeverStartsNode()
    {
        using var workspace = new TemporaryWorkspace();
        var session = new MdxInspectionSession(new MdxOptions(workspace.Root, workspace.Root)
        {
            NodeExecutable = "no-such-node-executable",
        });
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
        await session.DisposeAsync();
        await Assert.That(session.WorkerStarts).IsEqualTo(0);
        var rejected = false;
        try { await session.AnalyzeAsync("later.mdx", "# Later"); }
        catch (ObjectDisposedException) { rejected = true; }
        await Assert.That(rejected).IsTrue();
    }
}
