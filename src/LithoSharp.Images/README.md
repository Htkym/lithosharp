# LithoSharp.Images

Add declared, cached responsive raster image transforms to LithoSharp documentation sites (.NET 10). Use them alongside C# content contracts and build/output checks; image transforms do not replace those checks.

Use LithoSharp 2.2.0 for this package and the other LithoSharp packages in your project.

```sh
dotnet add package LithoSharp.Images --version 2.2.0
```

Use matching Core and Images versions. Register the same `ImageAsset` declaration
instances with generation and rendering so their declared outputs can be resolved.

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
Place the cache outside the output directory. RAW and DNG sources are unsupported
by SkiaSharp 4.153.1; convert them to PNG or JPEG before declaring an image.
On Linux, install the .NET native prerequisites, including `libstdc++.so.6`;
SkiaSharp 4.153.1 needs it even with `Linux.NoDependencies`.

PNG/JPEG/WebP need no external executable. AVIF requires a trusted avifenc path:
`new ExternalAvifEncoder(path, dependencyFingerprint: "installed-tool-and-codec-versions")`.
Pass it to ImageAsset with an AVIF variant and a fallback. Tool bytes and settings
are fingerprinted; changed tools are rejected even on cache hits. Recreate declarations
after updates. No PATH discovery or downloads occur. See the official
[avifenc manual](https://github.com/AOMediaCodec/libavif/blob/main/doc/avifenc.1.md).

See [assets and images](https://github.com/Htkym/lithosharp/blob/main/docs/assets-and-images.md)
for source roots, output paths, transform registration and cache restrictions.
