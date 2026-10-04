# Third-party notices

LithoSharp redistributes, or depends on, the following third-party packages.
Each remains under its own license.

## AngleSharp

- License: MIT
- Project: https://github.com/AngleSharp/AngleSharp

## YamlDotNet

- License: MIT
- Project: https://github.com/aaubry/YamlDotNet

## SkiaSharp / SkiaSharp.NativeAssets.Linux.NoDependencies

- License: MIT
- Project: https://github.com/mono/SkiaSharp
- SkiaSharp bundles native binaries built from Google's Skia
  (https://skia.org/), which is licensed under BSD-3-Clause.

## Bundled redistribution notices

The generator includes YamlDotNet 18.1.0 in its analyzer payload. The CLI and
language server include AngleSharp 1.8.3, YamlDotNet 18.1.0, and SkiaSharp 4.153.1
managed/native assemblies. Their distributions retain the applicable upstream
notices under `licenses/` alongside their payloads.

- `licenses/YamlDotNet.LICENSE.txt`: upstream v18.1.0 `LICENSE.txt`.
- `licenses/AngleSharp.LICENSE.txt`: upstream v1.8.3 `LICENSE`.
- `licenses/SkiaSharp.LICENSE.txt`: official 4.153.1 NuGet license.
- `licenses/SkiaSharp.THIRD-PARTY-NOTICES.txt`: unchanged official 4.153.1 native
  package notices; Linux, Windows, and macOS package copies are identical.

Original copyright and permission notices are preserved verbatim. Dependencies
restored separately through NuGet or npm retain their own upstream notices.
