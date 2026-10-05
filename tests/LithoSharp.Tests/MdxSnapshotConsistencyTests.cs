using System.Security.Cryptography;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Mdx;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class MdxSnapshotConsistencyTests
{
    [Test]
    [Arguments("entry")]
    [Arguments("include")]
    public async Task PhysicalInputEditAfterRealLoaderCaptureRejectsStaleCompilation(string input)
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        var entryFile = Path.Combine(content, "page.mdx");
        var includeFile = Path.Combine(content, "included.cs");
        var oldBody = input == "entry" ? "# CapturedOldMarker\n" : "# Page\n\n```cs source=\"included.cs\"\n```\n";
        await File.WriteAllTextAsync(entryFile, "---\ntitle: Page\n---\n" + oldBody);
        await File.WriteAllTextAsync(includeFile, "// CapturedOldMarker\n");
        var inner = new MdxContentCollectionLoader<MdxIntegrationTests.FrontMatter>(new("snapshot"), content,
            _ => SiteRoute.ForDirectoryIndex("page"), entry => new PageMetadata(entry.FrontMatter.Title));
        var loader = new CapturingLoader(inner);
        await using var mdx = new MdxSite(new(workspace.Root, Path.Combine(FindRepository(), "src/LithoSharp.Mdx/worker"))
            { Cacheable = false });
        mdx.AddCollection(loader);
        var output = Path.Combine(workspace.Root, "out");
        var options = new SiteGenerationOptions { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch };
        var settings = new SiteSettings { BaseUrl = "https://example.com/project/" };
        var generator = new SiteGenerator();
        await generator.GenerateWithOptionsAsync(settings, [], output, true, null, options, default);
        var published = HashOutput(output);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "page/index.html"))).Contains("CapturedOldMarker");

        var barrier = loader.Arm();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var attempt = generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, cancellation.Token);
        try
        {
            await barrier.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
            // The actual built-in loader has completed all raw reads and inclusion resolution.
            // Edit physical input before the wrapper returns those captured entries to MdxSite.
            await File.WriteAllTextAsync(input == "entry" ? entryFile : includeFile,
                input == "entry" ? "---\ntitle: Page\n---\n# CurrentNewMarker\n" : "// CurrentNewMarker\n");
        }
        catch
        {
            cancellation.Cancel();
            try { await attempt; } catch { /* Observe the bounded attempt; preserve the barrier failure. */ }
            throw;
        }
        finally { barrier.Release.TrySetResult(); }

        SiteBuildExtensionException? failure = null;
        try { await attempt; }
        catch (SiteBuildExtensionException error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Diagnostics.Any(diagnostic => diagnostic.Id == "LSMDX005")).IsTrue();
        await Assert.That(HashOutput(output)).IsEquivalentTo(published);

        // A fresh real load supplies a consistent new snapshot and recovers without recreating the extension.
        await generator.GenerateWithOptionsAsync(settings, [], output, false, null, options, default);
        var html = await File.ReadAllTextAsync(Path.Combine(output, "page/index.html"));
        await Assert.That(html).Contains("CurrentNewMarker");
        await Assert.That(html).DoesNotContain("CapturedOldMarker");
    }

    [Test]
    public async Task RealLoaderCapturesOriginalRawHashesBeforeFrontMatterAndRegionTransformations()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        var rawEntry = "---\ntitle: Snapshot\n---\n# Snapshot\n\n```cs source=\"included.cs\" region=\"Example\"\n```\n";
        var rawInclude = "// Outside selected region\r\n#region Example\r\n// CapturedRegion\r\n#endregion\r\n";
        await File.WriteAllTextAsync(Path.Combine(content, "page.mdx"), rawEntry);
        await File.WriteAllTextAsync(Path.Combine(content, "included.cs"), rawInclude);
        var loader = new MdxContentCollectionLoader<MdxIntegrationTests.FrontMatter>(new("raw"), content,
            _ => SiteRoute.ForDirectoryIndex("page"), entry => new PageMetadata(entry.FrontMatter.Title));
        var loaded = await loader.LoadAsync();
        await Assert.That(loaded.IsSuccess).IsTrue();
        var entry = loaded.Collection!.Entries.Single();
        await Assert.That(entry.Body.Body).Contains("CapturedRegion");
        await Assert.That(entry.Body.Body).DoesNotContain("Outside selected region");
        await Assert.That(entry.CapturedInputs.Count).IsEqualTo(2);
        await Assert.That(entry.Body.CapturedInputs).IsEquivalentTo(entry.CapturedInputs);
        foreach (var input in entry.CapturedInputs)
            await Assert.That(input.Hash).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(input.File))));
        await Assert.That(entry.SourceFingerprint).IsEqualTo("sha256:" + entry.CapturedInputs.Single(input => Path.GetFileName(input.File) == "page.mdx").Hash);
        await Assert.That(entry.CapturedInputs.Single(input => Path.GetFileName(input.File) == "included.cs").Hash)
            .IsNotEqualTo(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("// CapturedRegion"))));
    }

    [Test]
    public async Task CallerConstructedInMemoryBodyKeepsExplicitCallerSourceSemantics()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "page.mdx"), "# DifferentPhysicalText\n");
        var body = new MdxDocument("# ExplicitCallerBody\n");
        var entry = new ContentEntry<MdxIntegrationTests.FrontMatter, MdxDocument>(new("page"), "page.mdx", "caller-source-v1",
            new() { Title = "Caller" }, body);
        await Assert.That(body.CapturedInputs.Count).IsEqualTo(0);
        await using var mdx = new MdxSite(new(workspace.Root, Path.Combine(FindRepository(), "src/LithoSharp.Mdx/worker")));
        mdx.AddCollection(new MemoryLoader(new(new("memory"), content, [entry],
            _ => SiteRoute.ForDirectoryIndex("page"), value => new PageMetadata(value.FrontMatter.Title))));
        var output = Path.Combine(workspace.Root, "out");
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], output, true, null,
            new() { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
        var html = await File.ReadAllTextAsync(Path.Combine(output, "page/index.html"));
        await Assert.That(html).Contains("ExplicitCallerBody");
        await Assert.That(html).DoesNotContain("DifferentPhysicalText");
    }

    private sealed class MemoryLoader(ContentCollection<MdxIntegrationTests.FrontMatter, MdxDocument> collection)
        : IContentCollectionLoader<MdxIntegrationTests.FrontMatter, MdxDocument>
    {
        public ValueTask<ContentLoadResult<MdxIntegrationTests.FrontMatter, MdxDocument>> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ContentLoadResult<MdxIntegrationTests.FrontMatter, MdxDocument>.Success(collection));
    }

    private sealed class CapturingLoader(IContentCollectionLoader<MdxIntegrationTests.FrontMatter, MdxDocument> inner)
        : IContentCollectionLoader<MdxIntegrationTests.FrontMatter, MdxDocument>
    {
        private Barrier? armed;
        public Barrier Arm()
        {
            var barrier = new Barrier();
            if (Interlocked.CompareExchange(ref armed, barrier, null) is not null)
                throw new InvalidOperationException("The real-loader barrier is already armed.");
            return barrier;
        }
        public async ValueTask<ContentLoadResult<MdxIntegrationTests.FrontMatter, MdxDocument>> LoadAsync(CancellationToken cancellationToken = default)
        {
            var loaded = await inner.LoadAsync(cancellationToken);
            var barrier = Interlocked.Exchange(ref armed, null);
            if (barrier is not null)
            {
                barrier.Captured.TrySetResult();
                await barrier.Release.Task.WaitAsync(cancellationToken);
            }
            return loaded;
        }
    }
    private sealed class Barrier
    {
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static Dictionary<string, string> HashOutput(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(file => !Path.GetFileName(file).StartsWith('.')).ToDictionary(file => Path.GetRelativePath(directory, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    private static string FindRepository()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx"))) return path.FullName;
        throw new DirectoryNotFoundException("The MDX integration fixture requires the repository's restored worker.");
    }
}
