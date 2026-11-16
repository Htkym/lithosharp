using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LithoSharp.Images;

/// <summary>Legacy format names. Existing files of any format can be referenced by Core descriptors; generation supports PNG only.</summary>
public enum ImageFormat
{
    /// <summary>PNG.</summary>
    Png,
    /// <summary>Existing JPEG files can be copied; JPEG conversion is unsupported.</summary>
    Jpeg,
    /// <summary>Existing WebP files can be copied; WebP conversion is unsupported.</summary>
    WebP,
    /// <summary>Existing AVIF files can be copied; AVIF conversion is unsupported.</summary>
    Avif
}

/// <summary>A generated PNG at an exact width with proportional height.</summary>
public sealed class ImageVariant
{
    /// <summary>Declares PNG generation with lossless zlib compression.</summary>
    public ImageVariant(string id, string relativeOutputPath, int width, CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        if (!Enum.IsDefined(compressionLevel))
            throw new ArgumentOutOfRangeException(nameof(compressionLevel));
        if (!string.Equals(Path.GetExtension(relativeOutputPath), ".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("LSI001: Generated variants are PNG; use a .png output path or reference an existing image with ImageSource.", nameof(relativeOutputPath));
        Output = new SiteAssetOutput(id, relativeOutputPath, "image/png");
        Width = width;
        CompressionLevel = compressionLevel;
    }

    /// <summary>Reports migration from the removed format and lossy quality settings.</summary>
    [Obsolete("Use ImageVariant(id, relativeOutputPath, width, CompressionLevel). Only PNG generation is supported; reference existing JPEG/WebP/AVIF files with Core ImageSource.")]
    public ImageVariant(string id, string relativeOutputPath, int width, ImageFormat format, int quality = 80)
        => throw new NotSupportedException(format == ImageFormat.Png
            ? "LSI002: PNG has no lossy quality setting. Use ImageVariant(id, path, width, CompressionLevel.Optimal)."
            : "LSI001: JPEG, WebP and AVIF conversion is unsupported. Pre-generate the file and use Core ImageSource, or generate PNG.");

    /// <summary>Declared PNG output.</summary>
    public SiteAssetOutput Output { get; }
    /// <summary>Exact output width in pixels.</summary>
    public int Width { get; }
    /// <summary>Lossless PNG compression setting.</summary>
    public CompressionLevel CompressionLevel { get; }
}

/// <summary>An explicitly registered PNG transformation with existing-result descriptors.</summary>
public sealed class ImageAsset
{
    /// <summary>Declares PNG variants and explicitly selects one as fallback.</summary>
    public ImageAsset(SiteAsset source, IReadOnlyList<ImageVariant> variants, ImageVariant fallback)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(fallback);
        if (variants.Count == 0 || variants.Any(v => v is null) || !variants.Contains(fallback)
            || variants.Select(v => v.Width).Distinct().Count() != variants.Count)
            throw new ArgumentException("Declare distinct PNG widths and explicitly select an existing variant as fallback.", nameof(variants));
        Source = source;
        Variants = Array.AsReadOnly(variants.ToArray());
        var sources = Variants.Select(v => new ImageSource(v.Output)).ToArray();
        Descriptor = new ImageDescriptor(sources, sources[Array.IndexOf(Variants.ToArray(), fallback)]);
        Transform = new SiteAssetTransform($"image:{source.Id}", ImplementationFingerprint(new
        {
            Variants = Variants.Select(v => new { v.Output.Id, v.Output.RelativeOutputPath, v.Width, v.CompressionLevel })
        }), [source], Variants.Select(v => v.Output).ToArray(), async (context, cancellationToken) =>
        {
            using var stream = context.OpenRead(source);
            RgbaImage image;
            try
            {
                image = PngCodec.Decode(stream, cancellationToken: cancellationToken);
            }
            catch (NotSupportedException exception)
            {
                throw new NotSupportedException($"LSI003: Image '{source.Id}' is outside the supported PNG profile. Copy the original with SiteAsset and use ImageSource for existing variants. {exception.Message}", exception);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException($"LSI005: Image '{source.Id}' is not a valid supported PNG. JPEG/WebP/AVIF conversion is unavailable; use SiteAsset and ImageSource to publish existing files. {exception.Message}", exception);
            }
            foreach (var variant in Variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resized = image.Resize(variant.Width, cancellationToken: cancellationToken);
                var bytes = PngCodec.Encode(resized, variant.CompressionLevel, cancellationToken: cancellationToken);
                await context.WriteAsync(variant.Output, bytes,
                    new ImageMetadata(resized.Width, resized.Height, "image/png", ImageMetadataProvenance.Generated), cancellationToken).ConfigureAwait(false);
            }
        });
    }

    /// <summary>Reports migration to explicit fallback selection.</summary>
    [Obsolete("Select a fallback explicitly with ImageAsset(source, variants, fallback).")]
    public ImageAsset(SiteAsset source, IReadOnlyList<ImageVariant> variants)
        => throw new NotSupportedException("LSI004: Select an existing PNG variant explicitly with ImageAsset(source, variants, fallback).");

    /// <summary>Source to register in Assets.</summary>
    public SiteAsset Source { get; }
    /// <summary>Generated variants.</summary>
    public IReadOnlyList<ImageVariant> Variants { get; }
    /// <summary>Core descriptor rendered without decoding.</summary>
    public ImageDescriptor Descriptor { get; }
    /// <summary>Transformation to register in AssetTransforms.</summary>
    public SiteAssetTransform Transform { get; }

    internal static string ImplementationFingerprint(object settings) => JsonSerializer.Serialize(new
    {
        Implementation = "LithoSharp.Images/2",
        Module = typeof(ImageAsset).Module.ModuleVersionId,
        PngCodec.Profile,
        RgbaImage.ProcessingProfile,
        RuntimeInformation.RuntimeIdentifier,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.OSDescription,
        Framework = RuntimeInformation.FrameworkDescription,
        CompressionRuntime = typeof(System.IO.Compression.ZLibStream).Assembly.FullName,
        Settings = settings
    });
}
