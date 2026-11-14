# Reference inspection and editing

Reference inspection uses an explicitly acquired project catalog and current editor buffers. It does not run a project factory, MSBuild, or user code while typing. Markdown destinations, reference definitions, uses, and headings come from Syntamark syntax and exact UTF-16 ranges. Code examples and frontmatter values do not become references through text searches.

The language server provides completion, hover, definition, references, heading rename, and the `workspace/willRenameFiles` preparation request. A destination change or document move can also be reviewed through `lithosharp/previewRename`. The automatic Markdown scope consists of captured buffers. Closed documents, dynamic expressions, unavailable syntax, ambiguous targets, and C# callers remain explicit review scopes.

## Select a catalog scope

Every route retains its collection, version, and locale. The server selects a source document's scope when exactly one acquired tuple matches. When several tuples match, it requires an explicit choice.

Call `lithosharp/referenceScopes` with `{ "textDocument": { "uri": "file:///..." } }` to obtain `scopes`, `sourceVersion`, and the project and client context generations. Each scope contains `collection`, `version`, `locale`, and `publicPaths`. Reference and rename requests accept the selected tuple as `referenceScope`. Discard a selection if its source version or acquisition generation changes.

Resolution distinguishes `Resolved`, `Ambiguous`, `Unresolved`, and `Unavailable`. Missing or incomplete catalog evidence does not prove that a target or reference is absent. Alias routes remain separate completion candidates.

## Review and apply edits

`lithosharp/previewRename` accepts a document and position with `newName`. Set `kind` to `target` to change a parsed link destination. For a document move, supply `newUri` instead of a position; an optional `newPublicPath` declares an actual route change. The server does not derive a new public URL from the filename.

The preview includes guarded text edits, physical moves, source version and text hash, `manualScopes`, `isComplete`, and `canApply`. Each text edit retains its expected original text. The server validates complete captured buffers and versions before returning an edit, and checks their identity again before publishing the response. Stale expected input is rejected.

Standard edits use versioned `documentChanges`. The client must declare this capability. Multiple changed documents require transactional text-edit support. Remaining manual scopes require confirmation annotations and a client that honors them. The server refuses an unsupported application method instead of returning an unversioned replacement set.

`workspace/willRenameFiles` currently prepares one move at a time. Its response updates Markdown references; the editor performs the requested file move. Labels, surrounding titles, relative or bundle-root forms, source escapes, and line endings are preserved where exact source mappings permit an edit. An unknown mapping or ambiguous target stays manual.

C# review is available separately through `lithosharp preview-csharp-rename`, with explicit source files, reference assemblies, metadata type name, member name, and new identifier. A document name is not guessed to be a C# symbol. Generated C# files are not direct edit targets.

## OKF input evidence

OKF uses the same acquired routes and the bundle reader's relation classifications. Concepts, directory indexes, logs, and auxiliary assets retain distinct input kinds. Existing outside-bundle and unresolved relations remain authoritative. An auxiliary target can be resolved without parsing its bytes as Markdown.

The input policy is separate from the Markdown body profile and native YAML profile. Explicit OKF input metadata allows an index or log without frontmatter and checks a concept's required string `type`. Unknown OKF metadata remains native YAML syntax and is not bound to an ordinary document DTO.

After a known input changes, static input classification can remain available while routes, relation receipts, and semantic closure are discarded. This retained classification does not establish reference completeness. File membership changes and project changes require a fresh explicit acquisition.
