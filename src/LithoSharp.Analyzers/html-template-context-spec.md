# HTML interpolation context contract

The only registered template sink is the public metadata-defined
`LithoSharp.Html.UnsafeRaw(string)` returning `LithoSharp.IHtmlContent` from
assembly `LithoSharp`. There is no interpolated string handler API in this runtime.
Direct C# interpolated string arguments (including parentheses) are analyzed as
separate decoded literal tokens and original typed holes. Ordinary string variables
are not assumed to be HTML. Runtime APIs and rendering are unchanged.

| Rule | Severity | Proven condition | Location |
| --- | --- | --- | --- |
| LSA1101 | Info | The existing bounded static flow cannot determine all raw argument contents | Original argument expression |
| LSA1102 | Error | Exact UnsafeRaw result, directly or through IHtmlContent.ToHtmlString(), in a known quoted attribute | Original hole expression |
| LSA1103 | Error | Hole in a known tag name, attribute name or unquoted attribute value | Original hole expression |
| LSA1104 | Warning | Exact Html.Encode, sealed HtmlText or HtmlAttributeValue output in known script/style text or quoted on* / style attribute | Original hole expression |

LSA1101 describes unverified output; it does not assert a vulnerability. An
IHtmlContent interface variable alone does not prove a fragment: it might contain
one of the encoded wrappers. HtmlText and HtmlAttributeValue encode using the
runtime's Html.Encode and override ToString; their quoted attribute / element text
use does not produce LSA1102. Html.Encode does not prove JavaScript or CSS safety.
RawHtmlContent does not override ToString: direct interpolation currently emits
its type name, while ToHtmlString explicitly renders its raw contents. LSA1102
identifies the proven fragment/context type mismatch in both forms; it does not
claim that direct interpolation emits the raw fragment or establishes an exploit.
No getters, constructors, user methods, ToString or ToHtmlString are executed.

The cursor consumes literal characters and holes separately; holes are never
replaced by empty strings, sentinel tags or fabricated text for a document parser.
Unknown raw holes invalidate all subsequent contexts. Only compile-time string
hole constants have contents that can advance the cursor. Exact encoded outputs
preserve element text, quotes and script/style raw-text HTML structure, but do not
certify name, unquoted value or comment contexts. A forbidden hole itself has a
known position even when its contents are Unknown; it receives LSA1103 and makes
the following context Unknown.
Encoded holes inside a partially matched raw-text closing-tag prefix (including
`<` or the complete tag name before its delimiter) also invalidate the following
context: their contents can either break or complete that tag. Constant string
holes are consumed literally and retain a known context when their exact contents
establish it. Encoded holes with no pending raw-text prefix preserve that context.
Malformed prefixes, declarations, foreign SVG/MathML,
RCDATA/other special modes, and script escaped/double-escaped states are unsupported
and invalidate later context diagnostics. Comment holes also invalidate context.
Alignment and format clauses are unsupported for typed output classification.

The direct-template budget is 65,536 source and total decoded UTF-16 units (including
constant string holes), 256 holes and 128 characters
per tag/attribute name, in addition
to the static flow's existing graph/operation/depth budgets. Excess work is Deferred
without an added definite context Error. Concatenated/flow-derived templates, finite
sets of template prefixes, arbitrary formatting and arbitrary interface dispatch
are Deferred for context rules; static raw-content Info may still apply. Literal
HTML URL validation (LSA1105), CodeFix and complete HTML conformance are separate
tasks. Raw-text fake tags do not switch the cursor into an attribute context.

Locations are the Roslyn original expression spans, not offsets reconstructed from
decoded HTML. Normal, verbatim, raw/multiline raw and escaped tokens therefore keep
their source coordinates, including surrogate pairs and indentation. Rules respect
generated-code exclusion, cancellation and ordinary diagnostic suppression. No
cross-compilation state or user-code execution is used.
