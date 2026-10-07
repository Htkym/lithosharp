# LSP transport decision (V110-14)

Date: 2026-09-27. LSP 3.17 is the baseline; only the subset below is used.

## Decision

Standard library only (`System.Text.Json` over stdio). No JSON-RPC/LSP
transport package is added.

## Grounds

- The server speaks 7 methods plus one namespaced notification. A full client
  library (protocol models, routing, hosting) would dwarf the subset and pull a
  dependency graph that needs separate license and vulnerability tracking.
- Framing is Content-Length over pipes: exact byte reads, no socket Concurrency,
  about 120 lines including resync. It is pinned by tests for split writes,
  concatenated messages, UTF-8 byte lengths, garbage resync, and stdout purity.
- `System.Text.Json` is already the repository JSON standard (CLI envelopes,
  host protocol, snapshots).

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
  (V110-10 snapshots), checked for schema-major compatibility, then used for
  route lookup and built-in front matter binding only.
