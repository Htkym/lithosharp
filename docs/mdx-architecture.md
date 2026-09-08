# MDX integration contract

Status: architecture contract recorded at phase 10, 2026-09-08. The implemented
public API is documented in [the MDX guide](mdx.md). Acceptance evidence is tracked in
[the parity matrix](docusaurus-parity-matrix.md).

## Existing identities and variants

Reuse `ContentCollectionId` for the collection and `ContentEntryId` for the
stable document identity. A source path, display title and public URL are not
identifiers. Both existing IDs use NFC normalization and ordinal comparison.
Do not infer version or locale by splitting an existing opaque ID.

The logical key is `(CollectionId, DocumentId, VersionId, Locale)`. The legacy
variant has no explicit version and uses `SiteSettings.Language`; it preserves
its existing `PageId`, URL and artifact owner. In particular, the existing
`ContentPageIdentity.Create` length-prefixed representation remains unchanged.
The absence of an explicit version is distinct from a version named `current`.
The default displayed version is a routing choice, not a document identity.

Phase 14A adds the variant fields to the existing page/collection integration,
and resolves the complete key through `SiteRouteTable`. Variant IDs must be
encoded with unambiguous length prefixes in a separate namespace for explicit
variants; concatenation with delimiters alone is not sufficient. Validate
duplicate keys and output collisions before rendering. Typed references and
MDX links must use the same resolver. Missing variants require an explicit
fallback rule, and must not silently point at another document. Subset builds
must declare their ownership scope before stale-output removal is enabled.

Example: `(guide, install, null, en)` retains the existing legacy route;
`(guide, install, v1, ja)` is another page of the same document. Neither implies
a `/v1/ja/` URL until the configured route convention assigns one.

## ADR: loader and execution boundary

Keep the current `.md` loader and Markdig rendering unchanged. An opt-in MDX
integration uses the official MDX 3 compiler on raw MDX, not on rendered HTML.
Share the existing strict YAML parser, binder and generated schema; do not
parse YAML again in JavaScript. Preserve the original source and body offset
so diagnostics map to original lines and columns. Roslyn reads only static
metadata and never starts Node or evaluates exports.

The Node worker is a trusted build-time executable, not a sandbox for hostile
JavaScript. Starting it requires an explicitly enabled MDX integration.
Ordinary Markdown sites do not probe for Node, restore npm packages or start a
worker. Build does not install dependencies. An explicit restore operation is
separate from build and from running site-supplied scripts.

Use one reusable worker per build/watch session, with bounded requests and
explicit cancellation. Start it through `ProcessStartInfo.ArgumentList`, not
a shell command assembled from input paths. On cancellation or timeout, stop
the process tree and discard incomplete results. Dependency updates restart
the worker. No Node process is required to serve the generated site.

## ADR: versioned protocol

Protocol version 1 uses UTF-8 JSON messages, one complete message per line on
standard output; standard error carries worker logs. A handshake reports the
protocol and exact runtime/compiler/bundler versions before compilation.
Messages carry a request ID. Unknown versions, duplicate/missing IDs,
malformed JSON, oversized results, unexpected process exit and timeout become
structured build diagnostics. No partial response is a successful build.

The request contains the source-relative entry, original body offset, declared
input root, isolated work directory, component map identity, compiler settings,
public props, locale, fixed timestamp and ID prefix. Private configuration is
not serialized wholesale. The response contains rendered body HTML, browser
entry references, emitted file descriptors, transitive input fingerprints,
structured headings/links/text and original-source diagnostics. Physical
paths are build-only metadata and cannot appear in published source maps.

C# validates all descriptors against the declared roots, rejects traversal,
reparse points, duplicate/case-conflicting output names and undeclared files,
and verifies content hashes. Worker output never selects a final output
directory. Public DTOs accept only schema-validated JSON data with explicit
fields; functions, arbitrary exports and service objects are not DTOs.

## ADR: rendering ownership

C# owns the document, head, navigation and an explicit body container. React
owns the contents of that container. Use hydration-capable React server
rendering with completion waiting for supported Suspense/lazy content, and
`hydrateRoot` with identical props, component mapping, locale and ID prefix.
Do not use `renderToStaticMarkup` for the hydrated path. Client-only imports
must be deferred until the browser and have a matching static fallback.

Accept worker HTML at a named trusted HTML boundary; do not weaken text, URL
or attribute encoding elsewhere. Embed public JSON using the default safe
System.Text.Json encoder and inert JSON script content, never interpolated
executable JavaScript. Test `</script>` and canary secrets. Resolve asset URLs
before rendering; later HTML rewriting must not change React-owned DOM.

Page hydration is the R1 baseline. Phase 17B may introduce explicit/registered
islands, retaining page hydration for shared Context, Portal and cross-root
state. No claim of automatic safe splitting of arbitrary React trees is made.

## ADR: graph, assets and cache

Discover and compile in an isolated work directory before final plan
validation. Import the bundler metafile into the existing `BuildInput`,
`BuildNode` and `AssetRegistry` integration. One shared node owns each shared
chunk; pages reference that owner. Do not introduce another publication
manifest or let Node write to the final destination.

Cache keys include source bytes, transitive imports, public props, component
mapping, configuration, lockfile, restored dependency identities, worker and
tool versions, declared environment/time inputs and rendering mode. A lockfile
alone does not prove the installed modules are unchanged. Arbitrary code with
undeclared file/network/time/environment dependencies is not cacheable.

The existing staging, output validation, sidecar and rollback transaction
commits all MDX outputs with other artifacts. Failed discovery, cancellation,
conflicting output ownership and corrupt cache must preserve the previous
complete site. Watch includes imported modules; no-op and single-import-change
behavior must be demonstrated before cache support is marked complete.

## 日本語での契約要約

既存のコレクションID・文書ID・ルート表を使い、版と言語を同じ識別契約へ追加する。
既定のサイトのIDとURLは変えない。MDXは明示的に有効にし、通常のMarkdown生成には
Nodeを要求しない。YAMLの検証と公開条件はC#側、MDXの解釈とReact描画はworker側が担う。
出力は既存のビルド計画とトランザクションへ登録する。公開するデータを限定し、
workerの失敗やキャンセルによって既存サイトが壊れないことを実装時に検証する。

## Primary references

- [MDX compiler](https://mdxjs.com/packages/mdx/)
- [React hydration](https://react.dev/reference/react-dom/client/hydrateRoot)
- [esbuild input/output metadata](https://esbuild.github.io/api/#metafile)

These sources describe the external APIs. Protocol, identities and ownership
above are LithoSharp design decisions, not claims of external compatibility.
