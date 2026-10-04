# LithoSharp 1.1 features and upgrade notes

[日本語](release-1.1.ja.md)

Candidate status: Core 1.1 and extension 0.1.0 are verification candidates.
NuGet package metadata now targets 1.1.0; final distribution checks are pending.
The extension is not on the Marketplace. Numbers marked *unmeasured* below are
replaced after the final V110-25 measurements; nothing here claims them early.

## Feature table

| Area | Core 1.1 | VS Code extension 0.1.0 |
| --- | --- | --- |
| Markdown | Litho compiler with LIT001/002 diagnostics and opt-in LIT003/004/005 advisories; project-aware inspection snapshots | LIT diagnostics with original IDs, version-guarded display |
| MDX | Analysis-only inspection without plugin/bundle/SSR execution; locked worker (MDX 3.1.1, React 19.2.4, esbuild 0.28.2) | Debounced LSP session, Outline symbols, no build on keystrokes |
| Build/serve | Generation tracking, per-output cache report/reclaim, structured `serve` shutdown | Per-project build/serve state machines, real-output preview shell |
| Migration | Docusaurus analysis/conversion with route oracle, normalized page-set comparison, component functional-change report; pinned 3-site reproduction corpus | — (CLI-driven) |
| Tooling contracts | `lithosharp capabilities`, additive 1.x JSON envelopes, schema `"1.0"` | Language server `lithosharp` over stdio, reporting the Core assembly version |

Explicitly **not** in 1.1:

- Saved-file preview only: unsaved buffers have no preview.
- The MDX worker transpiles TypeScript; it does not type-check.
- No Marketplace distribution yet; install the extension from a local VSIX.
- Browser verification is Chromium-only; Firefox/Safari runs are not claimed.
- Trimming and Native AOT remain unsupported for the CLI/site host.
- AVIF encoding needs an explicitly configured trusted `avifenc` and is otherwise unverified.
- The earlier verification candidate passed Windows, Linux and macOS CI. The current candidate still requires final multi-OS validation and the 30-minute soaks; current soak evidence is rapid edits on Windows.

## Upgrade from 1.0 to 1.1

1. Update all seven Core packages and the tool together; do not mix 1.0 and 1.1 assemblies. Use matching 1.1.0 packages after their release; local release-candidate verification is still pending.
2. Rebuild the site and review content warnings, links and custom CSS.
3. The Markdown compiler is unchanged since 1.0: footnotes, definition lists and the other listed Markdig extensions stay unsupported (see [Known limitations](known-limitations.md#markdown-compatibility)).
4. `markdown-compat --advisory on` and `--project` share the editor inspection; advisories never change default output.

SkiaSharp is updated to 4.153.1, whose decoder no longer accepts RAW or DNG.
Convert those images to PNG or JPEG before using them as responsive-image sources
or site icons; see [assets and images](assets-and-images.md).
YamlDotNet 18.1.0 limits deserializer recursion to 130 by default. This applies
to `MarkdownFrontMatterYaml.Deserialize<T>`; flatten deeply nested inputs rather
than disabling the limit. Typed content loaders use their own strict parser,
and the static-content generator keeps its existing nesting limit of 64.

## API and schema policy

Public signatures are preserved; new APIs are additive within 1.x and recorded
in `src/LithoSharp/PublicAPI.Unshipped.txt`. Structured CLI/serve output keeps
schema `"1.0"`; unknown JSON fields are ignored. Anything else waits for 2.0.
See [compatibility contract](compatibility-contract.md) and
[CLI compatibility](cli.md#structured-output-and-1x-compatibility).

## Requirements and version combinations

- .NET 10 SDK, with 10.0.300 as the minimum supported version. Current release
  validation uses SDK 10.0.401 and runtime 10.0.12. Source generators target `netstandard2.0`
  against Roslyn 4.14.0; consumers need a compatible SDK, not a preview.
- `serve` also needs the ASP.NET Core shared framework from the SDK.
- Linux needs the [.NET native prerequisites](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual#dependencies),
  including `libstdc++.so.6` (`libstdc++6` on Ubuntu, `libstdc++` on Alpine).
  SkiaSharp 4.153.1 also requires this C++ runtime, including its
  `Linux.NoDependencies` package.
- Markdown-only sites need no Node.js. MDX needs Node.js 24.13.0 plus an explicit
  worker restore; arbitrary Node versions and npm packages are not certified.
- Use one Core/Tool/Generator version set per repository. The extension 0.1.0
  talks to the 1.x language server over stdio and reports the Core version in
  `serverInfo`; mismatched Core assemblies across the CLI and the server are not
  supported. The extension requires VS Code `^1.139.0`.
