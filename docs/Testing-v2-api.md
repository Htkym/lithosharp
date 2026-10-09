# Testing v2 snapshot and query contract

Testing v2 replaces the mutable third-party DOM getter with a LithoSharp-owned read-only snapshot. Existing Parse, RenderComponent, RenderLayout, AssertElement/Text/Attribute/Meta/Link/Image and the disposable SiteTestDocument ownership pattern remain. Document removal is a source and binary breaking change; the old DOM type is not emulated.

```csharp
using LithoSharp.Testing;
using var page = SiteTestDocument.Parse("<main><p class='lead'>A &amp; B</p></main>");
var first = page.Snapshot.Query("main > p.lead");       // null if absent
var text = first?.TextContent;                         // "A & B"
var all = page.Snapshot.QueryAll("main p");             // unique, read-only, tree order
page.AssertText("main > p", "A & B");
```

## Ownership and data

SiteTestDocument.Snapshot is an HtmlTestDocument. Query returns HtmlTestElement?; QueryAll returns IReadOnlyList<HtmlTestElement>. Missing matches are null/empty; an assertion mismatch remains SiteTestException. Lists expose no mutable array. Parallel reads are independent while the owner is alive. Dispose is idempotent and invalidates the owner, retained snapshots and retained elements: subsequent operations throw ObjectDisposedException. Retained immutable list metadata such as Count remains readable, but element accessors reject use. Holding any view retains the owning tree's memory. Dispose racing a query may stop it; an entire concurrent call is not an atomic transaction. There is no persistent query cache or independent snapshot lifetime.

TagName/NamespaceUri are canonical parsed local name/namespace. Children/Parent/PreviousSibling/NextSibling describe element relationships, skipping text/comments. SourceStart/SourceLength are raw UTF-16 spans; Line/Column are one-based raw line/UTF-16 column. Synthetic nodes have null coordinates. GetAttribute(name, namespaceUri = null) returns decoded values or null; null namespace selects ordinary attributes. Use the XLink URI for xlink:href.

HTML tag/attribute names compare ASCII case insensitively; foreign names preserve case. IDs/classes are ordinal in standards mode and ASCII insensitive in quirks mode. Attribute value matching uses [HTML's ASCII case-insensitive attribute table](https://html.spec.whatwg.org/multipage/semantics-other.html#case-sensitivity-of-selectors) or explicit i/s flags; no culture or Unicode folding.

TextContent is exact decoded descendant text, including script/style, without visibility rules or whitespace collapse. It is not innerText or search text. Document TextContent concatenates the connected tree; Title collapses HTML ASCII whitespace. Document queries include html, with :scope referring to html. Element queries return descendants only; :scope refers to the queried element, so Query(":scope") is null and Query(":scope > p") selects children. Selectors can inspect ancestors outside an element query root, but return only its descendants.

Template contents form a separate inert fragment, excluded from ordinary queries, Children and TextContent. TemplateContent exposes that fragment's element roots for explicit inspection. Those roots have no element Parent and can expose their descendants. Foreign content uses the same bounded HTML parser as document parsing.

## Declared selectors and errors

The bounded recursive-descent grammar uses [Selectors Level 4](https://www.w3.org/TR/2026/WD-selectors-4-20260122/) and [CSS Syntax Level 3](https://www.w3.org/TR/css-syntax-3/) as references. Full CSS/browser equivalence is not claimed.

Supported: type/universal, ID/class, attribute presence and =/~=/|=/^=/$=/*= operators, quoted/unquoted values, i/s flags, descendant/child/adjacent/general sibling, selector lists, :scope, :first-child/:last-child/:only-child, :nth-child(An+B), :not/:is/:where with nested complex selector lists, CSS escapes, Unicode identifiers, quoted-string newline continuation, comments, signed An+B and odd/even. Escaped An+B name tokens are supported.

Comments are supported between simple selectors and at whitespace boundaries; they do not splice name tokens or namespace/operator punctuation. Attribute i/s flags can directly follow a quoted value or a separating comment.

Every branch of :is/:where is validated strictly, instead of CSS's forgiving-list behavior. Unprefixed types and *|name match any namespace; |name selects an empty namespace. Ordinary/[|name] attributes select no namespace; [*|name] can match any namespace. Named prefixes and @namespace declarations are unsupported. GetAttribute accepts explicit namespace URI reads.

Only an unescaped `*` token is a wildcard. Escaped `\*` and `\2a` are literal names: type selectors with those names match no ordinary HTML elements, while `[\*]` reads an attribute named `*`. An escaped `*` used as a namespace prefix remains a named prefix and throws HtmlSelectorUnsupportedException.

:has(), pseudo-elements, interaction/form/browser-state and unlisted pseudos, and :nth-child(... of selector) are unsupported. No mutation, serialization, scripting or arbitrary Web API is exposed.

Limits: selector 4096 UTF-16 units; list depth 16 including the outer list; 64 branches per list; 256 total compounds; 64 compounds per chain; An+B magnitude up to Int32.MaxValue; 4,000,000 matching operations per query. Query/QueryAll propagate CancellationToken. HTML parsing uses the shared default bounds and rejects Partial/Failed with InvalidOperationException before exposing a snapshot. Unsupported HTML branches remain Partial.

Malformed selectors throw HtmlSelectorSyntaxException. Recognized unsupported features and limits throw HtmlSelectorUnsupportedException. Null/blank selectors retain standard argument exceptions. A valid zero-result selector stays distinct from every error. Scope is recorded in [selector-support.json](selector-support.json) and API changes in [Testing-api-diff.json](Testing-api-diff.json).

## Migration

| Previous read | v2 read |
| --- | --- |
| Document.QuerySelector(selector) | Snapshot.Query(selector) |
| Document.QuerySelectorAll(selector) | Snapshot.QueryAll(selector) |
| TextContent / GetAttribute(name) | Same element reads; namespace URI optional for attributes |
| Document.Title | Snapshot.Title |
| ParentElement / Children / PreviousElementSibling / NextElementSibling | Parent / Children / PreviousSibling / NextSibling |
| DOM source references | Nullable SourceStart/SourceLength/Line/Column |
| DOM mutation, third-party casts/extensions | Change source/rendering and Parse again, or explicit browser E2E |

DOM syntax exceptions become HtmlSelectorSyntaxException; unsupported features use HtmlSelectorUnsupportedException. First-match and tree-order behavior is retained for supported queries. Neither old source nor old binary DOM consumers can be migrated by a blind rename. User-owned dependencies are not removed.

For a previous SetAttribute/TextContent mutation, change the input or template explicitly:

```csharp
using LithoSharp.Testing;
using var original = SiteTestDocument.Parse("<p class=expected>Expected</p><a href='/good'>Good</a>");
original.AssertText("p", "Expected");
original.AssertAttribute("p", "class", "expected");
original.AssertLink("/good");
using var changed = SiteTestDocument.Parse("<p class=changed>Changed</p><a href='/bad'>Bad</a>");
// Previous text/class/link expectations now throw SiteTestException.
```

The [query conformance tests](../tests/LithoSharp.Tests/HtmlQueryTests.cs) execute this migration and cover selectors, escapes, namespaces, exact text, owner lifetime and query limits. [Document assertion tests](../tests/LithoSharp.Tests/SiteTestingDocumentTests.cs) and [site host tests](../tests/LithoSharp.Tests/SiteTestHostTests.cs) verify the retained helpers and generated-site assertions. Parse accepts document HTML with scripting disabled; it exposes no fragment or scripting-mode options.
