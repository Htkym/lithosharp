using System.Diagnostics;
using System.Threading;
using LithoSharp;

namespace LithoSharp.Tests;

public sealed class SiteBuildTimingRecorderTests
{
    [Test]
    public async Task ParallelVerificationAddsAndCountsAreExact()
    {
        const int workerCount = 16;
        const int samplesPerWorker = 257;
        var recorder = new SiteGenerator.BuildTimingRecorder(enabled: true);
        using var admitted = new CountdownEvent(workerCount);
        using var start = new ManualResetEventSlim();
        var workers = new Thread[workerCount];
        long expectedTicks = 0;

        for (var worker = 0; worker < workerCount; worker++)
        {
            var workerIndex = worker;
            expectedTicks += (long)(workerIndex + 1) * samplesPerWorker;
            workers[worker] = new Thread(() =>
            {
                admitted.Signal();
                start.Wait();
                for (var sample = 0; sample < samplesPerWorker; sample++)
                {
                    recorder.AddVerificationTimestampTicks(workerIndex + 1);
                    recorder.CountVerifiedArtifact();
                }
            });
            workers[worker].Start();
        }

        var allAdmitted = admitted.Wait(TimeSpan.FromSeconds(10));
        start.Set();
        var allJoined = true;
        foreach (var worker in workers) allJoined &= worker.Join(TimeSpan.FromSeconds(10));
        await Assert.That(allAdmitted && allJoined).IsTrue();

        await Assert.That(recorder.VerificationTimestampTicks).IsEqualTo(expectedTicks);
        await Assert.That(recorder.ToTimings()!.VerifiedArtifactCount).IsEqualTo(workerCount * samplesPerWorker);
    }

    [Test]
    public async Task FractionalMillisecondSamplesAreConvertedAfterAccumulation()
    {
        const int sampleCount = 4000;
        var sampleTicks = Stopwatch.Frequency / 2000;
        await Assert.That(sampleTicks > 0 && sampleTicks * 1000 < Stopwatch.Frequency).IsTrue();

        var recorder = new SiteGenerator.BuildTimingRecorder(enabled: true);
        for (var sample = 0; sample < sampleCount; sample++)
            recorder.AddVerificationTimestampTicks(sampleTicks);

        var expectedTicks = sampleTicks * sampleCount;
        var expectedMilliseconds = expectedTicks / Stopwatch.Frequency * 1000 +
                                   expectedTicks % Stopwatch.Frequency * 1000 / Stopwatch.Frequency;
        await Assert.That(recorder.VerificationTimestampTicks).IsEqualTo(expectedTicks);
        await Assert.That(recorder.ToTimings()!.VerificationMilliseconds).IsEqualTo(expectedMilliseconds);
    }

    [Test]
    public async Task DisabledRecorderReturnsNull()
    {
        var recorder = new SiteGenerator.BuildTimingRecorder(enabled: false);
        recorder.AddVerificationTimestampTicks(123);
        recorder.CountVerifiedArtifact();

        await Assert.That(recorder.VerificationTimestampTicks).IsEqualTo(0L);
        await Assert.That(recorder.ToTimings()).IsNull();
    }
}
