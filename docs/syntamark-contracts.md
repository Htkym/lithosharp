# Syntamark contracts in LithoSharp

[日本語](syntamark-contracts.ja.md)

LithoSharp consumes a fixed Syntamark runtime/Source pair. The selected version
and publication state are recorded in [the version matrix](../eng/version-matrix.json).
The runtime provides public analysis APIs; Source embeds the portable parser in
compiler hosts. Source is not a second copy of the runtime's public facade.
Do not mix versions or substitute a Source package merely because its name matches.
The [Syntamark repository](https://github.com/Htkym/syntamark) owns its complete
standalone API reference and installation guide.

## Profiles and rendering

`lithosharp-markdown/1` remains LithoSharp's default. `commonmark/0.31.2` and
`syntamark-docs/2` are explicit choices. The docs profile enables native footnotes
and directive syntax. LithoSharp owns HTML rendering, heading slugs, routes and
registered typed component execution; a parser fact does not authorize executable
expressions or arbitrary user components. MDX has its separate trusted worker.
See [profiles](markdown-profiles.md), [MDX](mdx.md) and [typed components](typed-markdown-collections.md).

## Source positions and selected analysis

Scope, source identity, source version, text hash, options hash, profile and parser
contract identify the input. Original-source positions count UTF-16 code units,
including a BOM and original line endings. Consumers must not normalize the input
and apply its positions to another string. The portable `Blocks` output used by
the embedded compiler is body-relative; it is not a second public runtime AST.
Public facts, unsupported candidates and edit ranges
refer to the complete original input. Include/source mappings retain their own
precision and related origin; missing precision is not an invented line number.

`MarkdownParser.Analyze` and `MarkdownParser.CreateAnalysisSession` request
`MarkdownAnalysisParts`. `All` does not include `UnsupportedSyntax`; include that
flag explicitly for candidate diagnostics. Inspect `Status` and `GetAvailability`
for each individual part before reading a possibly null output; `GetAvailability`
does not accept `None`, `AllFacts` or `All`. `NotRequested`, `Partial`
and `Unavailable` are distinct from a complete empty result. Unsupported syntax,
resource limits and cancellation do not establish whole-document validity.
A front-matter scan reads the header boundary without promising full body analysis.
See [diagnostics](diagnostics.md) and [language-server acquisition](language-server.md).

## Incremental sessions

Sessions are immutable and opt-in. `ApplyEdits` accepts sorted disjoint edits in
the preceding input's UTF-16 coordinates and checks the expected text hash.
It returns a new session; failure or cancellation leaves the previous one intact.
`Mode` and `Fallback` describe the actual path. Reused/rebased block counts and
`ParsedBlocks` describe block reuse; `ParsedBlocks` counts eligible plain
paragraphs, not every block or fact projection. `ScanUnits` counts logical
canonical-parser debits, including replay; it is not an executed-work or timing
measurement. A canonical full parse is a supported fallback, not a delta success.
Null options retain the prior session's options.

Document analysis and transformed Markdown bodies require separate sessions with
their own text/options/identity. LithoSharp's retained body analysis also requires
the exact requested parts, body input options and a null `SourceVersion`; an
original document's versioned result is not interchangeable with that body result.
An editor applies sequential protocol changes in
order and suppresses results from older generations. A selective analysis is not
itself an incremental session. See [incremental builds](incremental-builds.md) for
render caches, diagnostics and dependencies, which are separate from parser reuse.

## YAML, footnotes and proofreading

The shared YAML syntax tree preserves raw text and unknown values. It supports
block and flow mappings/sequences, plain/quoted scalars, and literal/folded block
scalars within its documented limits. Tags, directives, anchors, aliases and
complex keys are unsupported; malformed syntax and resource limits have separate
diagnostic kinds. None is silently converted into a successful complete tree.
The typed DTO binder supports its documented mapping subset; it does not turn
the syntax tree into an unrestricted serializer. Use [typed YAML](typed-yaml-collections.md)
and [migration](migration-3.0.md) for the supported DTO contract. OKF uses its own
conformance policy and preserves unknown/raw input independently of DTO binding.

The docs profile resolves footnote definitions and references from one snapshot;
other profiles preserve their literal behavior. Duplicate definitions, unresolved
uses and original labels remain available to the caller. LithoSharp renders the
resolved numbers and backlinks without introducing another Markdown lexer.

Proofreading consumes explicit text regions and source mapping. It does not run
a remote model during parsing, and a suggested change is not applied merely
because analysis completed. The CLI requires explicit proofreading execution and
can return an edit preview; it does not write back to a file. An applicable edit
requires an exact native range. Line-only findings are at most `CoveringTokens`
and do not authorize automatic replacement. The API's explicit `Apply` checks source
identity, version, text hash and expected raw text before returning a new string.
It does not write a file either. See [proofreading](proofreading.md),
[reference editing](reference-editing.md) and [known limits](known-limitations.md).
