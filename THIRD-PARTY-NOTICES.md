# Third-party notices

LithoSharp redistributes, or depends on, the following third-party packages.
Each remains under its own license.

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
language server include YamlDotNet 18.1.0 and SkiaSharp 4.153.1
managed/native assemblies. Their distributions retain the applicable upstream
notices under `licenses/` alongside their payloads.

- `licenses/YamlDotNet.LICENSE.txt`: upstream v18.1.0 `LICENSE.txt`.
- `licenses/SkiaSharp.LICENSE.txt`: official 4.153.1 NuGet license.
- `licenses/SkiaSharp.THIRD-PARTY-NOTICES.txt`: unchanged official 4.153.1 native
  package notices; Linux, Windows, and macOS package copies are identical.

Original copyright and permission notices are preserved verbatim. Dependencies
restored separately through NuGet or npm retain their own upstream notices.

## WHATWG HTML named character references

The internal HTML tokenizer contains the 2,231 named character reference entries
from https://html.spec.whatwg.org/entities.json, retrieved on 2026-10-09.
The generated representation and provenance are recorded under eng/html/.
Upstream source code portions are available under the BSD 3-Clause license;
the complete, unchanged WHATWG notice is retained in
licenses/WHATWG.HTML.LICENSE.txt and included in the Core NuGet package.
