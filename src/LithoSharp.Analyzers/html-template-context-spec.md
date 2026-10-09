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
are Deferred for interpolation context rules; static raw-content Info may still apply.
LSA1105 now uses the separate direct-literal contract below. CodeFix and complete
HTML conformance remain separate. Raw-text fake tags do not switch the cursor into an attribute context.

Locations are the Roslyn original expression spans, not offsets reconstructed from
decoded HTML. Normal, verbatim, raw/multiline raw and escaped tokens therefore keep
their source coordinates, including surrogate pairs and indentation. Rules respect
generated-code exclusion, cancellation and ordinary diagnostic suppression. No
cross-compilation state or user-code execution is used.

## Direct literal URL contract (RA-04B / LSA1105)

The same UnsafeRaw(string) metadata sink accepts direct normal, verbatim and raw
string literals for URL analysis. Parentheses are permitted. This first scope does
not infer HTML from arbitrary strings, const/flow variables, concatenations or
interpolations, including constant holes. Those composite inputs remain Deferred
for LSA1105, even if another bounded static-value rule knows their resulting text.

The existing host-safe HtmlLiteralFacts bridge supplies the actual repaired tree's
URL attributes, decoded entity values, raw source ranges and rel metadata. Parsing
must finish Complete with no incomplete-coverage diagnostic. Partial/Failed trees
produce no LSA1105, including bad URLs seen before the incomplete suffix. Compiler
limits are 65,536 decoded units, 655,360 source units, 8,192 nodes, depth 128 and
262,144 tree operations. Mapping must match Roslyn ValueText exactly; normal escapes,
verbatim quote doubling and raw indentation/CRLF are mapped to original UTF-16 spans.
An empty URL has a zero-length location at its original value boundary.

LSA1105 is Error only for a base-independent LSQ001 issue: empty/whitespace resource
URLs, explicit-scheme malformed absolute references, unsupported absolute schemes
or HTTP(S) userinfo. The pure checks are shared with runtime SiteQualityValidator.
Runtime trimming and allowance of mailto/tel/data are retained. Relative, rooted
and protocol-relative references require the actual base/site graph and remain
Deferred. There is no invented origin/base used to prove a compiler Error.

The projection covers href (resource except a/area), src, poster, object data and
form action, with unnamespaced attributes. It excludes base and every element whose
rel tokens contain canonical, matching the runtime's policy,
because their site policy differs. Namespaced xlink, srcset/CSS splitting, formaction,
site membership/anchors and canonical-route correctness remain outside this scope.
Inert template content and script fake tags follow the actual bridge tree, not a
substring scan. The rule does not certify runtime URL validity or security.
