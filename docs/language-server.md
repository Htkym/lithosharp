# Language-server protocol

The bundled language server uses LSP 3.17 over stdio with Content-Length framing. Positions use UTF-16.

## Protocol subset (advertised == implemented)

- `initialize` / `initialized` / `shutdown` / `exit`
- `textDocument/didOpen|didChange|didSave|didClose` (incremental sync, UTF-16)
- `textDocument/publishDiagnostics` (push only, unversioned on close)
- `textDocument/documentSymbol` (heading hierarchy, owned ranges only)
- `$/cancelRequest` (in-flight symbol requests)
- `lithosharp/projectContext` (explicit snapshot in/out, major-version checked)

Anything else answers `-32601` (requests) or is ignored (notifications).
Stdout carries frames only; every log goes to stderr.

## Compatibility rules

- Unknown fields are ignored; unknown methods are rejected, never assumed.
- Position encoding is always UTF-16. Columns count UTF-16 code units, 1-based
  in Core and converted to 0-based only at the LSP boundary, in one place.
- Regressed document versions and out-of-range edits are dropped with an
  stderr note; the server keeps serving and never crashes on abnormal input.
- Incompatible project assemblies are never loaded: contexts are data
  (project snapshots), checked for schema-major compatibility, then used for
  route lookup and built-in front matter binding only.
