# Assets and images

[日本語](assets-and-images.ja.md)

Existing `SiteAsset` declarations keep their fingerprinted URLs. `AssetUrl.Fingerprint`
is lowercase SHA-256; `Integrity` is `sha256-` plus the Base64 digest for a quoted
`integrity` attribute. Set `SiteGenerationOptions.PublicDirectory` to copy regular
files recursively under their original relative paths. Missing directories,
symlinks, invalid paths and route collisions fail before output publication.
Public input and output directories must not overlap; asset source files must also
be outside output. Keep asset and external-link caches outside the public directory.

## Declared transforms and CSS references

```csharp
using System.Text;
using LithoSharp;

var logo = new SiteAsset("logo", "assets", "logo.png", "images/logo.png");
var stylesheet = new SiteAssetOutput("brand-css", "styles/brand.css");
var transform = new SiteAssetTransform("brand-css", "brand-css/v1", [logo], [stylesheet],
    (context, token) => context.WriteAsync(stylesheet,
        Encoding.UTF8.GetBytes($".brand {{ background-image: url('{context.GetUrl(logo).Value}'); }}"), token));
var options = new SiteGenerationOptions
{
    Assets = [logo],
    AssetTransforms = [transform],
    AssetCacheDirectory = ".lithosharp/assets"
};
// In a rendering context:
// context.Assets.GetUrl(stylesheet).ToAttributeValue()
```

The context exposes read-only snapshots through `OpenRead(input)`, declared input
URLs through `GetUrl(input)`, and exactly-once output writes through `WriteAsync`.
Register the same declaration objects used by the handler and renderer. Duplicate
IDs/paths, undeclared reads/writes and missing outputs fail. Output `AssetUrl.Asset`
is a synthetic compatibility declaration; its input path is not a source file.
Use `SiteAssetOutput` to address transformed outputs. `AssetRegistry.BuildNodes`
exposes copy and transform dependencies, fingerprints and owned artifacts.

A transform handler must be deterministic: do not read undeclared files, network,
time or mutable process state. The context restricts its own read/write APIs; it
cannot sandbox arbitrary delegate code. Include every setting, implementation and
tool dependency in `implementationFingerprint`. An optional `validateInputs`
callback runs before every cache lookup, including hits, and may reject changed
external dependencies. For example, `validateInputs: token => { token.ThrowIfCancellationRequested(); ValidateTool(); return Task.CompletedTask; }`.

The opt-in `AssetCacheDirectory` must be outside the output directory. Keys include
input content, input URLs, output declarations and implementation/settings/tool
fingerprints. Cached bytes are hash-checked; missing, corrupt or mismatched entries
regenerate. A cache is never used as output ownership evidence. Cache hits skip the
handler. Matching staged asset bytes skip the final asset write; transaction setup
still copies existing output into isolated staging to preserve rollback.

## Responsive images

Install/reference `LithoSharp.Images` and register the source and transform:

```csharp
using LithoSharp.Images;

var image = new ImageAsset(
    new SiteAsset("photo", "assets", "photo.jpg", "images/original.jpg"),
    [new ImageVariant("photo-jpeg", "images/photo.jpg", 640, ImageFormat.Jpeg),
     new ImageVariant("photo-webp", "images/photo.webp", 640, ImageFormat.WebP)]);
var imageOptions = new SiteGenerationOptions
{
    Assets = [image.Source],
    AssetTransforms = [image.Transform],
    AssetCacheDirectory = ".lithosharp/assets"
};
// In a layout: ResponsiveImage.Render(context.Assets, image, "Lake at dawn")
```

`ResponsiveImage.Render` encodes alt text and attributes, reads fallback dimensions,
and produces `picture`, format sources, `srcset`, `sizes`, intrinsic width/height,
`decoding="async"`, and `loading="lazy"`. Set `lazy: false` for an eager image.
Each variant has an exact positive width and proportional height; quality is 0–100.
A PNG/JPEG fallback and distinct format/width pairs are required. Unsupported or
invalid raster inputs fail the build. SVG pass-through uses ordinary `SiteAsset`.
Skia encodes PNG/JPEG/WebP without an external executable. Cache fingerprints include
variant settings, managed/native Skia versions and runtime platform.

AVIF requires an explicitly configured, trusted `avifenc`:

```csharp
var encoder = new ExternalAvifEncoder("/opt/libavif/bin/avifenc",
    dependencyFingerprint: "libavif-1.3.0+aom-3.12.1", timeout: TimeSpan.FromMinutes(2));
var avifImage = new ImageAsset(image.Source,
    [image.Variants[0], new ImageVariant("photo-avif", "images/photo.avif", 640, ImageFormat.Avif)], encoder);
```

Use the versions actually installed in the dependency fingerprint. No PATH search,
download or shell command is performed. The adapter hashes executable bytes and
fixed encoder settings, checks tool replacement even on a cache hit, bounds process
execution and accepts only an AVIF-branded output container. Recreate the encoder
and image declarations after changing the tool or codec libraries. See the official
[avifenc manual](https://github.com/AOMediaCodec/libavif/blob/main/doc/avifenc.1.md).
Core generation never requires this tool. The Docs sample provides
`--asset-demo --check` for public files and PNG/WebP variants.
