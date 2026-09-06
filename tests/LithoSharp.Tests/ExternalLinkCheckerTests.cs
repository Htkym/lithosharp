using System.Net;
using System.Net.Http;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;

namespace LithoSharp.Tests;

public sealed class ExternalLinkCheckerTests
{
    [Test]
    public async Task IsPublicAddress_RejectsPrivateAndAcceptsPublic()
    {
        foreach (var value in new[] { "0.0.0.0", "10.0.0.1", "100.64.0.1", "127.0.0.1", "169.254.1.1", "172.16.0.1", "192.0.2.1", "192.168.0.1", "198.18.0.1", "198.51.100.1", "203.0.113.1", "224.0.0.1", "::", "::1", "fc00::1", "fe80::1", "ff02::1", "2001:db8::1" })
            await Assert.That(ExternalLinkChecker.IsPublicAddress(IPAddress.Parse(value))).IsFalse();
        foreach (var value in new[] { "::2", "64:ff9b:1::1", "2002::1", "2001::1", "3fff::1" })
            await Assert.That(ExternalLinkChecker.IsPublicAddress(IPAddress.Parse(value))).IsFalse();
        foreach (var value in new[] { "8.8.8.8", "1.1.1.1", "198.51.101.1", "2001:4860:4860::8888" })
            await Assert.That(ExternalLinkChecker.IsPublicAddress(IPAddress.Parse(value))).IsTrue();
        await Assert.That(ExternalLinkChecker.IsPublicAddress(IPAddress.Parse("::ffff:8.8.8.8"))).IsTrue();
        await Assert.That(ExternalLinkChecker.IsPublicAddress(IPAddress.Parse("::ffff:10.0.0.1"))).IsFalse();
    }

    [Test]
    public async Task CheckAsync_UsesHeadThenGetOnlyFor405AndReportsFailures()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(new HttpResponseMessage(
            request.Method == HttpMethod.Head ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.NotFound)));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var result = await ExternalLinkChecker.CheckAsync(
            new Dictionary<string, SiteSourceLocation> { ["https://example.test/page"] = new("page.md") },
            new ExternalLinkCheckOptions(path, minimumRequestInterval: TimeSpan.Zero), CancellationToken.None, handler);

        await Assert.That(handler.Methods).IsEquivalentTo(["HEAD", "GET"]);
        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(result[0].Id).IsEqualTo("LSQ011");
        File.Delete(path);
    }

    [Test]
    public async Task CheckAsync_FollowsRedirectsManually()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/old"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/new", UriKind.Relative) } }
            : new HttpResponseMessage(HttpStatusCode.OK)));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var result = await ExternalLinkChecker.CheckAsync(
            new Dictionary<string, SiteSourceLocation> { ["https://example.test/old"] = new("page.md") },
            new ExternalLinkCheckOptions(path, minimumRequestInterval: TimeSpan.Zero), CancellationToken.None, handler);

        await Assert.That(result).IsEmpty();
        await Assert.That(handler.RequestUris).IsEquivalentTo(["https://example.test/old", "https://example.test/new"]);
        File.Delete(path);
    }

    [Test]
    public async Task CheckAsync_IgnoresMalformedAndInvalidCacheEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        foreach (var document in new[]
        {
            "not json",
            "{\"https://example.test/page\":null}",
            "{\"version\":0,\"entries\":{}}",
            "{\"version\":1,\"entries\":{\"https://example.test/page\":null}}",
            "{\"version\":1,\"entries\":{\"https://example.test/page\":{\"checkedAt\":\"2099-01-01T00:00:00Z\",\"status\":200,\"message\":\"\"}}}",
            "{\"version\":1,\"entries\":{\"https://example.test/page\":{\"checkedAt\":\"2026-01-01T00:00:00Z\",\"status\":999,\"message\":null}}}"
        })
        {
            File.WriteAllText(path, document);
            handler.Methods.Clear();
            var result = await ExternalLinkChecker.CheckAsync(new Dictionary<string, SiteSourceLocation> { ["https://example.test/page"] = new("page.md") }, new ExternalLinkCheckOptions(path, minimumRequestInterval: TimeSpan.Zero), CancellationToken.None, handler);
            await Assert.That(result).IsEmpty();
            await Assert.That(handler.Methods).Count().IsEqualTo(1);
        }
        File.Delete(path);
    }

    [Test]
    public async Task CheckAsync_UsesVersionedCacheOnSecondRun()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var options = new ExternalLinkCheckOptions(path, minimumRequestInterval: TimeSpan.Zero);
        var links = new Dictionary<string, SiteSourceLocation> { ["https://example.test/cached"] = new("page.md") };
        await ExternalLinkChecker.CheckAsync(links, options, CancellationToken.None, handler);
        handler.Methods.Clear();
        await ExternalLinkChecker.CheckAsync(links, options, CancellationToken.None, handler);

        await Assert.That(handler.Methods).IsEmpty();
        await Assert.That(File.ReadAllText(path)).Contains("\"version\":1");
        File.Delete(path);
    }

    [Test]
    public async Task CheckAsync_PropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new HttpResponseMessage(HttpStatusCode.OK); });
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        cancellation.Cancel();
        Func<Task> operation = async () => { await ExternalLinkChecker.CheckAsync(new Dictionary<string, SiteSourceLocation> { ["https://example.test/cancel"] = new("page.md") }, new ExternalLinkCheckOptions(path, minimumRequestInterval: TimeSpan.Zero), cancellation.Token, handler); };
        await Assert.That(operation).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task CheckAsync_PropagatesInFlightCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var operation = ExternalLinkChecker.CheckAsync(new Dictionary<string, SiteSourceLocation> { ["https://example.test/cancel"] = new("page.md") }, new ExternalLinkCheckOptions(path, requestTimeout: TimeSpan.FromSeconds(5)), cancellation.Token, handler);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await operation).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task CheckAsync_TimesOutAsWarning()
    {
        var handler = new RecordingHandler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new HttpResponseMessage(HttpStatusCode.OK); });
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var result = await ExternalLinkChecker.CheckAsync(new Dictionary<string, SiteSourceLocation> { ["https://example.test/slow"] = new("page.md") }, new ExternalLinkCheckOptions(path, requestTimeout: TimeSpan.FromMilliseconds(20), minimumRequestInterval: TimeSpan.Zero), CancellationToken.None, handler);
        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(result[0].Id).IsEqualTo("LSQ011");
        File.Delete(path);
    }

    [Test]
    public async Task CheckAsync_RejectsPrivateRedirectWithoutSendingIt()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://127.0.0.1/private") } }));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var result = await ExternalLinkChecker.CheckAsync(new Dictionary<string, SiteSourceLocation> { ["https://example.test/redirect"] = new("page.md") }, new ExternalLinkCheckOptions(path, minimumRequestInterval: TimeSpan.Zero), CancellationToken.None, handler);
        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(handler.RequestUris).IsEquivalentTo(["https://example.test/redirect"]);
        File.Delete(path);
    }

    [Test]
    public async Task CheckAsync_HonorsGlobalRateLimit()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await ExternalLinkChecker.CheckAsync(
            new Dictionary<string, SiteSourceLocation>
            {
                ["https://example.test/one"] = new("one.md"),
                ["https://example.test/two"] = new("two.md"),
            },
            new ExternalLinkCheckOptions(path, minimumRequestInterval: TimeSpan.FromMilliseconds(40)), CancellationToken.None, handler);

        await Assert.That(handler.RequestTimes[1] - handler.RequestTimes[0]).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(30));
        File.Delete(path);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        public List<string> RequestUris { get; } = [];
        public List<DateTimeOffset> RequestTimes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method.Method); RequestUris.Add(request.RequestUri!.ToString()); RequestTimes.Add(DateTimeOffset.UtcNow);
            return await callback(request, cancellationToken);
        }
    }
}
