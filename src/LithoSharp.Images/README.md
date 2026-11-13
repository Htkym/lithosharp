# LithoSharp.Images

[English](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Images/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Images/README.ja.md)

Add explicit, cached PNG transforms to LithoSharp sites (.NET 10). Core registers and copies existing images of any format and renders responsive markup without this package or a decoder. Register Images transforms only when PNG processing is needed.

Use matching Core and Images versions. Register the same `ImageAsset` declaration
instances with generation and rendering so their declared outputs can be resolved.

```csharp
using LithoSharp;
using LithoSharp.Images;
using System.IO.Compression;

var small = new ImageVariant("small", "images/photo-small.png", 320);
var large = new ImageVariant("large", "images/photo-large.png", 640, CompressionLevel.Optimal);
var image = new ImageAsset(
    new SiteAsset("photo", "assets", "photo.png", "images/original.png"),
    [small, large], fallback: large);
var options = new SiteGenerationOptions
{
    Assets = [image.Source],
    AssetTransforms = [image.Transform],
    AssetCacheDirectory = ".lithosharp/assets"
};
// In a layout: ResponsiveImage.Render(context.Assets, image.Descriptor, "Lake at dawn")
```

The Core renderer escapes alt text and attributes and emits picture, format sources,
srcset, sizes, intrinsic dimensions, loading and decoding attributes. The fallback
must be selected explicitly from existing variants. Unknown dimensions stay unknown.
For existing images, use Core alone:

```csharp
var webp = new ImageSource("https://cdn.example.test/photo.webp",
    new ImageMetadata(640, 320, "image/webp", ImageMetadataProvenance.Declared));
var jpeg = new ImageSource("https://cdn.example.test/photo.jpg",
    new ImageMetadata(640, 320, "image/jpeg", ImageMetadataProvenance.Declared));
var existing = new ImageDescriptor([webp, jpeg], fallback: jpeg);
// ResponsiveImage.Render(context.Assets, existing, "Lake at dawn")
```

`ImageSource` also accepts a registered `SiteAsset` or `SiteAssetOutput`. Source
dimensions supplied by the caller are `Declared`; copying an image does not prove
it can be decoded. Declare dimensions on the source asset to retain registration,
fingerprinting and build dependencies without Images transforms:

```csharp
var smallAsset = new SiteAsset("small", "assets", "photo-small.jpg", "images/photo-small.jpg",
    new ImageMetadata(320, 160, "image/jpeg", ImageMetadataProvenance.Declared));
var largeAsset = new SiteAsset("large", "assets", "photo-large.jpg", "images/photo-large.jpg",
    new ImageMetadata(640, 320, "image/jpeg", ImageMetadataProvenance.Declared));
var smallSource = new ImageSource(smallAsset);
var largeSource = new ImageSource(largeAsset);
var registered = new ImageDescriptor([smallSource, largeSource], fallback: largeSource);
var existingOptions = new SiteGenerationOptions { Assets = [smallAsset, largeAsset] };
// ResponsiveImage.Render(context.Assets, registered, "Lake at dawn")
```

Unregistered asset references fail even when their declared dimensions are known.
Supported decoding establishes `Decoded` facts, while transforms
attach `Generated` metadata to the exact output bytes. Cache format 2 associates
metadata with the output ID, content hash and transform identity. Old, missing,
invalid or mismatched cache metadata causes regeneration. Keep caches outside output.
Fingerprints include the PNG and pixel profiles, compression setting, implementation
and runtime identity; byte equality across unverified environments is not promised.

PNG input is limited to 8-bit, noninterlaced grayscale, RGB, grayscale-alpha and RGBA.
Filters 0–4, multiple IDAT chunks, transparency and bounded structural/checksum
validation are supported. Output is noninterlaced RGBA8 PNG with filter 0 and zlib
compression. Resize respects the exact width and preserves aspect ratio, rounding
height half up. Bilinear interpolation and source-over compositing use premultiplied
alpha; fully transparent output RGB is zero. `ImageLimits` bounds input, chunks,
dimensions, pixels and decoded bytes before allocation.

Palette images, 16-bit PNG, Adam7, APNG, JPEG/WebP/AVIF conversion, SVG rasterization,
EXIF orientation, ICC/cICP/HDR color conversion and general text/vector drawing are
unsupported. Inputs must be sRGB or untagged; compatible gAMA/cHRM values are checked.
Optional metadata is validated and skipped; transformed metadata preservation is
not guaranteed. Publish unsupported formats unchanged with `SiteAsset` and declare
existing variants with Core descriptors.

For explicit social composition, create `SocialImageAsset(background, logo, id,
"social.png", logoWidth, logoX, logoY)`. Register both PNG source assets and its
`Transform`, then use its `Image` in `SiteGenerationOptions.CommonSocialImage` or
`SocialImages` (keyed by the page's normalized relative output path). The background
determines canvas dimensions; logo placement is clipped to the canvas. Existing
site or per-page images use `ImageSource` directly. Favicon presence does not generate
social images. Supply pre-generated artwork when titles or other text are needed.

Migration: replace format/quality variants with `ImageVariant(id, path, width,
CompressionLevel)`, select an explicit fallback in `ImageAsset`, and pass its
`Descriptor` to Core `ResponsiveImage.Render`. The old format/quality overloads
report `LSI001`/`LSI002`; PNG has no lossy quality setting. Unsupported PNG features
report `LSI003`, invalid PNG input reports `LSI005`, and omitted fallback selection
reports `LSI004`. `ExternalAvifEncoder` has been removed; pre-generate AVIF files
and reference them as existing assets.

See [assets and images](https://github.com/Htkym/lithosharp/blob/main/docs/assets-and-images.md)
for source roots, output paths, transform registration and cache restrictions.
