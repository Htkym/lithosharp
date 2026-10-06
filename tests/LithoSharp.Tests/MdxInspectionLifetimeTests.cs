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
        var other = await CreateSessionAsync(workspace, "other", hold: false);
        Task<MdxAnalysisResult>? active = null;
        Task<MdxAnalysisResult>? queued = null;
        Task? disposal = null;
        Task? repeated = null;
        Exception? primary = null;
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
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            // Preserve the primary assertion/analysis failure and finish every
            // owned analysis/disposer/session even when one cleanup task faults.
            // Expected cancellation of analyses is the only suppressed outcome.
            var cleanupFailures = new List<Exception>();
            try { abort.Cancel(); }
            catch (Exception error) { cleanupFailures.Add(error); }
            foreach (var analysis in new[] { active, queued })
            {
                if (analysis is null) continue;
                try { await analysis.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
            foreach (var disposer in new[] { disposal, repeated })
            {
                if (disposer is null) continue;
                try { await disposer.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
            foreach (var ownedSession in new[] { owner, other })
            {
                try { await ownedSession.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
            if (cleanupFailures.Count != 0)
            {
                if (primary is not null)
                    throw new AggregateException("MDX inspection test failed; owned cleanup also failed.", new[] { primary }.Concat(cleanupFailures));
                throw new AggregateException("MDX inspection owned cleanup failed.", cleanupFailures);
            }
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

    // WaitAsync can grant a queued request before disposal cancels its token,
    // while its ConfigureAwait(false) continuation is still pending. Releasing
    // the semaphore and racing DisposeAsync cannot force that boundary. Replay
    // the actual compiled continuation there instead of adding a product hook
    // or depending on ThreadPool scheduling. The existing active/queued worker
    // test above still exercises ordinary AnalyzeAsync admission and cleanup.
    private static (Task<MdxAnalysisResult> Completion, Action Resume) GateGrantedContinuation(
        MdxInspectionSession session, CancellationTokenSource analysisCancellation)
    {
        var method = typeof(MdxInspectionSession).GetMethod(nameof(MdxInspectionSession.AnalyzeAsync))!;
        var attribute = method.GetCustomAttributes(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false)
            .Cast<System.Runtime.CompilerServices.AsyncStateMachineAttribute>().Single();
        var machine = Activator.CreateInstance(attribute.StateMachineType, nonPublic: true)!;
        var fields = attribute.StateMachineType.GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        var builder = System.Runtime.CompilerServices.AsyncTaskMethodBuilder<MdxAnalysisResult>.Create();
        var completion = builder.Task;
        var stateField = fields.Single(field => field.Name == "<>1__state" && field.FieldType == typeof(int));
        stateField.SetValue(machine, 0);
        fields.Single(field => field.FieldType == typeof(MdxInspectionSession)).SetValue(machine, session);
        fields.Single(field => field.FieldType == typeof(System.Runtime.CompilerServices.AsyncTaskMethodBuilder<MdxAnalysisResult>))
            .SetValue(machine, builder);
        fields.Single(field => field.FieldType == typeof(CancellationTokenSource)).SetValue(machine, analysisCancellation);
        var gateAwaiter = Task.CompletedTask.ConfigureAwait(false).GetAwaiter();
        if (!gateAwaiter.IsCompleted || (int)stateField.GetValue(machine)! != 0)
            throw new InvalidOperationException("The admitted lifetime-await continuation must be complete at state zero.");
        fields.Single(field => field.FieldType == typeof(System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter))
            .SetValue(machine, gateAwaiter);
        return (completion, () => ((System.Runtime.CompilerServices.IAsyncStateMachine)machine).MoveNext());
    }

    [Test]
    public async Task Lsr25GateGrantedAcceptedAnalysisCancelsAfterDisposalAndNewRequestStillThrowsDisposed()
    {
        using var workspace = new TemporaryWorkspace();
        var session = new MdxInspectionSession(new MdxOptions(workspace.Root, workspace.Root)
        {
            NodeExecutable = "no-such-node-executable",
        });
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var gate = (SemaphoreSlim)typeof(MdxInspectionSession).GetField("lifetime", flags)!.GetValue(session)!;
        var owner = (CancellationTokenSource)typeof(MdxInspectionSession).GetField("ownerCancellation", flags)!.GetValue(session)!;
        await gate.WaitAsync();
        var transferred = false;
        Task? disposal = null;
        Exception? primary = null;
        using var acceptedCancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
        var acceptedToken = acceptedCancellation.Token;
        try
        {
            var continuation = GateGrantedContinuation(session, acceptedCancellation);
            disposal = session.DisposeAsync().AsTask();
            await Assert.That(acceptedToken.IsCancellationRequested).IsTrue();
            // This held permit now belongs to the already-granted continuation.
            // Its unchanged production finally releases it to the real disposer.
            transferred = true;
            continuation.Resume();
            Exception? outcome = null;
            try { await continuation.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) { outcome = error; }
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine("LSR25_BOUNDARY " + JsonSerializer.Serialize(new
            {
                stage = "gate-granted-disposal",
                exceptionType = outcome?.GetType().Name,
                cancellationRequested = acceptedToken.IsCancellationRequested,
                workerStarts = session.WorkerStarts,
            }));
            await Assert.That(outcome is OperationCanceledException).IsTrue();
            await Assert.That(outcome is ObjectDisposedException).IsFalse();
            await Assert.That(((OperationCanceledException)outcome!).CancellationToken == acceptedToken).IsTrue();
            await Assert.That(session.WorkerStarts).IsEqualTo(0);
            await Assert.That(gate.CurrentCount).IsEqualTo(1);

            Exception? newRequest = null;
            try { await session.AnalyzeAsync("later.mdx", "# Later", cancellationToken: new CancellationToken(true)); }
            catch (Exception error) { newRequest = error; }
            await Assert.That(newRequest is ObjectDisposedException).IsTrue();
            await Assert.That(session.WorkerStarts).IsEqualTo(0);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            Exception? cleanup = null;
            try
            {
                if (!transferred) gate.Release();
                if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { cleanup = error; }
            try { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error)
            {
                cleanup = cleanup is null ? error : new AggregateException("Both owned disposal waits failed.", cleanup, error);
            }
            if (cleanup is not null)
            {
                if (primary is not null)
                    throw new AggregateException("LSR25 test failed; owned disposal also failed.", primary, cleanup);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
            }
        }
    }
}
