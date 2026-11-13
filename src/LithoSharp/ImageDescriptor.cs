using System.Globalization;
using System.Text;

namespace LithoSharp;

/// <summary>How image dimensions and media type were obtained.</summary>
public enum ImageMetadataProvenance
{
    /// <summary>Supplied by the caller without decoding.</summary>
    Declared,
    /// <summary>Verified by a supported decoder.</summary>
    Decoded,
    /// <summary>Established by the image producer.</summary>
    Generated
}

/// <summary>Known intrinsic image dimensions, media type and their origin. Core never decodes images.</summary>
public sealed class ImageMetadata
{
    /// <summary>Creates validated metadata with positive pixel dimensions and an image media type.</summary>
    public ImageMetadata(int width, int height, string mediaType, ImageMetadataProvenance provenance)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ValidateMediaType(mediaType);
        if (!Enum.IsDefined(provenance))
            throw new ArgumentOutOfRangeException(nameof(provenance));
        Width = width;
        Height = height;
        MediaType = mediaType;
        Provenance = provenance;
    }

    /// <summary>Intrinsic width in pixels.</summary>
    public int Width { get; }
    /// <summary>Intrinsic height in pixels.</summary>
    public int Height { get; }
    /// <summary>Image media type.</summary>
    public string MediaType { get; }
    /// <summary>Origin of these facts.</summary>
    public ImageMetadataProvenance Provenance { get; }

    internal static void ValidateMediaType(string mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        if (!mediaType.StartsWith("image/", StringComparison.Ordinal) || mediaType.Length == 6
            || mediaType.AsSpan(6).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789!#$&^_.+-"))
            throw new ArgumentException("Use an image media type without parameters, such as image/png.", nameof(mediaType));
    }
}

/// <summary>An existing image URL or a registered asset reference, independent of image processing.</summary>
public sealed class ImageSource
{
    private readonly string? url;
    private readonly SiteAsset? asset;
    private readonly SiteAssetOutput? output;
    private readonly ImageMetadata? metadata;

    /// <summary>References an existing image. Optional caller supplied dimensions must have Declared provenance.</summary>
    public ImageSource(string url, ImageMetadata? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (url.Contains('\\') || url.Any(char.IsControl) || url.Any(char.IsWhiteSpace))
            throw new ArgumentException("Image URLs must use forward slashes and contain no whitespace or control characters.", nameof(url));
        if (url.StartsWith('/'))
        {
            if (url.StartsWith("//", StringComparison.Ordinal))
                throw new ArgumentException("A root relative image URL must not contain an authority.", nameof(url));
            var suffix = url.IndexOfAny(['?', '#']);
            var path = suffix < 0 ? url : url[..suffix];
            try
            {
                _ = path.EndsWith('/') ? Routing.SiteRoute.ForDirectoryIndex(path[1..]) : Routing.SiteRoute.ForFile(path[1..]);
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException)
            {
                throw new ArgumentException("The root relative image URL must contain a valid site route path.", nameof(url), exception);
            }
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || !uri.IsWellFormedOriginalString())
            throw new ArgumentException("Use a root relative image URL or a well formed absolute HTTP(S) URL without credentials.", nameof(url));
        if (metadata is not null && metadata.Provenance != ImageMetadataProvenance.Declared)
            throw new ArgumentException("External image metadata must use Declared provenance.", nameof(metadata));
        this.url = url;
        this.metadata = metadata;
    }

    /// <summary>References an existing registered source asset.</summary>
    public ImageSource(SiteAsset asset) => this.asset = asset ?? throw new ArgumentNullException(nameof(asset));
    /// <summary>References an existing registered transformed output.</summary>
    public ImageSource(SiteAssetOutput output) => this.output = output ?? throw new ArgumentNullException(nameof(output));
    /// <summary>Resolves the registered URL, or returns the explicit URL.</summary>
    public string GetUrl(AssetRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return url ?? (asset is not null ? registry.GetUrl(asset).Value : registry.GetUrl(output!).Value);
    }
    /// <summary>Returns associated image metadata, or null when dimensions are unknown.</summary>
    public ImageMetadata? GetMetadata(AssetRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return url is not null ? metadata : asset is not null ? registry.GetImageMetadata(asset) : registry.GetImageMetadata(output!);
    }
}

/// <summary>Existing variants and an explicitly selected fallback from those variants.</summary>
public sealed class ImageDescriptor
{
    /// <summary>Declares existing variants. The exact fallback reference must appear in the variant list.</summary>
    public ImageDescriptor(IReadOnlyList<ImageSource> variants, ImageSource fallback)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(fallback);
        if (variants.Count == 0 || variants.Any(v => v is null) || !variants.Contains(fallback)
            || variants.Distinct().Count() != variants.Count)
            throw new ArgumentException("Declare distinct existing images including the explicit fallback.", nameof(variants));
        Variants = Array.AsReadOnly(variants.ToArray());
        Fallback = fallback;
    }
    /// <summary>Existing image variants.</summary>
    public IReadOnlyList<ImageSource> Variants { get; }
    /// <summary>Explicitly selected fallback.</summary>
    public ImageSource Fallback { get; }
}

/// <summary>Renders responsive image markup using metadata without invoking a decoder.</summary>
public static class ResponsiveImage
{
    /// <summary>Renders picture, format sources and known intrinsic dimensions. Unknown dimensions are omitted.</summary>
    public static IHtmlContent Render(AssetRegistry registry, ImageDescriptor image, string alt, bool lazy = true, string sizes = "100vw")
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(alt);
        ArgumentNullException.ThrowIfNull(sizes);
        var variants = image.Variants.Select(v => (Source: v, Url: v.GetUrl(registry), Metadata: v.GetMetadata(registry))).ToArray();
        var fallback = variants.Single(v => ReferenceEquals(v.Source, image.Fallback));
        if (variants.Length > 1 && variants.Any(v => v.Metadata is null))
            throw new InvalidOperationException("Responsive variants require known dimensions and media types; declare metadata for existing images.");
        if (variants.Where(v => v.Metadata is not null).GroupBy(v => (v.Metadata!.MediaType, v.Metadata.Width)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Responsive variants must have distinct media type and width pairs.");
        var html = new StringBuilder("<picture>");
        foreach (var group in variants.Where(v => v.Metadata?.MediaType != fallback.Metadata?.MediaType)
                     .GroupBy(v => v.Metadata!.MediaType)
                     .OrderBy(g => g.Key switch { "image/avif" => 0, "image/webp" => 1, _ => 2 })
                     .ThenBy(g => g.Key, StringComparer.Ordinal))
            html.Append("<source type=\"").Append(Attr(group.Key)).Append("\" srcset=\"")
                .Append(Attr(SrcSet(group))).Append("\" sizes=\"").Append(Attr(sizes)).Append("\">");
        html.Append("<img src=\"").Append(Attr(fallback.Url));
        if (fallback.Metadata is not null)
            html.Append("\" srcset=\"").Append(Attr(SrcSet(variants.Where(v => v.Metadata?.MediaType == fallback.Metadata.MediaType))))
                .Append("\" sizes=\"").Append(Attr(sizes));
        html.Append("\" alt=\"").Append(Attr(alt));
        if (fallback.Metadata is not null)
            html.Append("\" width=\"").Append(fallback.Metadata.Width.ToString(CultureInfo.InvariantCulture))
                .Append("\" height=\"").Append(fallback.Metadata.Height.ToString(CultureInfo.InvariantCulture));
        html.Append("\" loading=\"").Append(lazy ? "lazy" : "eager").Append("\" decoding=\"async\"></picture>");
        return Html.UnsafeRaw(html.ToString());

        static string SrcSet(IEnumerable<(ImageSource Source, string Url, ImageMetadata? Metadata)> values) =>
            string.Join(", ", values.OrderBy(v => v.Metadata!.Width).Select(v =>
            {
                if (v.Url.EndsWith(','))
                    throw new InvalidOperationException("Image URLs used in srcset must not end with a literal comma; supply an already percent-encoded URL such as %2C.");
                return $"{v.Url} {v.Metadata!.Width.ToString(CultureInfo.InvariantCulture)}w";
            }));
        static string Attr(string value) => new HtmlAttributeValue(value).ToHtmlString();
    }
}
