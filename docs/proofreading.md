# Optional proofreading

`LithoSharp.Proofreading` selects prose with Syntamark's syntax and source
projections. Python and Yomiyasu are required only when you explicitly call the
external proofreader. Site generation and editor startup do not start it.

The adapter supports Yomiyasu 3.x with JSON format 2. Select the Python executable
and installed `yomiyasu_lint.py` yourself; LithoSharp does not install or discover
them. It invokes the lint CLI with `--json --no-words -`, without a time limit.
Cancellation stops the owned process. The stdout limit is 4,194,304 UTF-16 code
units, with a separate 65,536-code-unit limit for stderr.

```csharp
using LithoSharp.Proofreading;
using Syntamark;

var source = new SourceSnapshot("guide.md", "editor-42", markdown);
var selected = MarkdownProofreading.Select(source,
    protectedIdentifiers: ["System.String", "DocumentCatalog"]);
var result = await YomiyasuProofreader.CheckAsync(
    selected, pythonExecutable, lintScript, cancellationToken);
var diagnostics = result.Findings.Select(finding => finding.Diagnostic);
```

Headings, paragraphs, table cells and link labels are selected. Code, HTML tokens
and blocks, math, image labels, link destinations and URLs are excluded. Add API
identifiers that appear outside inline code to `protectedIdentifiers`; matching
uses exact ordinal decoded text. `Exclusions` records each policy reason and its
known original range. Separate segments preserve gaps around excluded content.
The submitted prose escapes Markdown control characters so decoded literal
backticks or brackets do not become new markup in Yomiyasu.

Front matter is separated by the native header scanner independently of the
selected Markdown profile. The body uses that profile's Body input. Missing or
incomplete syntax is not a successful empty proofreading result. Inspect `WasRun`,
`State`, `ParseStatus` and exclusions when presenting coverage.

JSON-format-2 findings identify a line, rather than an exact replacement range.
Their locations map through BOM, CRLF, supplementary Unicode characters and
native projections to the original Markdown. Their precision is at most
`CoveringTokens`; `finding.PreviewReplacement(text)` therefore shows an
unavailable automatic edit. A message or snippet is never searched to invent a
token position.

An application can preview a replacement for an explicitly selected decoded
code-point range with `selected.PreviewReplacement(segment, start, length, text)`.
Only one exact native mapping permits applying the preview. Entities such as
`&amp;` retain token-covering precision. `preview.Apply(currentSnapshot)` checks
source identity, version, text hash and expected original text, and returns new
text without writing a file. A stale snapshot or ambiguous range is rejected.

The standalone command evaluates no project and writes no source:

```text
lithosharp proofread guide.md --python /path/to/python --yomiyasu /path/to/yomiyasu_lint.py --protect System.String --format json
```

JSON includes selected segments and diagnostics. An optional
`--preview segment:start:length --replacement text` previews an explicit selection
using zero-based segment and code-point positions. It does not apply the change.

Findings are editorial review candidates. They do not certify technical content,
meaning preservation or OKF verification metadata.
