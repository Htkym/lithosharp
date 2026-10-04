using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
using SkiaSharp;

namespace LithoSharp.Tests;

public sealed class SocialImageGeneratorTests
{
    private const string IconPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Test]
    public async Task SiteAndPostImagesDrawTitleAndSubtitleGlyphs()
    {
        foreach (var bold in new[] { true, false })
        {
            using var face = SocialImageGenerator.ResolveTypeface(bold);
            await Assert.That(face.GetGlyphs("LithoSharp titleRegular subtitleBold subtitle").All(glyph => glyph != 0)).IsTrue();
        }
        var generator = new SocialImageGenerator(Convert.FromBase64String(IconPng));
        var fingerprint = SocialImageGenerator.GetImplementationFingerprint();
        await Assert.That(fingerprint).IsNotNull();
        foreach (var bytes in new[]
                 {
                     await generator.BuildSiteImageAsync("LithoSharp title", "Regular subtitle", CancellationToken.None),
                     await generator.BuildPostImageAsync("LithoSharp title", "Bold subtitle", CancellationToken.None),
                 })
        {
            using var bitmap = SKBitmap.Decode(bytes)
                ?? throw new InvalidOperationException("Generated social PNG did not decode.");
            await Assert.That(CountTextPixels(bitmap, 214, 300)).IsGreaterThan(100);
            await Assert.That(CountTextPixels(bitmap, 314, 400)).IsGreaterThan(100);
        }

        await Assert.That(SocialImageGenerator.GetImplementationFingerprint()).IsEqualTo(fingerprint);
    }

    [Test]
    public async Task SocialImagesReuseTheFingerprintedFontAndInvalidateChangedPostTitle()
    {
        using var workspace = new TemporaryWorkspace();
        var favicon = Path.Combine(workspace.Root, "favicon");
        Directory.CreateDirectory(favicon);
        await File.WriteAllBytesAsync(Path.Combine(favicon, "android-chrome-192x192.png"),
            Convert.FromBase64String(IconPng));
        var output = Path.Combine(workspace.Root, "output");
        var site = new SiteSettings
        {
            Title = "LithoSharp title", Description = "Social image test",
            BaseUrl = "https://example.test/", Language = "en", TimeZone = "UTC",
        };
        var customization = new SiteCustomization
        {
            Template = new BlogSiteTemplate(), FaviconSourceDirectory = favicon,
        };
        var timestamp = new DateTimeOffset(2026, 8, 30, 10, 11, 12, TimeSpan.Zero);
        var post = new MarkdownPost("content/post.md", "post",
            new PostFrontMatter { Title = "Original post", Date = timestamp.AddDays(-1) },
            "# Heading\n\nBody.", "posts/post.html");
        async Task<SiteGenerationResult> GenerateAsync(MarkdownPost input, bool clean) =>
            await new SiteGenerator().GenerateWithOptionsAsync(site, [input], output, clean,
                customization, new SiteGenerationOptions { BuildTimestamp = timestamp }, CancellationToken.None);

        var first = await GenerateAsync(post, clean: true);
        var socialNodes = first.BuildPlan.Nodes.Where(node => node.Id.Value.StartsWith("social:", StringComparison.Ordinal)).ToArray();
        await Assert.That(socialNodes.Length).IsEqualTo(2);
        foreach (var node in socialNodes)
        {
            await Assert.That(node.Inputs.Single(input => input.Key == "social.implementation").Value)
                .IsEqualTo(SocialImageGenerator.GetImplementationFingerprint());
            using var bitmap = SKBitmap.Decode(await File.ReadAllBytesAsync(
                Path.Combine(output, node.Artifacts.Single().RelativeOutputPath)))!;
            await Assert.That(CountTextPixels(bitmap, 214, 300)).IsGreaterThan(100);
            await Assert.That(CountTextPixels(bitmap, 314, 400)).IsGreaterThan(100);
        }

        var before = await File.ReadAllBytesAsync(Path.Combine(output,
            socialNodes.Single(node => node.Id.Value.StartsWith("social:post:", StringComparison.Ordinal))
                .Artifacts.Single().RelativeOutputPath));
        var unchanged = await GenerateAsync(post, clean: false);
        var unchangedSocial = unchanged.BuildReport.Nodes
            .Where(node => node.NodeId.StartsWith("social:", StringComparison.Ordinal)).ToArray();
        await Assert.That(unchangedSocial.Select(node => node.NodeId).ToArray())
            .IsEquivalentTo(socialNodes.Select(node => node.Id.Value).ToArray());
        await Assert.That(unchangedSocial.All(node => node.CacheHit)).IsTrue();
        await Assert.That(unchanged.BuildPlan.GetInvalidatedNodes(first.BuildPlan)
            .Any(item => item.NodeId.Value.StartsWith("social:", StringComparison.Ordinal))).IsFalse();

        var changed = await GenerateAsync(post with { FrontMatter = post.FrontMatter with { Title = "Changed post" } }, clean: false);
        await Assert.That(changed.BuildReport.Nodes.Single(node => node.NodeId == "social:default").CacheHit).IsTrue();
        await Assert.That(changed.BuildReport.Nodes.Single(node => node.NodeId.StartsWith("social:post:", StringComparison.Ordinal)).CacheHit).IsFalse();
        var after = await File.ReadAllBytesAsync(Path.Combine(output,
            socialNodes.Single(node => node.Id.Value.StartsWith("social:post:", StringComparison.Ordinal))
                .Artifacts.Single().RelativeOutputPath));
        await Assert.That(after.SequenceEqual(before)).IsFalse();
    }

    [Test]
    public async Task CapturedFontBytesSurviveFileReplacementAndRemoval()
    {
        using var workspace = new TemporaryWorkspace();
        using var selectedBold = SocialImageGenerator.ResolveTypeface(isBold: true);
        using var selectedNormal = SocialImageGenerator.ResolveTypeface(isBold: false);
        var (boldBytes, boldIndex) = ReadFontBytes(selectedBold);
        var (normalBytes, normalIndex) = ReadFontBytes(selectedNormal);
        var boldPath = Path.Combine(workspace.Root, "bold.ttf");
        var normalPath = Path.Combine(workspace.Root, "normal.ttf");
        await File.WriteAllBytesAsync(boldPath, boldBytes);
        await File.WriteAllBytesAsync(normalPath, normalBytes);
        SocialImageGenerator generator;
        using (var bold = SKTypeface.FromFile(boldPath, boldIndex)!)
        using (var normal = SKTypeface.FromFile(normalPath, normalIndex)!)
        {
            generator = new SocialImageGenerator(Convert.FromBase64String(IconPng), bold, normal);
        }
        var fingerprint = generator.GetFontImplementationFingerprint();
        var before = await generator.BuildSiteImageAsync("LithoSharp title", "Regular subtitle", CancellationToken.None);
        await File.WriteAllBytesAsync(boldPath, [0, 1, 2]);
        File.Delete(normalPath);
        var after = await generator.BuildSiteImageAsync("LithoSharp title", "Regular subtitle", CancellationToken.None);
        await Assert.That(after.SequenceEqual(before)).IsTrue();
        await Assert.That(generator.GetFontImplementationFingerprint()).IsEqualTo(fingerprint);
        using var bitmap = SKBitmap.Decode(after)!;
        await Assert.That(CountTextPixels(bitmap, 214, 300)).IsGreaterThan(100);
        await Assert.That(CountTextPixels(bitmap, 314, 400)).IsGreaterThan(100);
    }

    private static (byte[] Bytes, int Index) ReadFontBytes(SKTypeface face)
    {
        using var stream = face.OpenStream(out var index);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer, buffer.Length)) > 0) output.Write(buffer, 0, count);
        return (output.ToArray(), index);
    }

    private static int CountTextPixels(SKBitmap bitmap, int top, int bottom)
    {
        var background = new SKColor(22, 27, 34);
        var count = 0;
        // Brand icon ends at x=258; this region contains only the rendered text.
        for (var y = top; y < bottom; y++)
            for (var x = 278; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y) != background) count++;
        return count;
    }
}
