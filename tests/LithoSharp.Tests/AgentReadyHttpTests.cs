using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;

namespace LithoSharp.Tests;

public sealed class AgentReadyHttpTests
{
    [Test]
    public async Task LocalServeMapsMarkdownWithoutStartingAServer()
    {
        var assembly = System.Reflection.Assembly.LoadFrom(FindTool());
        var mapping = assembly.GetType("LithoSharp.Tool.DevServer", throwOnError: true)!
            .GetMethod("ContentType", BindingFlags.NonPublic | BindingFlags.Static)!;
        await Assert.That((string)mapping.Invoke(null, ["guide.md"])!).IsEqualTo("text/markdown; charset=utf-8");
        await Assert.That((string)mapping.Invoke(null, ["guide.MD"])!).IsEqualTo("text/markdown; charset=utf-8");
        await Assert.That((string)mapping.Invoke(null, ["robots.txt"])!).IsEqualTo("text/plain; charset=utf-8");
    }

    [Test]
    public async Task ExplicitPublicCheckVerifiesMarkdownBytesMime404AndActualOriginRootRobots()
    {
        using var fixture = await Fixture.CreateAsync("https://example.test/project/");
        var handler = new Handler(fixture.Response);
        var report = await SitePublicationHttpInspector.InspectAsync(fixture.Manifest, default, handler, TimeSpan.Zero);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error)).IsFalse();
        await Assert.That(handler.Urls.Contains("https://example.test/robots.txt")).IsTrue();
        await Assert.That(handler.Urls.Contains("https://example.test/project/robots.txt")).IsFalse();
        await Assert.That(handler.Urls.Any(url => url.StartsWith("https://example.test/project/__lithosharp-missing-", StringComparison.Ordinal))).IsTrue();
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Id == "LSPHTTP004" && diagnostic.Message.Contains("does not manage", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments("application/octet-stream", "utf-8", "# Public\n")]
    [Arguments("text/markdown", null, "# Public\n")]
    [Arguments("text/markdown", "utf-8", "# Wrong deployed body\n")]
    public async Task PublicMarkdownRequiresCorrectMimeCharsetAndInspectedBody(string mime, string? charset, string body)
    {
        using var fixture = await Fixture.CreateAsync("https://example.test/project/");
        var handler = new Handler(uri => uri.AbsolutePath.EndsWith(".md", StringComparison.Ordinal)
            ? Text(HttpStatusCode.OK, mime, body, charset) : fixture.Response(uri));
        var report = await SitePublicationHttpInspector.InspectAsync(fixture.Manifest, default, handler, TimeSpan.Zero);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Id == "LSPHTTP001" && diagnostic.Severity == SiteDiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task Soft404IsRejectedInsteadOfClaimingAnUnknownUrlWasHandled()
    {
        using var fixture = await Fixture.CreateAsync("https://example.test/project/");
        var handler = new Handler(uri => uri.AbsolutePath.Contains("__lithosharp-missing-", StringComparison.Ordinal)
            ? Text(HttpStatusCode.OK, "text/html", "Not found") : fixture.Response(uri));
        var report = await SitePublicationHttpInspector.InspectAsync(fixture.Manifest, default, handler, TimeSpan.Zero);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Id == "LSPHTTP003" && diagnostic.Severity == SiteDiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task DeclaredDirectoryAliasRedirectIsObservedAndPrivateRedirectIsNeverSent()
    {
        using var fixture = await Fixture.CreateAsync("https://example.test/project/");
        var handler = new Handler(uri => uri.AbsolutePath.EndsWith("index.html", StringComparison.Ordinal)
            ? new(HttpStatusCode.MovedPermanently) { Headers = { Location = new("/project/", UriKind.Relative) } }
            : fixture.Response(uri));
        var report = await SitePublicationHttpInspector.InspectAsync(fixture.Manifest, default, handler, TimeSpan.Zero);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error)).IsFalse();
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Id == "LSPHTTP002")).IsTrue();
        handler = new(uri => uri.AbsolutePath.EndsWith(".md", StringComparison.Ordinal)
            ? new(HttpStatusCode.Redirect) { Headers = { Location = new("http://127.0.0.1/private") } }
            : fixture.Response(uri));
        report = await SitePublicationHttpInspector.InspectAsync(fixture.Manifest, default, handler, TimeSpan.Zero);
        await Assert.That(handler.Urls.Any(url => url.Contains("127.0.0.1", StringComparison.Ordinal))).IsFalse();
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Id == "LSPHTTP001" && diagnostic.Severity == SiteDiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    [Arguments("https://example.test/文書/")]
    [Arguments("https://example.test/%E6%96%87%E6%9B%B8/")]
    public async Task UnicodeAndPercentOutputRoutesUseTheSameNormalizedUriIdentity(string baseUrl)
    {
        using var fixture = await Fixture.CreateAsync(baseUrl, markdownPath: "ja/概要%.md");
        var handler = new Handler(fixture.Response);
        var report = await SitePublicationHttpInspector.InspectAsync(fixture.Manifest, default, handler, TimeSpan.Zero);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error)).IsFalse();
        await Assert.That(handler.Urls.Any(url => url.Contains("/%E6%A6%82%E8%A6%81%25.md", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Direct404ArtifactCanReturn404WithoutHidingTheUnknownUrlProbe()
    {
        using var fixture = await Fixture.CreateAsync("https://example.test/project/", notFoundArtifact: true);
        var handler = new Handler(uri => uri.AbsolutePath.EndsWith("/404.html", StringComparison.Ordinal)
            ? Text(HttpStatusCode.NotFound, "text/html", "Not found") : fixture.Response(uri));
        var report = await SitePublicationHttpInspector.InspectAsync(fixture.Manifest, default, handler, TimeSpan.Zero);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error)).IsFalse();
    }

    [Test]
    public async Task RootOwnedRobotsMustMatchTheInspectedArtifact()
    {
        using var fixture = await Fixture.CreateAsync("https://example.test/", rootRobots: true);
        var handler = new Handler(fixture.Response);
        var report = await SitePublicationHttpInspector.InspectAsync(fixture.Manifest, default, handler, TimeSpan.Zero);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error)).IsFalse();
        handler = new(uri => uri.AbsolutePath == "/robots.txt" ? Text(HttpStatusCode.OK, "text/plain", "User-agent: *\nDisallow:\n") : fixture.Response(uri));
        report = await SitePublicationHttpInspector.InspectAsync(fixture.Manifest, default, handler, TimeSpan.Zero);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Id == "LSPHTTP004" && diagnostic.Severity == SiteDiagnosticSeverity.Error)).IsTrue();
    }

    [Test]
    public async Task CancellationAndPreInspectionDoNotSendRequests()
    {
        using var fixture = await Fixture.CreateAsync("https://example.test/project/");
        var handler = new Handler(fixture.Response);
        _ = await GeneratedSiteInspector.InspectAsync(fixture.Output, fixture.Manifest.BaseUrl);
        await Assert.That(handler.Urls.Count).IsEqualTo(0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await SitePublicationHttpInspector.InspectAsync(fixture.Manifest, cancellation.Token, handler, TimeSpan.Zero)).Throws<OperationCanceledException>();
        await Assert.That(handler.Urls.Count).IsEqualTo(0);
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string mime, string body, string? charset = "utf-8")
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mime) { CharSet = charset };
        return response;
    }

    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request.RequestUri));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryWorkspace workspace = new();
        internal string Output => Path.Combine(workspace.Root, "output");
        internal SitePublicationManifest Manifest { get; private set; } = null!;
        private bool ownsRobots;
        private const string Markdown = "# Public\n";
        private const string Robots = "User-agent: *\nDisallow: /private/\n";
        internal static async Task<Fixture> CreateAsync(string baseUrl, bool rootRobots = false, bool notFoundArtifact = false, string markdownPath = "guide.md")
        {
            var fixture = new Fixture { ownsRobots = rootRobots };
            Directory.CreateDirectory(fixture.Output);
            await File.WriteAllTextAsync(Path.Combine(fixture.Output, "index.html"),
                $"<html lang='en'><head><title>Home</title><meta name='description' content='Description'><meta property='og:title' content='Home'><meta property='og:description' content='Description'><meta property='og:url' content='{baseUrl}'><link rel='canonical' href='{baseUrl}'></head><body><a href='{string.Join("/", markdownPath.Split('/').Select(Uri.EscapeDataString))}'>Guide</a></body></html>");
            var markdownFile = Path.Combine(fixture.Output, markdownPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(markdownFile)!);
            await File.WriteAllTextAsync(markdownFile, Markdown);
            if (rootRobots)
                await File.WriteAllTextAsync(Path.Combine(fixture.Output, "robots.txt"), Robots);
            if (notFoundArtifact)
                await File.WriteAllTextAsync(Path.Combine(fixture.Output, "404.html"),
                    $"<html><head><title>Not found</title><meta name='description' content='Not found'><meta property='og:title' content='Not found'><meta property='og:description' content='Not found'><meta property='og:url' content='{baseUrl}404.html'><link rel='canonical' href='{baseUrl}404.html'></head><body>Not found</body></html>");
            var report = await GeneratedSiteInspector.InspectPublicationAsync(fixture.Output, baseUrl, "http-tests", []);
            fixture.Manifest = report.PublicationManifest ?? throw new InvalidOperationException(report.Format(SiteDiagnosticFormat.Text));
            return fixture;
        }
        internal HttpResponseMessage Response(Uri uri) => uri.AbsolutePath.EndsWith(".md", StringComparison.Ordinal)
            ? Text(HttpStatusCode.OK, "text/markdown", Markdown)
            : uri.AbsolutePath == "/robots.txt" ? Text(ownsRobots ? HttpStatusCode.OK : HttpStatusCode.NotFound, "text/plain", ownsRobots ? Robots : "")
            : uri.AbsolutePath.Contains("__lithosharp-missing-", StringComparison.Ordinal) ? Text(HttpStatusCode.NotFound, "text/html", "Not found")
            : Text(HttpStatusCode.OK, "text/html", "Home");
        public void Dispose() => workspace.Dispose();
    }

    private static string FindTool()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "LithoSharp.Tool", "bin", "Release", "net10.0", "LithoSharp.Tool.dll");
            if (File.Exists(path))
                return path;
        }
        throw new InvalidOperationException("Build the Release Tool before running the pure local-serve MIME mapping test. No server is started by this test.");
    }
}
