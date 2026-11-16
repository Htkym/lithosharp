---
title: Assets and PNG images
supported_version: "3.0"
prerequisites: [quickstart]
---

Core registers image references without decoding files or requiring native image
libraries. `ImageSource`, `ImageMetadata` and responsive descriptors describe
explicit URLs, dimensions and variants. Existing JPEG, WebP and AVIF files can be
registered as assets; Core does not generate missing image formats.

This site explicitly adds `LithoSharp.Images`. Its `website-social` transform uses
`RgbaImage.Create`, `FillRectangle` and `PngCodec.Encode`, writes a declared PNG
output with generated width and height metadata, and registers the result through
`CommonSocialImage`. Inspect the generated page's `og:image` metadata and image.
The transform has a configuration and processing fingerprint. It does not read an
undeclared source file or invoke an external renderer.

The initial PNG codec supports 8-bit non-interlaced grayscale, RGB,
grayscale-alpha and RGBA within explicit limits. Palette/interlaced inputs and
other bit depths require prior conversion. `ImageAsset` can resize a supported
PNG; `SocialImageAsset` can composite explicitly registered PNG inputs. These
operations do not supply fonts, JPEG/WebP/AVIF encoders or arbitrary native codecs.

See the paired [asset guide](../../reference/en/assets-and-images/)
for fallback variants, path containment, asset dependencies and cache invalidation.
An incompatible or incomplete cached image descriptor must be regenerated.
