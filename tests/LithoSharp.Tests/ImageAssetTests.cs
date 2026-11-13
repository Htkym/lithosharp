using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using LithoSharp.Configuration;
using LithoSharp.Images;
using LithoSharp.Quality;
using LithoSharp.Testing;

namespace LithoSharp.Tests;

public sealed class ImageAssetTests
{
    // Existing independent fixture: 1x1 grayscale+alpha, opaque black; not produced by PngCodec.
    private const string IconPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Test]
    public async Task PngVariantsCarryAssociatedMetadataOnColdAndCachedBuilds()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "photo.png"), Convert.FromBase64String(IconPng));
        var source = new SiteAsset("photo", workspace.Root, "photo.png", "assets/photo.png");
        var small = new ImageVariant("small", "images/small.png", 2, CompressionLevel.Fastest);
        var large = new ImageVariant("large", "images/large.png", 4);
        var image = new ImageAsset(source, [small, large], large);
        string? html = null;
        ImageMetadata? metadata = null;
        Action<SiteTemplateContext> inspect = context =>
        {
            html = ResponsiveImage.Render(context.Assets, image.Descriptor, "A <photo> & sample", sizes: "4px").ToHtmlString();
            metadata = context.Assets.GetImageMetadata(large.Output);
            var assetReference = new ImageSource(context.Assets.GetUrl(large.Output).Asset);
            if (!ReferenceEquals(assetReference.GetMetadata(context.Assets), metadata))
                throw new InvalidOperationException("The generated asset reference lost its associated image facts.");
        };
        var first = await Generate(workspace, [source], [image.Transform], inspect);
        await Assert.That(html!).Contains("alt=\"A &lt;photo&gt; &amp; sample\"");
        await Assert.That(html!).Contains("width=\"4\" height=\"4\" loading=\"lazy\" decoding=\"async\"");
        await Assert.That(html!).Contains(" 2w");
        await Assert.That(metadata!.Provenance).IsEqualTo(ImageMetadataProvenance.Generated);
        foreach (var variant in image.Variants)
        {
            var artifact = first.BuildPlan.Artifacts.Single(item => item.Id.Value == "asset:" + variant.Output.Id);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Output(workspace), artifact.RelativeOutputPath));
            // Inspect standard PNG IHDR independently of our decoder.
            await Assert.That(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16))).IsEqualTo(variant.Width);
            await Assert.That(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20))).IsEqualTo(variant.Width);
            await Assert.That(bytes[24]).IsEqualTo((byte)8);
            await Assert.That(bytes[25]).IsEqualTo((byte)6);
        }
        var cache = Directory.EnumerateFiles(Cache(workspace), "*.json").Single();
        File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddDays(-2));
        var timestamp = File.GetLastWriteTimeUtc(cache);
        await Generate(workspace, [source], [image.Transform], inspect, clean: false);
        await Assert.That(File.GetLastWriteTimeUtc(cache)).IsEqualTo(timestamp);
        await Assert.That(metadata!.Width).IsEqualTo(4);
        await Assert.That(metadata.Height).IsEqualTo(4);
        await Assert.That(metadata.MediaType).IsEqualTo("image/png");
    }

    [Test]
    public async Task MissingAlteredOrOldCacheMetadataForcesRegeneration()
    {
        using var workspace = new TemporaryWorkspace();
        var output = new SiteAssetOutput("image", "image.png", "image/png");
        var calls = 0;
        var transform = new SiteAssetTransform("image", "image/v1", [], [output], (context, token) =>
        {
            calls++;
            return context.WriteAsync(output, Convert.FromBase64String(IconPng),
                new ImageMetadata(1, 1, "image/png", ImageMetadataProvenance.Decoded), token);
        });
        await Generate(workspace, [], [transform], _ => { });
        await Generate(workspace, [], [transform], _ => { }, clean: false);
        await Assert.That(calls).IsEqualTo(1);
        var cache = Directory.EnumerateFiles(Cache(workspace), "*.json").Single();
        foreach (var corruption in new[] { "missing", "dimension", "provenance", "missing-provenance", "output", "version", "invalid" })
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(cache))!;
            var entry = json["Outputs"]!["image"]!;
            switch (corruption)
            {
                case "missing":
                    entry.AsObject().Remove("Metadata");
                    break;
                case "dimension":
                    entry["Metadata"]!["Width"] = 2;
                    break;
                case "provenance":
                    entry["Metadata"]!["Provenance"] = 2;
                    break;
                case "missing-provenance":
                    entry["Metadata"]!.AsObject().Remove("Provenance");
                    break;
                case "output":
                    entry["Id"] = "wrong";
                    break;
                case "version":
                    json["Version"] = 1;
                    break;
                case "invalid":
                    entry["Metadata"]!["Width"] = 0;
                    break;
            }
            await File.WriteAllTextAsync(cache, json.ToJsonString());
            await Generate(workspace, [], [transform], context =>
            {
                if (context.Assets.GetImageMetadata(output)?.Width != 1)
                    throw new InvalidOperationException("Invalid metadata was reused.");
            }, clean: false);
        }
        await Assert.That(calls).IsEqualTo(8);
    }

    [Test]
    public async Task ExistingFormatsRenderAndCopyWithoutDecoding()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "photo.avif"), "opaque pass-through bytes");
        var asset = new SiteAsset("photo", workspace.Root, "photo.avif", "assets/photo.avif",
            new ImageMetadata(40, 20, "image/avif", ImageMetadataProvenance.Declared));
        var avif = new ImageSource(asset);
        var fallback = new ImageSource("https://cdn.example.test/photo.jpg?a=1&b=2",
            new ImageMetadata(80, 40, "image/jpeg", ImageMetadataProvenance.Declared));
        var webp = new ImageSource("/existing.webp", new ImageMetadata(40, 20, "image/webp", ImageMetadataProvenance.Declared));
        var png = new ImageSource("/existing.png", new ImageMetadata(40, 20, "image/png", ImageMetadataProvenance.Declared));
        var descriptor = new ImageDescriptor([png, webp, avif, fallback], fallback);
        string? html = null;
        await Generate(workspace, [asset], [], context =>
        {
            html = ResponsiveImage.Render(context.Assets, descriptor, "\"safe\"", lazy: false).ToHtmlString();
        });
        await Assert.That(html!).Contains("<source type=\"image/avif\"");
        await Assert.That(html!.IndexOf("image/avif", StringComparison.Ordinal)).IsLessThan(html.IndexOf("image/webp", StringComparison.Ordinal));
        await Assert.That(html.IndexOf("image/webp", StringComparison.Ordinal)).IsLessThan(html.IndexOf("image/png", StringComparison.Ordinal));
        await Assert.That(html!).Contains("a=1&amp;b=2");
        await Assert.That(html!).Contains("alt=\"&quot;safe&quot;\"");
        await Assert.That(html!).Contains("width=\"80\" height=\"40\" loading=\"eager\"");
        var copied = Directory.EnumerateFiles(Path.Combine(Output(workspace), "assets")).Single();
        await Assert.That(await File.ReadAllTextAsync(copied)).IsEqualTo("opaque pass-through bytes");
    }

    [Test]
    public async Task RegisteredExistingVariantsUseDeclaredDimensionsWithoutImagesTransforms()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "small.jpg"), "pre-generated small image bytes");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "large.jpg"), "pre-generated large image bytes");
        var smallAsset = new SiteAsset("small-existing", workspace.Root, "small.jpg", "images/small.jpg",
            new ImageMetadata(40, 20, "image/jpeg", ImageMetadataProvenance.Declared));
        var largeAsset = new SiteAsset("large-existing", workspace.Root, "large.jpg", "images/large.jpg",
            new ImageMetadata(80, 40, "image/jpeg", ImageMetadataProvenance.Declared));
        var small = new ImageSource(smallAsset);
        var large = new ImageSource(largeAsset);
        var descriptor = new ImageDescriptor([small, large], large);
        var unregistered = new ImageSource(new SiteAsset(largeAsset.Id, workspace.Root, "large.jpg", "images/large.jpg",
            new ImageMetadata(80, 40, "image/jpeg", ImageMetadataProvenance.Declared)));
        var rejectedAlias = false;
        string? html = null;
        string? smallUrl = null;
        string? largeUrl = null;
        var result = await Generate(workspace, [smallAsset, largeAsset], [], context =>
        {
            smallUrl = context.Assets.GetUrl(smallAsset).Value;
            largeUrl = context.Assets.GetUrl(largeAsset).Value;
            html = ResponsiveImage.Render(context.Assets, descriptor, "Existing image").ToHtmlString();
            if (small.GetMetadata(context.Assets)?.Provenance != ImageMetadataProvenance.Declared
                || large.GetMetadata(context.Assets)?.Provenance != ImageMetadataProvenance.Declared)
                throw new InvalidOperationException("Declared source metadata was not preserved.");
            try
            {
                ResponsiveImage.Render(context.Assets, new ImageDescriptor([small, unregistered], unregistered), "");
            }
            catch (AssetRegistryException)
            {
                rejectedAlias = true;
            }
        });
        await Assert.That(html!).Contains($"srcset=\"{smallUrl} 40w, {largeUrl} 80w\"");
        await Assert.That(html!).Contains("width=\"80\" height=\"40\"");
        await Assert.That(rejectedAlias).IsTrue();
        foreach (var asset in new[] { smallAsset, largeAsset })
        {
            var artifact = result.BuildPlan.Artifacts.Single(item => item.Id.Value == "asset:" + asset.Id);
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), artifact.RelativeOutputPath)))
                .IsEqualTo(await File.ReadAllTextAsync(Path.Combine(workspace.Root, asset.RelativeInputPath)));
        }
    }

    [Test]
    public async Task ImageOutputsRequireMatchingMetadataAndRegistryDeclarationIdentity()
    {
        using var workspace = new TemporaryWorkspace();
        var output = new SiteAssetOutput("typed-image", "image.png", "image/png");
        var missing = new SiteAssetTransform("missing", "v1", [], [output], (context, token) =>
            context.WriteAsync(output, Convert.FromBase64String(IconPng), token));
        await Assert.That(async () => await Generate(workspace, [], [missing], _ => { })).Throws<ArgumentException>();
        var mismatch = new SiteAssetTransform("mismatch", "v1", [], [output], (context, token) =>
            context.WriteAsync(output, Convert.FromBase64String(IconPng),
                new ImageMetadata(1, 1, "image/jpeg", ImageMetadataProvenance.Generated), token));
        await Assert.That(async () => await Generate(workspace, [], [mismatch], _ => { })).Throws<ArgumentException>();
        var valid = new SiteAssetTransform("valid", "v1", [], [output], (context, token) =>
            context.WriteAsync(output, Convert.FromBase64String(IconPng),
                new ImageMetadata(1, 1, "image/png", ImageMetadataProvenance.Decoded), token));
        var rejectedAlias = false;
        await Generate(workspace, [], [valid], context =>
        {
            var outputAsset = context.Assets.GetUrl(output).Asset;
            if (new ImageSource(outputAsset).GetMetadata(context.Assets)?.Provenance != ImageMetadataProvenance.Decoded)
                throw new InvalidOperationException("The registered asset reference lost decoded image facts.");
            try
            {
                context.Assets.GetImageMetadata(new SiteAssetOutput(output.Id, output.RelativeOutputPath, "image/png"));
            }
            catch (AssetRegistryException)
            {
                rejectedAlias = true;
            }
        });
        await Assert.That(rejectedAlias).IsTrue();
    }

    [Test]
    public async Task UnknownDimensionsAreOmittedAndInventedFactsAreRejected()
    {
        using var workspace = new TemporaryWorkspace();
        var unknown = new ImageSource("/existing.svg");
        var escapedUrl = "/assets/photo%20one.png?x=1&y=2";
        var escaped = new ImageSource(escapedUrl);
        string? html = null;
        await Generate(workspace, [], [], context =>
        {
            html = ResponsiveImage.Render(context.Assets, new ImageDescriptor([unknown], unknown), "").ToHtmlString();
            if (escaped.GetUrl(context.Assets) != escapedUrl)
                throw new InvalidOperationException("A valid escaped URL was changed.");
        });
        await Assert.That(html!.Contains("width=", StringComparison.Ordinal)).IsFalse();
        await Assert.That(html.Contains("srcset=", StringComparison.Ordinal)).IsFalse();
        await Assert.That(() => new ImageMetadata(0, 2, "image/png", ImageMetadataProvenance.Declared)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ImageSource("javascript:alert(1)")).Throws<ArgumentException>();
        foreach (var invalidUrl in new[]
        {
            "/\\evil.example/image.png", "//evil.example/image.png", "/assets\\image.png",
            "/assets/\timage.png", "/assets/\nimage.png", "/assets/%0dimage.png", "/%5cevil.example/image.png",
            "https://user:password@cdn.example.test/image.png", "https://cdn.example.test\\image.png"
        })
            await Assert.That(() => new ImageSource(invalidUrl)).Throws<ArgumentException>();
        await Assert.That(() => new ImageSource("/image.png", new ImageMetadata(1, 1, "image/png", ImageMetadataProvenance.Decoded))).Throws<ArgumentException>();
        await Assert.That(() => new ImageDescriptor([unknown], new ImageSource("/other.png"))).Throws<ArgumentException>();
        await Assert.That(() => new ImageVariant("bad", "bad.webp", 20)).Throws<ArgumentException>();
#pragma warning disable CS0618
        await Assert.That(() => new ImageVariant("legacy", "legacy.png", 20, ImageFormat.Png)).Throws<NotSupportedException>();
        await Assert.That(() => new ImageVariant("legacy", "legacy.avif", 20, ImageFormat.Avif)).Throws<NotSupportedException>();
#pragma warning restore CS0618
    }

    [Test]
    [Arguments("/existing.png?token=,")]
    [Arguments("/existing.png#view,")]
    public async Task SrcsetRejectsTrailingLiteralCommasButSrcOnlyImagesPreserveThem(string url)
    {
        var source = new ImageSource(url, new ImageMetadata(80, 40, "image/png", ImageMetadataProvenance.Declared));
        await Assert.That(source.GetUrl(AssetRegistry.Empty)).IsEqualTo(url);
        await Assert.That(() => ResponsiveImage.Render(AssetRegistry.Empty, new ImageDescriptor([source], source), ""))
            .Throws<InvalidOperationException>().WithMessageContaining("%2C");
        var fallback = new ImageSource("/fallback.jpg", new ImageMetadata(80, 40, "image/jpeg", ImageMetadataProvenance.Declared));
        await Assert.That(() => ResponsiveImage.Render(AssetRegistry.Empty, new ImageDescriptor([source, fallback], fallback), ""))
            .Throws<InvalidOperationException>().WithMessageContaining("%2C");

        var unknown = new ImageSource(url);
        using var document = SiteTestDocument.Parse(
            ResponsiveImage.Render(AssetRegistry.Empty, new ImageDescriptor([unknown], unknown), "").ToHtmlString());
        var image = document.Snapshot.Query("img")!;
        await Assert.That(image.GetAttribute("src")).IsEqualTo(url);
        await Assert.That(image.GetAttribute("srcset")).IsNull();
    }

    [Test]
    [Arguments("/existing.png?token=first,second%2C")]
    [Arguments("/existing.png#first,second%2c")]
    [Arguments("https://cdn.example.test/existing.png?token=first,second%2c&signature=MiXeD")]
    public async Task SrcsetPreservesInternalAndPercentEncodedCommas(string url)
    {
        var source = new ImageSource(url, new ImageMetadata(80, 40, "image/png", ImageMetadataProvenance.Declared));
        using var document = SiteTestDocument.Parse(
            ResponsiveImage.Render(AssetRegistry.Empty, new ImageDescriptor([source], source), "").ToHtmlString());
        var image = document.Snapshot.Query("img")!;
        await Assert.That(image.GetAttribute("src")).IsEqualTo(url);
        var srcset = image.GetAttribute("srcset")!;
        await Assert.That(srcset).IsEqualTo($"{url} 80w");
        await Assert.That(SrcsetUrlScanner.Read(srcset, default).Single()).IsEqualTo(url);
    }

    [Test]
    public async Task ExplicitSocialCompositionProducesMetadataAndExpectedLogoPixels()
    {
        using var workspace = new TemporaryWorkspace();
        // Background and logo are producer inputs; independent pixel expectation verifies composition through the asset adapter.
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "background.png"), PngCodec.Encode(RgbaImage.Create(4, 2, 0, 0, 255)));
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "logo.png"), Convert.FromBase64String(IconPng));
        var background = new SiteAsset("background", workspace.Root, "background.png", "background.png");
        var logo = new SiteAsset("logo", workspace.Root, "logo.png", "logo.png");
        var social = new SocialImageAsset(background, logo, "social", "social.png", 1, 1, 0);
        await Generate(workspace, [background, logo], [social.Transform], context =>
        {
            var metadata = social.Image.GetMetadata(context.Assets)!;
            if (metadata.Width != 4 || metadata.Height != 2 || metadata.Provenance != ImageMetadataProvenance.Generated)
                throw new InvalidOperationException("Social image facts were lost.");
            using var stream = context.Assets.OpenRead(social.Output);
            var pixels = PngCodec.Decode(stream).Pixels.Span;
            if (!pixels[..4].SequenceEqual(new byte[] { 0, 0, 255, 255 }) || !pixels.Slice(4, 4).SequenceEqual(new byte[] { 0, 0, 0, 255 }))
                throw new InvalidOperationException("Social image did not place the explicit logo over the background.");
        });
    }

    private static Task<SiteGenerationResult> Generate(TemporaryWorkspace workspace, IReadOnlyList<SiteAsset> assets,
        IReadOnlyList<SiteAssetTransform> transforms, Action<SiteTemplateContext> inspect, bool clean = true)
    {
        var publicDirectory = Path.Combine(workspace.Root, "public");
        Directory.CreateDirectory(publicDirectory);
        return new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.test/sub/" }, [], Output(workspace), clean,
            new SiteCustomization { GenerateLlmsTxt = false, Template = new CallbackTemplate(inspect) },
            new SiteGenerationOptions { Assets = assets, AssetTransforms = transforms, PublicDirectory = publicDirectory, AssetCacheDirectory = Cache(workspace) }, default);
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
