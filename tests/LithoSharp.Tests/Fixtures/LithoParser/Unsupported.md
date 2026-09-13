# Litho Parser Unsupported Constructs

The Litho frontend implements the CommonMark 0.31.2 core blocks and inlines,
the GFM 0.29 tables, task lists, strikethrough, and autolinks, plus the C07
extensions (admonitions, directives, math, diagrams, code metadata) needed by
LithoSharp content. Everything below stays literal text; it is never silently
treated as success. Each ID maps to `LithoLimits.UnsupportedIds`. The retired
IDs U03 (math), U04 (diagrams), U05 (alert blocks), and U06 (custom containers)
were implemented in C07 and are never reused.

- U01-footnotes: `[^a]` references and definitions stay literal.
  Footnote syntax additionally emits a LIT001 warning diagnostic instead of failing silently.
- U02-definition-lists: `Term` followed by `: definition` stays paragraphs.
- U07-abbreviations: `*[HTML]: ...` definitions stay paragraphs.
- U08-citations: `""cite""` stays literal text.
- U09-figures: `^^^` blocks stay paragraphs.
- U10-footers: `^^` footers stay paragraphs.
- U11-media-links: bare media URLs render as plain autolinks, not embeds.
- U12-grid-tables: `+-+` grids stay paragraphs.
- U13-generic-attributes: `{#id .class}` stays literal text.
- U14-list-extras: `a.`/`i.` ordered markers stay paragraphs.
- U15-subscript-superscript: single `~` and `^` stay literal text.
- U16-inserted-marked-text: `++ins++` and `==mark==` stay literal text.
- U17-emoji-smarty-pants: `:smile:` and `--`/`...` stay literal text.

Reference: CommonMark 0.31.2 (https://github.com/commonmark/commonmark-spec,
CC-BY-SA 4.0) and GFM 0.29-gfm (https://github.com/github/cmark-gfm,
CC-BY-SA 4.0). The official spec.txt files are not vendored; the supported
suite in `LithoSupportedSuiteTests` pins the implemented subset instead.
