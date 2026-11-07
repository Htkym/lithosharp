# Language-server protocol

The bundled language server uses LSP 3.17 over stdio with Content-Length framing. Positions use UTF-16.

The 0.3.0 VS Code extension carries this server with Core 2.1.3. The server is
not a separately installed NuGet tool. Keep its DLL, runtime configuration and
dependencies together; see [distribution](distribution.md). Markdown inspection
uses the fixed Syntamark 1.2.0 pair and `lithosharp-markdown/1` profile. MDX body
inspection remains a separate, explicitly restored Node worker path.

## Protocol subset (advertised == implemented)

- `initialize` / `initialized` / `shutdown` / `exit`
- `textDocument/didOpen|didChange|didSave|didClose` (incremental sync, UTF-16)
- `textDocument/publishDiagnostics` (push only, unversioned on close)
- `textDocument/documentSymbol` (Markdown section hierarchy/ranges and heading selection; MDX heading ranges)
- `$/cancelRequest` (in-flight symbol requests)
- `lithosharp/projectContext` (client-supplied project snapshot, major-version checked)
- `lithosharp/analysisStatus` (server notification for analysis availability, with document/context generation identity)

Anything else answers `-32601` (requests) or is ignored (notifications).
Stdout carries frames only; every log goes to stderr.

The initialize response advertises `positionEncoding: "utf-16"`, incremental
text synchronization and document symbols. Its experimental LithoSharp object
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
  route lookup and built-in front matter binding only.

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

`NoContext` permits syntax diagnostics and navigation but not project-bound schema or route checks. Context snapshots bind built-in schemas only; custom C# schemas remain compiler/site-build checks.

Markdown section facts and keys are internal coordination data, not additional
public `DocumentInfo` properties or a guarantee that MDX uses Markdown sections.
Symbols and diagnostics share server-owned work for the same snapshot. Canceling
a symbol request cancels that waiter, not shared diagnostic analysis.
