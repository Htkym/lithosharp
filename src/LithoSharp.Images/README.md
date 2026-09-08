# LithoSharp.Images

Declared responsive raster image transforms for LithoSharp (.NET 10).

```csharp
using LithoSharp;
using LithoSharp.Images;

var image = new ImageAsset(
    new SiteAsset("photo", "assets", "photo.jpg", "images/original.jpg"),
    [new ImageVariant("jpeg", "images/photo.jpg", 640, ImageFormat.Jpeg),
     new ImageVariant("webp", "images/photo.webp", 640, ImageFormat.WebP)]);
var options = new SiteGenerationOptions
{
    Assets = [image.Source],
    AssetTransforms = [image.Transform],
    AssetCacheDirectory = ".lithosharp/assets"
};
// In a layout: ResponsiveImage.Render(context.Assets, image, "Lake at dawn")
```

The renderer encodes alt text and attributes and emits intrinsic dimensions,
format sources, srcset, sizes and lazy loading. PNG/JPEG fallback, positive widths,
quality 0–100 and distinct format/width pairs are required. Invalid images fail
before output publication. Cached bytes are verified; corrupt entries regenerate.
Place the cache outside the output directory.

PNG/JPEG/WebP need no external executable. AVIF requires a trusted avifenc path:
`new ExternalAvifEncoder(path, dependencyFingerprint: "installed-tool-and-codec-versions")`.
Pass it to ImageAsset with an AVIF variant and a fallback. Tool bytes and settings
are fingerprinted; changed tools are rejected even on cache hits. Recreate declarations
after updates. No PATH discovery or downloads occur. See the official
[avifenc manual](https://github.com/AOMediaCodec/libavif/blob/main/doc/avifenc.1.md).
