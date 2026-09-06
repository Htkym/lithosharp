using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace LithoSharp.Images;

/// <summary>Supported output encodings. AVIF requires an explicitly configured avifenc executable.</summary>
public enum ImageFormat
{
    /// <summary>Portable Network Graphics.</summary>
    Png,
    /// <summary>JPEG.</summary>
    Jpeg,
    /// <summary>WebP.</summary>
    WebP,
    /// <summary>AVIF using avifenc.</summary>
    Avif
}

/// <summary>A responsive image output with an exact width and a proportional height.</summary>
public sealed class ImageVariant
{
    /// <summary>Declares an image variant.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Width is not positive, quality is outside 0–100, or format is unknown.</exception>
    public ImageVariant(string id, string relativeOutputPath, int width, ImageFormat format, int quality = 80)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        if (quality is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        Output = new SiteAssetOutput(id, relativeOutputPath);
        Width = width;
        Format = format;
        Quality = quality;
    }

    /// <summary>Declared output used to resolve the fingerprinted URL.</summary>
    public SiteAssetOutput Output { get; }
    /// <summary>Output width in pixels.</summary>
    public int Width { get; }
    /// <summary>Output encoding.</summary>
    public ImageFormat Format { get; }
    /// <summary>Encoder quality from 0 through 100.</summary>
    public int Quality { get; }
}

/// <summary>A raster source and its declared, cacheable responsive variants.</summary>
/// <remarks>Register Source in Assets and Transform in AssetTransforms. PNG or JPEG is required as the HTML fallback.</remarks>
public sealed class ImageAsset
{
    /// <summary>Creates an image transformation. Encoder and all variant settings are included in its fingerprint.</summary>
    /// <exception cref="ArgumentException">Variants are empty, repeat format/width pairs, lack a PNG/JPEG fallback, or require an absent AVIF encoder.</exception>
    /// <exception cref="ArgumentNullException">Source or variants is null.</exception>
    public ImageAsset(SiteAsset source, IReadOnlyList<ImageVariant> variants, ExternalAvifEncoder? avifEncoder = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(variants);
        if (variants.Count == 0 || variants.Any(v => v is null)
            || !variants.Any(v => v.Format is ImageFormat.Png or ImageFormat.Jpeg)
            || variants.GroupBy(v => (v.Format, v.Width)).Any(group => group.Count() > 1))
            throw new ArgumentException("Declare distinct format/width variants including a PNG or JPEG fallback.", nameof(variants));
        if (variants.Any(v => v.Format == ImageFormat.Avif) && avifEncoder is null)
            throw new ArgumentException("AVIF variants require an explicit ExternalAvifEncoder.", nameof(avifEncoder));
        Source = source;
        Variants = Array.AsReadOnly(variants.ToArray());
        var fingerprint = JsonSerializer.Serialize(new
        {
            Adapter = "LithoSharp.Images/1",
            Skia = typeof(SKBitmap).Assembly.FullName,
            NativeSkia = SkiaSharpVersion.Native.ToString(),
            RuntimeInformation.RuntimeIdentifier,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Encoder = variants.Any(v => v.Format == ImageFormat.Avif) ? avifEncoder!.Fingerprint : null,
            Variants = Variants.Select(v => new { v.Output.Id, v.Output.RelativeOutputPath, v.Width, v.Format, v.Quality })
        });
        Transform = new SiteAssetTransform($"image:{source.Id}", fingerprint, [source],
            Variants.Select(v => v.Output).ToArray(), async (context, cancellationToken) =>
            {
                using var stream = context.OpenRead(source);
                using var bitmap = SKBitmap.Decode(stream) ?? throw new InvalidDataException($"Image '{source.Id}' cannot be decoded.");
                foreach (var variant in Variants)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var height = Math.Max(1, checked((int)Math.Round((double)bitmap.Height * variant.Width / bitmap.Width)));
                    using var resized = bitmap.Resize(new SKImageInfo(variant.Width, height), new SKSamplingOptions(SKFilterMode.Linear))
                        ?? throw new InvalidDataException($"Image '{source.Id}' cannot be resized.");
                    using var image = SKImage.FromBitmap(resized);
                    var format = variant.Format switch
                    {
                        ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                        ImageFormat.WebP => SKEncodedImageFormat.Webp,
                        _ => SKEncodedImageFormat.Png
                    };
                    using var encoded = image.Encode(format, variant.Quality)
                        ?? throw new InvalidDataException($"Image '{source.Id}' cannot be encoded as {variant.Format}.");
                    var bytes = encoded.ToArray();
                    if (variant.Format == ImageFormat.Avif)
                        bytes = await avifEncoder!.EncodeAsync(bytes, variant.Quality, cancellationToken).ConfigureAwait(false);
                    await context.WriteAsync(variant.Output, bytes, cancellationToken).ConfigureAwait(false);
                }
            }, Variants.Any(v => v.Format == ImageFormat.Avif) ? avifEncoder!.ValidateAsync : null);
    }

    /// <summary>Source asset to register in generation options.</summary>
    public SiteAsset Source { get; }
    /// <summary>Declared image variants.</summary>
    public IReadOnlyList<ImageVariant> Variants { get; }
    /// <summary>Transformation to register in generation options.</summary>
    public SiteAssetTransform Transform { get; }
}

/// <summary>Renders encoded responsive image markup using registered variant URLs.</summary>
public static class ResponsiveImage
{
    /// <summary>Renders a picture element with intrinsic dimensions, format sources, srcset and optional lazy loading.</summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="AssetRegistryException">An image variant is not registered.</exception>
    /// <exception cref="InvalidDataException">The fallback image cannot be decoded.</exception>
    public static IHtmlContent Render(AssetRegistry registry, ImageAsset image, string alt, bool lazy = true, string sizes = "100vw")
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(alt);
        ArgumentNullException.ThrowIfNull(sizes);
        var fallback = image.Variants.Where(v => v.Format is ImageFormat.Png or ImageFormat.Jpeg).OrderByDescending(v => v.Width).First();
        using var stream = registry.OpenRead(fallback.Output);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("The registered image fallback cannot be decoded.");
        var html = new StringBuilder("<picture>");
        foreach (var group in image.Variants.Where(v => v.Format != fallback.Format).GroupBy(v => v.Format)
            .OrderBy(g => g.Key switch { ImageFormat.Avif => 0, ImageFormat.WebP => 1, _ => 2 }))
            html.Append("<source type=\"image/").Append(group.Key switch { ImageFormat.Jpeg => "jpeg", ImageFormat.WebP => "webp", ImageFormat.Avif => "avif", _ => "png" })
                .Append("\" srcset=\"").Append(Attr(SrcSet(group))).Append("\" sizes=\"").Append(Attr(sizes)).Append("\">");
        html.Append("<img src=\"").Append(registry.GetUrl(fallback.Output).ToAttributeValue().ToHtmlString())
            .Append("\" srcset=\"").Append(Attr(SrcSet(image.Variants.Where(v => v.Format == fallback.Format))))
            .Append("\" sizes=\"").Append(Attr(sizes)).Append("\" alt=\"").Append(Attr(alt))
            .Append("\" width=\"").Append(codec.Info.Width.ToString(CultureInfo.InvariantCulture))
            .Append("\" height=\"").Append(codec.Info.Height.ToString(CultureInfo.InvariantCulture))
            .Append("\" loading=\"").Append(lazy ? "lazy" : "eager").Append("\" decoding=\"async\"></picture>");
        return Html.UnsafeRaw(html.ToString());

        string SrcSet(IEnumerable<ImageVariant> variants) => string.Join(", ", variants.OrderBy(v => v.Width)
            .Select(v => $"{registry.GetUrl(v.Output).Value} {v.Width.ToString(CultureInfo.InvariantCulture)}w"));
        static string Attr(string value) => new HtmlAttributeValue(value).ToHtmlString();
    }
}
