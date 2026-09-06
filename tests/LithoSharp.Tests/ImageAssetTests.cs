using LithoSharp.Configuration;
using LithoSharp.Images;
using SkiaSharp;

namespace LithoSharp.Tests;

public sealed class ImageAssetTests
{
    [Test]
    public async Task ImageVariantsHaveExpectedDimensionsAndResponsiveMarkupUsesCache()
    {
        using var workspace = new TemporaryWorkspace();
        WritePng(Path.Combine(workspace.Root, "photo.png"), 40, 20);
        var source = new SiteAsset("photo", workspace.Root, "photo.png", "assets/photo.png");
        var image = new ImageAsset(source,
        [
            new ImageVariant("photo-png-20", "images/photo-20.png", 20, ImageFormat.Png),
            new ImageVariant("photo-png-40", "images/photo-40.png", 40, ImageFormat.Png),
            new ImageVariant("photo-webp-20", "images/photo-20.webp", 20, ImageFormat.WebP),
            new ImageVariant("photo-webp-40", "images/photo-40.webp", 40, ImageFormat.WebP)
        ]);
        string? html = null;

        var first = await Generate(workspace, image, context =>
            html = ResponsiveImage.Render(context.Assets, image, "A <photo> & sample", sizes: "(max-width: 40px) 100vw, 40px").ToHtmlString());
        await Assert.That(html!).Contains("<source type=\"image/webp\"");
        await Assert.That(html!).Contains(" 20w");
        await Assert.That(html!).Contains(" 40w");
        await Assert.That(html!).Contains("alt=\"A &lt;photo&gt; &amp; sample\"");
        await Assert.That(html!).Contains("width=\"40\" height=\"20\" loading=\"lazy\" decoding=\"async\"");

        foreach (var variant in image.Variants)
        {
            var artifact = first.BuildPlan.Artifacts.Single(item => item.Id.Value == "asset:" + variant.Output.Id);
            using var codec = SKCodec.Create(Path.Combine(Output(workspace), artifact.RelativeOutputPath));
            await Assert.That(codec).IsNotNull();
            await Assert.That(codec!.Info.Width).IsEqualTo(variant.Width);
            await Assert.That(codec.Info.Height).IsEqualTo(variant.Width / 2);
        }

        var cacheFile = Directory.EnumerateFiles(Cache(workspace), "*.json").Single();
        var oldTimestamp = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(cacheFile, oldTimestamp);
        oldTimestamp = File.GetLastWriteTimeUtc(cacheFile);
        await Generate(workspace, image, _ => { }, clean: false);
        await Assert.That(File.GetLastWriteTimeUtc(cacheFile)).IsEqualTo(oldTimestamp);
    }

    [Test]
    public async Task ImageDeclarationsRejectMissingAvifEncoderAndInvalidVariants()
    {
        using var workspace = new TemporaryWorkspace();
        var source = new SiteAsset("photo", workspace.Root, "photo.png", "assets/photo.png");
        var fallback = new ImageVariant("fallback", "images/photo.png", 20, ImageFormat.Png);

        await Assert.That(() => new ImageVariant("zero", "images/zero.png", 0, ImageFormat.Png))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ImageVariant("quality", "images/q.webp", 20, ImageFormat.WebP, 101))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ImageAsset(source,
            [fallback, new ImageVariant("avif", "images/photo.avif", 20, ImageFormat.Avif)]))
            .Throws<ArgumentException>();
        await Assert.That(() => new ImageAsset(source,
            [fallback, new ImageVariant("duplicate", "images/other.png", 20, ImageFormat.Png)]))
            .Throws<ArgumentException>();
        await Assert.That(() => new ImageAsset(source,
            [new ImageVariant("webp", "images/photo.webp", 20, ImageFormat.WebP)]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ModernSourcesPrecedeOtherRasterSourcesAndEagerLoadingIsExplicit()
    {
        using var workspace = new TemporaryWorkspace();
        WritePng(Path.Combine(workspace.Root, "photo.png"), 40, 20);
        var image = new ImageAsset(new SiteAsset("photo", workspace.Root, "photo.png", "original.png"),
            [new ImageVariant("png", "small.png", 20, ImageFormat.Png),
             new ImageVariant("webp", "small.webp", 20, ImageFormat.WebP),
             new ImageVariant("jpeg", "large.jpg", 40, ImageFormat.Jpeg)]);
        string? html = null;
        await Generate(workspace, image, context => html = ResponsiveImage.Render(context.Assets, image, "", lazy: false).ToHtmlString());
        await Assert.That(html!.IndexOf("image/webp", StringComparison.Ordinal))
            .IsLessThan(html.IndexOf("image/png", StringComparison.Ordinal));
        await Assert.That(html).Contains("loading=\"eager\"");
    }

    private static Task<SiteGenerationResult> Generate(
        TemporaryWorkspace workspace,
        ImageAsset image,
        Action<SiteTemplateContext> inspect,
        bool clean = true)
    {
        var publicDirectory = Path.Combine(workspace.Root, "public");
        Directory.CreateDirectory(publicDirectory);
        return new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/sub/" }, [], Output(workspace), clean,
            new SiteCustomization { GenerateLlmsTxt = false, Template = new CallbackTemplate(inspect) },
            new SiteGenerationOptions
            {
                Assets = [image.Source],
                AssetTransforms = [image.Transform],
                PublicDirectory = publicDirectory,
                AssetCacheDirectory = Cache(workspace)
            }, default);
    }

    private static void WritePng(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Could not encode the test PNG.");
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static string Output(TemporaryWorkspace workspace) => Path.Combine(workspace.Root, "output");
    private static string Cache(TemporaryWorkspace workspace) => Path.Combine(workspace.Root, "cache");

    private sealed class CallbackTemplate(Action<SiteTemplateContext> inspect) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default)
        {
            inspect(context);
            return Task.FromResult(new SiteTemplateResult([new SiteTemplateFile { RelativePath = "index.html", Content = "ok" }]));
        }
    }
}
