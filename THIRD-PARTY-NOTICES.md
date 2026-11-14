# Third-party notices

LithoSharp redistributes, or depends on, the following third-party packages.
Each remains under its own license.

## Syntamark and Syntamark.Source

- License: MIT
- Project: https://github.com/Htkym/syntamark
- The selected runtime/Source pair supplies Markdown, native YAML and raw source facts.
  Compiler packages embed the selected portable source; the CLI and bundled
  language server redistribute the runtime. The unchanged upstream notice is
  retained in `licenses/Syntamark.LICENSE.txt`.

## Historical YamlDotNet notices

- License: MIT
- Project: https://github.com/aaubry/YamlDotNet
- Current products do not depend on or redistribute YamlDotNet. The repository
  retains the original v18.1.0 notice for historical provenance.

## Historical SkiaSharp notices

- License: MIT
- Project: https://github.com/mono/SkiaSharp
- SkiaSharp bundles native binaries built from Google's Skia
  (https://skia.org/), which is licensed under BSD-3-Clause.
- Current products do not depend on or redistribute SkiaSharp or its native
  assets. The repository retains the original notices for historical provenance.

## Bundled redistribution notices

The generator and analyzer embed Syntamark portable source. The CLI and language
server redistribute its runtime. These distributions retain the upstream notice
in `licenses/Syntamark.LICENSE.txt` alongside `THIRD-PARTY-NOTICES.md`.

Historical repository-only notices are `licenses/YamlDotNet.LICENSE.txt` from
upstream v18.1.0 and `licenses/SkiaSharp.LICENSE.txt` and
`licenses/SkiaSharp.THIRD-PARTY-NOTICES.txt` from the official 4.153.1 packages.
They are not included in current distributions.

Original copyright and permission notices are preserved verbatim. Dependencies
restored separately through NuGet or npm retain their own upstream notices.

## WHATWG HTML named character references

The internal HTML tokenizer contains the 2,231 named character reference entries
from https://html.spec.whatwg.org/entities.json, retrieved on 2026-10-09.
The generated representation and provenance are recorded under eng/html/.
Upstream source code portions are available under the BSD 3-Clause license;
the complete, unchanged WHATWG notice is retained in
licenses/WHATWG.HTML.LICENSE.txt and included in the Core NuGet package.
