using System.IO.Compression;

namespace LithoSharp.Images;

/// <summary>Explicitly composes a PNG logo over a PNG background. Text must be supplied in a pre-generated image.</summary>
public sealed class SocialImageAsset
{
    /// <summary>Uses the background's intrinsic size and places a proportionally resized logo at pixel coordinates.</summary>
    public SocialImageAsset(SiteAsset background, SiteAsset logo, string id, string relativeOutputPath,
        int logoWidth, int logoX, int logoY, CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(logo);
        var variant = new ImageVariant(id, relativeOutputPath, logoWidth, compressionLevel);
        Output = variant.Output;
        Image = new ImageSource(Output);
        Transform = new SiteAssetTransform("social-image:" + id, ImageAsset.ImplementationFingerprint(new
        {
            Background = background.Id,
            Logo = logo.Id,
            logoWidth,
            logoX,
            logoY,
            compressionLevel
        }), new[] { background, logo }.Distinct().ToArray(), [Output], async (context, cancellationToken) =>
        {
            using var backgroundStream = context.OpenRead(background);
            using var logoStream = context.OpenRead(logo);
            var canvas = PngCodec.Decode(backgroundStream, cancellationToken: cancellationToken);
            var logoImage = PngCodec.Decode(logoStream, cancellationToken: cancellationToken)
                .Resize(logoWidth, cancellationToken: cancellationToken);
            canvas.Composite(logoImage, logoX, logoY, cancellationToken);
            await context.WriteAsync(Output, PngCodec.Encode(canvas, compressionLevel, cancellationToken: cancellationToken),
                new ImageMetadata(canvas.Width, canvas.Height, "image/png", ImageMetadataProvenance.Generated), cancellationToken).ConfigureAwait(false);
        });
    }

    /// <summary>Generated PNG output.</summary>
    public SiteAssetOutput Output { get; }
    /// <summary>Core reference usable as an explicitly configured social image.</summary>
    public ImageSource Image { get; }
    /// <summary>Transformation to register explicitly in AssetTransforms, with its source assets in Assets.</summary>
    public SiteAssetTransform Transform { get; }
}
