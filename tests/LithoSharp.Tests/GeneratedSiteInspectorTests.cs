using System.Security.Cryptography;
using System.Text;
using TUnit.Core.Enums;
using LithoSharp.Quality;

namespace LithoSharp.Tests;

public sealed class GeneratedSiteInspectorTests
{
    private const string BaseUrl = "https://example.test/guide/";

    [Test]
    public async Task ReadsActualJapaneseBasePathOutputWithoutWritesAndRechecksChangedBytes()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(Path.Combine(output, "話題"));
        Directory.CreateDirectory(Path.Combine(output, "styles"));
        var home = Path.Combine(output, "index.html");
        await File.WriteAllTextAsync(home, Page("index.html", "Home",
            "<a href='話題/index.html#節'>Topic</a><link rel='stylesheet' href='styles/site.css'>"));
        await File.WriteAllTextAsync(Path.Combine(output, "話題", "index.html"), Page("%E8%A9%B1%E9%A1%8C/", "Topic", "<h2 id='節'>Section</h2>"));
        await File.WriteAllTextAsync(Path.Combine(output, "styles", "site.css"), "body { background:url('../picture.png') }");
        await File.WriteAllBytesAsync(Path.Combine(output, "picture.png"), [0, 255, 1]);
        await File.WriteAllTextAsync(Path.Combine(output, ".lithosharp-output-manifest.json"), "{\"version\":1,\"files\":[\"absent.html\"]}");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "source-sentinel.txt"), "source");
        var before = Snapshot(workspace.Root);
        var report = await GeneratedSiteInspector.InspectAsync(output, BaseUrl);
        await Assert.That(report.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(report.HtmlParseCount).IsEqualTo(2);
        await Assert.That(report.HtmlFactsCacheHitCount).IsEqualTo(0);
        await Assert.That(Snapshot(workspace.Root).SequenceEqual(before)).IsTrue();

        var timestamp = File.GetLastWriteTimeUtc(home);
        var original = await File.ReadAllTextAsync(home);
        await File.WriteAllTextAsync(home, original.Replace("#節", "#欠", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(home, timestamp);
        before = Snapshot(workspace.Root);
        report = await GeneratedSiteInspector.InspectAsync(output, BaseUrl);
        await Assert.That(report.Diagnostics.Single(diagnostic => diagnostic.Id == "LSQ002").Location!.FilePath).IsEqualTo("index.html");
        await Assert.That(Snapshot(workspace.Root).SequenceEqual(before)).IsTrue();
    }

    [Test]
    public async Task NativeRedirectsKeepIndependentCanonicalValidationAndReportChainsCyclesAndMissingTargets()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "index.html"), Page("index.html", "Home", ""));
        var old = Path.Combine(workspace.Root, "old.html");
        await File.WriteAllTextAsync(old, Redirect("index.html"));
        var report = await GeneratedSiteInspector.InspectAsync(workspace.Root, BaseUrl);
        await Assert.That(report.Diagnostics.Count).IsEqualTo(0);
        await File.WriteAllTextAsync(old, Redirect("index.html").Replace("rel='canonical' href='" + BaseUrl + "index.html'",
            "rel='canonical' href='" + BaseUrl + "old.html'", StringComparison.Ordinal));
        report = await GeneratedSiteInspector.InspectAsync(workspace.Root, BaseUrl);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Id == "LSQ003")).IsTrue();

        await File.WriteAllTextAsync(old, Redirect("middle.html"));
        var middle = Path.Combine(workspace.Root, "middle.html");
        await File.WriteAllTextAsync(middle, Redirect("index.html"));
        report = await GeneratedSiteInspector.InspectAsync(workspace.Root, BaseUrl);
        await Assert.That(report.Diagnostics.Single(diagnostic => diagnostic.Id == "LSQ005").Location!.FilePath).IsEqualTo("old.html");
        await File.WriteAllTextAsync(middle, Redirect("old.html"));
        report = await GeneratedSiteInspector.InspectAsync(workspace.Root, BaseUrl);
        await Assert.That(report.Diagnostics.Count(diagnostic => diagnostic.Id == "LSQ004")).IsEqualTo(2);
        await File.WriteAllTextAsync(old, Redirect("missing.html"));
        report = await GeneratedSiteInspector.InspectAsync(workspace.Root, BaseUrl);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Id == "LSQ006")).IsTrue();
    }

    [Test]
    public async Task RejectsOutputLinksAndRootAncestorLinksWithoutChangingOutsideFiles()
    {
        using var workspace = new TemporaryWorkspace();
        using var outside = new TemporaryWorkspace();
        var site = Path.Combine(outside.Root, "site");
        Directory.CreateDirectory(site);
        await File.WriteAllTextAsync(Path.Combine(site, "index.html"), Page("index.html", "Home", ""));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "index.html"), Page("index.html", "Home", ""));
        var before = Snapshot(outside.Root);
        var link = Path.Combine(workspace.Root, "linked");
        await CreateDirectoryLink(link, outside.Root);
        try
        {
            await Assert.That(async () => await GeneratedSiteInspector.InspectAsync(workspace.Root, BaseUrl)).Throws<InvalidOperationException>();
            await Assert.That(async () => await GeneratedSiteInspector.InspectAsync(link, BaseUrl)).Throws<InvalidOperationException>();
            await Assert.That(async () => await GeneratedSiteInspector.InspectAsync(Path.Combine(link, "site"), BaseUrl)).Throws<InvalidOperationException>();
            await Assert.That(Snapshot(outside.Root).SequenceEqual(before)).IsTrue();
        }
        finally { Directory.Delete(link); }
    }

    [Test]
    public async Task AcceptsOnlyUtf8WithOptionalBomForHtmlAndCss()
    {
        using var workspace = new TemporaryWorkspace();
        var home = Path.Combine(workspace.Root, "index.html");
        var stylesheet = Path.Combine(workspace.Root, "site.css");
        var html = Page("index.html", "Home", "日本語<link rel='stylesheet' href='site.css'>");
        const string css = "/* 日本語 */ body { color: black }";
        foreach (var encoding in new[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true) })
        {
            await File.WriteAllBytesAsync(home, [.. encoding.GetPreamble(), .. encoding.GetBytes(html)]);
            await File.WriteAllBytesAsync(stylesheet, [.. encoding.GetPreamble(), .. encoding.GetBytes(css)]);
            var report = await GeneratedSiteInspector.InspectAsync(workspace.Root, BaseUrl);
            await Assert.That(report.Diagnostics.Count).IsEqualTo(0);
        }
        var encodings = new Encoding[] { Encoding.Unicode, Encoding.BigEndianUnicode,
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) };
        var rejected = encodings.Select(encoding => encoding.GetPreamble().Concat(encoding.GetBytes(html)).ToArray())
            .Prepend([0xc3, 0x28]).Prepend([0xef, 0xbb, 0xbf, 0xc3, 0x28]);
        foreach (var bytes in rejected)
        {
            await File.WriteAllTextAsync(stylesheet, css);
            await File.WriteAllBytesAsync(home, bytes);
            await Assert.That(async () => await GeneratedSiteInspector.InspectAsync(workspace.Root, BaseUrl)).Throws<DecoderFallbackException>();
            await File.WriteAllTextAsync(home, html);
            await File.WriteAllBytesAsync(stylesheet, bytes);
            await Assert.That(async () => await GeneratedSiteInspector.InspectAsync(workspace.Root, BaseUrl)).Throws<DecoderFallbackException>();
        }
    }

    [Test, RunOn(OS.Linux | OS.MacOs)]
    public async Task RejectsUnixLiteralBackslashNamesInsteadOfInventingBinaryTargets()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "index.html"),
            Page("index.html", "Home", "<img src='assets/picture.png'>"));
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, @"assets\picture.png"), [0, 255, 1]);
        await Assert.That(async () => await GeneratedSiteInspector.InspectAsync(workspace.Root, BaseUrl))
            .Throws<InvalidOperationException>().WithMessageContaining("literal backslash");
    }

    [Test, RunOn(OS.Linux | OS.MacOs)]
    public async Task RejectsUnixFifosBeforeOpeningHtmlOrAcceptingBinaryTargets()
    {
        const string childRootVariable = "LITHOSHARP_OWNED_OUTPUT_FIFO_ROOT";
        if (Environment.GetEnvironmentVariable(childRootVariable) is { } childRoot)
        {
            await Assert.That(async () => await GeneratedSiteInspector.InspectAsync(childRoot, BaseUrl))
                .Throws<InvalidOperationException>().WithMessageContaining("regular file");
            return;
        }
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "index.html"), Page("index.html", "Home", ""));
        foreach (var name in new[] { "stuck.html", "asset.bin" })
        {
            var fifo = Path.Combine(workspace.Root, name);
            var start = new System.Diagnostics.ProcessStartInfo("mkfifo") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(fifo);
            try
            {
                using (var process = System.Diagnostics.Process.Start(start)!)
                {
                    await WaitOwnedProcessAsync(process, TimeSpan.FromSeconds(5));
                    await Assert.That(process.ExitCode).IsEqualTo(0);
                }
                var childStart = new System.Diagnostics.ProcessStartInfo("dotnet")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                childStart.ArgumentList.Add(typeof(GeneratedSiteInspectorTests).Assembly.Location);
                childStart.ArgumentList.Add("--treenode-filter");
                childStart.ArgumentList.Add("/*/*/GeneratedSiteInspectorTests/RejectsUnixFifosBeforeOpeningHtmlOrAcceptingBinaryTargets");
                childStart.Environment[childRootVariable] = workspace.Root;
                using var child = System.Diagnostics.Process.Start(childStart)!;
                var stdout = child.StandardOutput.ReadToEndAsync();
                var stderr = child.StandardError.ReadToEndAsync();
                try
                {
                    await WaitOwnedProcessAsync(child, TimeSpan.FromSeconds(15));
                    if (child.ExitCode != 0)
                        throw new InvalidOperationException(string.Concat(await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5))));
                }
                finally { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            finally { File.Delete(fifo); }
        }
    }

    private static async Task WaitOwnedProcessAsync(System.Diagnostics.Process process, TimeSpan timeout)
    {
        try { await process.WaitForExitAsync().WaitAsync(timeout); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static string[] Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + File.GetLastWriteTimeUtc(path).Ticks
            + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static string Page(string publicPath, string title, string body) =>
        $"<!doctype html><html><head><title>{title}</title><link rel='canonical' href='{BaseUrl}{publicPath}'><meta name='description' content='Description'><meta property='og:title' content='{title}'><meta property='og:description' content='Description'><meta property='og:url' content='{BaseUrl}{publicPath}'></head><body>{body}</body></html>";

    private static string Redirect(string target) =>
        $"<!doctype html><html><head><title>Redirect</title><link rel='canonical' href='{BaseUrl}{target}'><meta http-equiv='refresh' content='0;url={BaseUrl}{target}'></head><body><a href='{BaseUrl}{target}'>Continue</a></body></html>";

    private static async Task CreateDirectoryLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(path); start.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        await Assert.That(process.ExitCode).IsEqualTo(0);
    }
}
