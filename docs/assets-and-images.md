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

## Existing responsive images

Core can copy JPEG, WebP, AVIF, SVG and other existing image files without decoding
them. Responsive markup uses `ImageDescriptor` and an explicit fallback selected
from its variants. No Images package or transform is required:

```csharp
using LithoSharp;

var small = new SiteAsset("photo-small", "assets", "photo-small.jpg", "images/photo-small.jpg",
    new ImageMetadata(320, 160, "image/jpeg", ImageMetadataProvenance.Declared));
var large = new SiteAsset("photo-large", "assets", "photo-large.jpg", "images/photo-large.jpg",
    new ImageMetadata(640, 320, "image/jpeg", ImageMetadataProvenance.Declared));
var smallSource = new ImageSource(small);
var largeSource = new ImageSource(large);
var picture = new ImageDescriptor([smallSource, largeSource], fallback: largeSource);
var imageOptions = new SiteGenerationOptions { Assets = [small, large] };
// In a layout: ResponsiveImage.Render(context.Assets, picture, "Lake at dawn")
```

`ImageSource` also accepts an external HTTP(S) URL or a registered transform output.
Caller-supplied dimensions have `Declared` provenance. Supported decoding and
generation establish `Decoded` and `Generated` facts respectively; copying a file
does not establish that it can be decoded. Unknown dimensions are omitted for a
single image. Multiple responsive variants require known dimensions and media
types, with distinct media-type/width pairs.

`ResponsiveImage.Render` escapes alt text and attributes and emits `picture`,
format sources, `srcset`, `sizes`, known intrinsic width/height, `decoding="async"`
and `loading="lazy"`. Set `lazy: false` for an eager image. Rendering does not open
a codec to discover dimensions. Register the exact declaration objects used by the
descriptor; an unregistered alias does not become valid by supplying metadata.

## Optional PNG processing

Use matching Core and `LithoSharp.Images` packages when PNG processing is needed.
Declare PNG variants with a positive width and an optional BCL `CompressionLevel`,
then select the fallback explicitly:

```csharp
using LithoSharp.Images;

var fallback = new ImageVariant("photo-png", "images/photo.png", 640);
var image = new ImageAsset(
    new SiteAsset("photo", "assets", "photo.png", "images/original.png"),
    [fallback], fallback);
var transformed = new SiteGenerationOptions
{
    Assets = [image.Source],
    AssetTransforms = [image.Transform],
    AssetCacheDirectory = ".lithosharp/assets"
};
// ResponsiveImage.Render(context.Assets, image.Descriptor, "Lake at dawn")
```

The limited PNG decoder accepts noninterlaced 8-bit grayscale, RGB,
grayscale-alpha and RGBA input. Encoding produces RGBA8 PNG. Resize preserves
aspect ratio using premultiplied-alpha bilinear interpolation; height rounds half
up to at least one pixel. `ImageLimits` bounds input, chunks, dimensions, pixels
and decoded allocations. Invalid input, unsupported conversion, resource limits
and cancellation remain distinct failures.

Palette/16-bit/Adam7 PNG, animation, EXIF orientation, non-sRGB color management,
JPEG/WebP/AVIF conversion, SVG rasterization and text drawing are unsupported.
Keep these as existing assets or provide pre-generated variants. The
[Images package guide](../src/LithoSharp.Images/README.md) lists the exact PNG
profile, pixel operations and explicit background/logo composition API.

Transform metadata is associated with output identity and byte hashes in the
versioned cache. Missing, invalid or mismatched metadata causes regeneration;
cache hits retain the same dimensions, media type and provenance.

## Social images and migration

Set `SiteGenerationOptions.CommonSocialImage` for a shared existing image, or
`SocialImages` for overrides keyed by relative page output path. Registered PNG
composition results can use the same `ImageSource` contract. A favicon does not
implicitly generate a social image.

Migrate old format/quality overloads to PNG `CompressionLevel` settings or
pre-generated files. `ExternalAvifEncoder` is removed. Replace
`SiteThemeOptions.DefaultSocialSubtitle` and implicit social-generation flags
with explicit images; title-bearing images must be prepared in advance.

The [Docs sample](../samples/LithoSharp.DocsSample/README.md) provides
`--asset-demo --check` for public files and PNG transforms:

```sh
dotnet run --project samples/LithoSharp.DocsSample -- --asset-demo --check --output .local/assets-site
```

Run that command from the repository root. Generation-time
[quality checks](site-quality.md) inspect references to registered assets.
[`check-output`](generated-output-checks.md) can inspect an existing distribution's
local targets but cannot establish registered asset ownership or unused assets.
