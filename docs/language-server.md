# Language-server protocol

The bundled language server uses LSP 3.17 over stdio with Content-Length framing. Positions use UTF-16.

The 0.5.0 VS Code extension carries this server with Core 3.0.0. These are the
target versions; the [version matrix](../eng/version-matrix.json) identifies the
selected development candidate separately from formal publication. The server is
not a separately installed NuGet tool. Keep its DLL, runtime configuration and
dependencies together; see [distribution](distribution.md). Markdown inspection
uses the pinned Syntamark runtime/Source pair and the explicitly acquired Markdown profile. MDX body
inspection remains a separate, explicitly restored Node worker path.

## Protocol subset (advertised == implemented)

- `initialize` / `initialized` / `shutdown` / `exit`
- `textDocument/didOpen|didChange|didSave|didClose` (incremental sync, UTF-16)
- `textDocument/publishDiagnostics` (push only, unversioned on close)
- `textDocument/documentSymbol` (Markdown section hierarchy/ranges and heading selection; MDX heading ranges)
- `textDocument/completion` (acquired custom front matter fields and enum/boolean values, and registered Markdown component metadata)
- `textDocument/hover` (acquired static field and component descriptions)
- `textDocument/definition|references` (captured static Markdown references)
- `textDocument/prepareRename|rename` (guarded heading/reference edits when client capabilities permit)
- `workspace/willRenameFiles` (one Markdown file move at a time, when negotiated)
- `lithosharp/referenceScopes|previewRename` and `lithosharp/referenceStatus` (explicit scope selection, preview and availability)
- `$/cancelRequest` (in-flight symbol, completion and hover requests)
- `lithosharp/projectContext` (client-supplied project snapshot, major-version checked)
- `lithosharp/analysisStatus` (server notification for analysis availability, with document/context generation identity)

Anything else answers `-32601` (requests) or is ignored (notifications).
Stdout carries frames only; every log goes to stderr.

The initialize response advertises `positionEncoding: "utf-16"`, incremental
text synchronization, document symbols, completion, hover, definitions and references.
Rename capabilities depend on the client's versioned edit and confirmation support.
Its experimental LithoSharp object
reports the bundled Core identity, Markdown inspection, and on-demand MDX
inspection. Both preflight flags are false: `preflight` is a Tool command, not
an LSP method. See [capability negotiation](tooling-capabilities.md).

## Compatibility rules

- Unknown fields are ignored; unknown methods are rejected, never assumed.
- Position encoding is always UTF-16. Columns count UTF-16 code units, 1-based
  in Core and converted to 0-based only at the LSP boundary, in one place.
- Regressed document versions and out-of-range edits are dropped with an
  stderr note; the server keeps serving and never crashes on abnormal input.
- Incompatible project assemblies are never loaded: contexts are data
  (project snapshots), checked for schema-major compatibility, then used for
  route lookup, built-in binding and explicitly acquired generated static schemas.

## Markdown symbols and unavailable analysis

Markdown symbols use the same snapshot as diagnostics. A symbol's `selectionRange` is the original heading range; its `range` is the corresponding section `SubtreeSpan`. `HeadingLocalKey`, `LocalKey` and `ParentLocalKey` supply the correspondence and hierarchy. If section information is unavailable, only the known heading range is retained. MDX continues to use its separate heading-based symbol path.

Input/resource limits can make facts unavailable (`LSM013`). When no reliable source range exists, `lithosharp/analysisStatus` reports that state separately instead of creating a range at the start of the document. A valid subsequent snapshot clears the status. Clients must reject stale document versions and project/context generations, just as they do for diagnostics. Unavailable or partial facts are not evidence that the document is valid.

Status payloads contain `uri`, `version`, `language`, `state` (`ready` or
`unavailable`), `message`, `lithosharpOpenGeneration` and
`lithosharpContextGeneration`. Diagnostics carry the same open/context generation
fields. Reopening a URI is a new lifecycle even if its version repeats. Missing
MDX dependencies also report unavailable analysis without fabricating a source
diagnostic; restore the worker or correct its settings and restart the server.
`ready` does not mean the document is error-free or that later build stages ran.

New servers also report optional `projectStatus`, `schemaName`, `schemaScope`,
`projectId`, `projectGeneration`, `projectHash`, schema/input hashes and `contextAcquiredAt` fields. Project identity
and acquisition time describe the applied snapshot, not proof that all inputs
are current. `projectStatus` is the Markdown inspection's document resolution
state. `schemaScope` is `built-in-binding`, `static-schema-build-required`, `build-required` or `unknown`: it
describes the supported inspection path, not a successful bind. MDX reports
`unknown` even when a context names a built-in schema. Older servers that omit
these fields leave the corresponding scope unknown. The VS Code extension
invalidates display state on edits, document lifetime changes, context changes
and server replacement, using the same identity guards as diagnostics.

`NoContext` permits syntax diagnostics and navigation but not project-bound schema or route checks.
An explicit Build or Inspect captures generated `SchemaJson` from binders that
implement `IContentFrontMatterSchemaProvider`. Custom snapshots include schema
version `1.0`, project generation and acquisition hash, collection identity,
schema hash, input-set hash, and exact absolute input membership. A schema is
applied only when one current snapshot claims that input; filename suffixes do
not establish membership. Invalid versions, hashes or revision identities are
rejected.
When a newer acquisition is rejected, last-known route evidence can remain,
while generated field candidates, component descriptors and semantic facts are
discarded until a valid refresh.

Custom field names, nullability, statically required values, enum choices and
descriptions come from generated metadata. Static checks use native YAML key,
value and sequence-item locations and the same invariant scalar conversions as
the runtime. Constructors, getters, setters, initializer-dependent requirements
and custom validators are never executed by completion, hover or static schema
description. These constraints remain build-required; a static schema result
does not certify an entire runtime bind. Declarative semantic context similarly
keeps unknown publication decisions deferred.

Project selection, C# or project changes, input changes and a new explicit
acquisition invalidate the old context. Completion and hover use the same
buffer/context generation checks as diagnostics, including while requests wait
for shared analysis. Programmatic callers can create a
`FrontMatterSchemaSnapshot` from generated `SchemaJson` and attach it with
`ProjectInspectionSnapshot.WithSchemas`; they must supply the acquisition hash
and exact input paths themselves.

Markdown section facts and keys are internal coordination data, not additional
public `DocumentInfo` properties or a guarantee that MDX uses Markdown sections.
Symbols and diagnostics share server-owned work for the same snapshot. Canceling
a symbol, completion or hover request cancels that waiter, not shared diagnostic analysis.

## Reference navigation and guarded edits

Reference requests require an open Markdown document and an explicitly acquired,
unambiguous project input scope. Definition and reference lookup use parsed native
reference facts and captured routes. `lithosharp/referenceScopes` lists available
collection/version/locale scopes; pass `referenceScope` when an input has multiple
scopes. Dynamic MDX and closed files remain explicit manual or unavailable scope,
not evidence of a complete reference search.

`lithosharp/previewRename` reports guarded edits, manual scopes, document/hash
identity and current availability without applying them. A heading edit, target
change or one-file move must be revalidated against the current captured buffers.
Unknown or incomplete reference coverage cannot establish that a target is absent.
The optional `expectedSourceTextHash` and document version reject stale requests.

Automatic edits require versioned `documentChanges`. The advertised rename path
also requires confirmation annotations; edits across several files require
`transactional` or `textOnlyTransactional` failure handling. Resource moves are
negotiated separately and accept one `.md` file at a time. Unsupported clients
retain the explicit preview path rather than receiving unversioned edits.
`lithosharp/referenceStatus` reports the remaining manual scopes and captured
document/context generations. C# caller changes require the separate
[C# reference rename preview](csharp-reference-rename.md); a Markdown preview does
not prove that regenerated C# metadata and all downstream callers are correct.
