# HTML interpolation context contract

Inline CSS URL projection uses the same private source/tree parse and runtime CssUrlScanner source. Surviving style attributes use decoded HTML attribute text; style elements use the active tree's text chunks (HTML raw text and foreign entity decoding retain their parser semantics). Template content and removed duplicate attributes are not reconstructed. Stylesheets include the existing top-level import strings; attributes do not.

LSA1105 requires Complete HTML and Complete CSS for the relevant input, a base-independent LSQ001 resource violation and the existing all-value/all-origin common proof. The candidate's raw CSS token range maps through HTML segments to one original C# piece. A zero-length URL requires touching segments to agree on one raw boundary and that point must not be an ambiguous piece boundary. CSS Partial or sink work/mapping exhaustion defers CSS without removing independent href/src/srcset diagnostics. The CSS projection shares a 262144-step budget across sink origin alternatives, independent of the existing HTML candidate mapping budget; it never publishes a truncated CSS prefix. No new user-code evaluation or external stylesheet resolution occurs.

The only registered template sink is the public metadata-defined
`LithoSharp.Html.UnsafeRaw(string)` returning `LithoSharp.IHtmlContent` from
assembly `LithoSharp`. There is no interpolated string handler API in this runtime.
Direct and flow-composed C# interpolated strings are analyzed as separate decoded
literal tokens and original typed holes when they reach this sink. Local/capture
assignments, aliases, built-in string addition and finite branches use optional
immutable shape proofs on the existing CFG evaluation snapshots. Runtime APIs and
rendering are unchanged.

| Rule | Severity | Proven condition | Location |
| --- | --- | --- | --- |
| LSA1101 | Info | The existing bounded static flow cannot determine all raw argument contents | Original argument expression |
| LSA1102 | Error | Explicit IHtmlContent.ToHtmlString() output of an exact UnsafeRaw result in a known quoted attribute | Original hole expression |
| LSA1103 | Error | Hole in a known tag name, attribute name or unquoted attribute value | Original hole expression |
| LSA1104 | Warning | Exact Html.Encode, sealed HtmlText or HtmlAttributeValue output in known script/style text or quoted on* / style attribute | Original hole expression |

LSA1101 describes unverified output; it does not assert a vulnerability. An
IHtmlContent interface variable alone does not prove a fragment: it might contain
one of the encoded wrappers. HtmlText and HtmlAttributeValue encode using the
runtime's Html.Encode and override ToString; their quoted attribute / element text
use does not produce LSA1102. Html.Encode does not prove JavaScript or CSS safety.
RawHtmlContent does not override ToString: direct interpolation currently emits
its type name, while ToHtmlString explicitly renders its raw contents. Only the
explicit rendered string proves a fragment for LSA1102. This corrects the former
direct-object classification; it does not establish an exploit.
No getters, constructors, user methods, ToString or ToHtmlString are executed.

The cursor consumes literal characters and holes separately; holes are never
replaced by empty strings, sentinel tags or fabricated text for a document parser.
Unknown raw holes invalidate all subsequent contexts. Known string hole values
from the same evaluation snapshot can advance the cursor. Exact encoded outputs
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
Alignment and format clauses defer that hole's definite context diagnostic and
invalidate subsequent context. The previous direct path reported LSA1103 for
`Html.UnsafeRaw($"<{x:tag}>")`, `Html.UnsafeRaw($"<p {x,10}='v'>")` and
`Html.UnsafeRaw($"<p a={x:value}>")` despite unsupported formatting. These now keep
the scalar LSA1101 Info without LSA1103, consistently with shared templates: no
formatter or alignment output is modeled as a proven typed hole.

The direct-template budget is 65,536 source and total decoded UTF-16 units (including
constant string holes), 256 holes and 128 characters
per tag/attribute name, in addition
to the static flow's existing graph/operation/depth budgets. Excess work is Deferred
without publishing a successful prefix. Shape limits are independently eight
alternatives and 513 pieces per alternative, with adjacent literals merged. They
do not reuse the URL proof's 64-piece limit. All alternatives must prove the same
original hole, rule and context at each sink. Compilation aggregation unions
independently proven sinks, with at most 16 deterministic related use locations.
Unknown scalar contents remain Unknown even if the shape is known. A missing
shape alternative is never discarded. Arbitrary formatting and interface dispatch
remain Deferred; static raw-content Info still applies independently.
LSA1105 now uses the separate direct-literal contract below. CodeFix and complete
HTML conformance remain separate. Raw-text fake tags do not switch the cursor into an attribute context.

Locations are the Roslyn original expression spans, not offsets reconstructed from
decoded HTML. Normal, verbatim, raw/multiline raw and escaped tokens therefore keep
their source coordinates, including surrogate pairs and indentation. Rules respect
generated-code exclusion, cancellation and ordinary diagnostic suppression. No
cross-compilation state or user-code execution is used.

## Direct literal URL contract (RA-04B / LSA1105)

The same UnsafeRaw(string) metadata sink accepts direct normal, verbatim and raw
string literals and bounded built-in string additions composed only of those
literal leaves. Parentheses and implicit identity conversions are permitted.
Normal binding, string operand types, Add with no OperatorMethod, literal leaves
and equality of joined decoded values with compiler ConstantValue are all required.
ConstantValue alone never admits arbitrary expressions. Nonliteral leaves,
const/flow variables, calls/getters, null/numeric operands, explicit casts,
user operators/conversions and interpolations (including constant holes) remain
Deferred. Extraction limits are 64 literal leaves and 64 operation/syntax levels,
with the existing decoded and total expression source-size limits below.

Each C# token is decoded separately before its ValueText is joined. Token.Text
concatenation or reinterpretation of escapes across tokens is forbidden. The joined
HTML is parsed once. An original HTML URL/entity range must fit wholly in one leaf
to use that leaf's CSharpLiteralMap; cross-leaf origins and empty origins at a leaf
boundary are candidate-local Deferred, never an expression-wide/attribute-wide
span substitute. Cancellation propagates from extraction and parsing; interrupted
analysis is not promoted to Complete.

## Shared static HTML inputs

Source string const declarations, known local/capture assignments and aliases,
finite branch composition and string-only interpolation without alignment/format
use the existing scalar CFG and evaluation-time snapshots. Optional immutable
original pieces accompany string values. Compiler ConstantValue's scalar early
return, Known/Unknown, value Distinct and evaluation order remain unchanged.
Origin failure never turns an existing scalar Known value into Unknown. Source
const lookup reads current Compilation declarations only, with bounded cycle/depth
checks; metadata-only constants retain unmapped proof pieces. No initializer,
getter, call, operator, formatter, I/O or Core runtime is executed.

Normal/verbatim/raw text is mapped only when decoded characters match compiler
values exactly. String holes carry their evaluated pieces. A URL/entity source
range must fit wholly into a mapped piece; unknown/cross-piece/ambiguous empty
origins stay Deferred. Equal values retain all different origin alternatives;
missing origins and alternative overflow cannot be dropped to establish proof.
Deconstruction invalidates scalar values and all optional proofs. Additional ref
escape clears all optional proofs even where scalar facts remain Known. Existing
call/getter/ref/mutation/loop/exception invalidation also drops template shapes and
output-kind witnesses. Cancellation propagates through extraction and parsing.

Each sink proves the intersection of identical source tree/span, decoded URL,
attribute role and LSQ001 issue across all values and origin alternatives. Every
distinct value must parse Complete. A mixed/Unknown branch or different invalid
origins does not prove a common Error. Compilation aggregation unions independently
proven sinks, rather than intersecting separate uses. It emits one original primary
URL location with at most 16 deterministically ordered related use locations.
Normal suppression and pragma apply at the primary source definition.

Origin limits are 64 pieces per alternative, 64 alternatives, eight distinct parsed
values and 65,536 total decoded units per sink; existing CFG/depth/source/tree and
262,144 mapping limits remain. This contract proves local URL misuse and never
certifies whole-HTML/whole-site coverage or absence of Error in later execution.

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
because their site policy differs. Namespaced xlink, external CSS/base resolution, formaction,
site membership/anchors and canonical-route correctness remain outside this scope.
Inert template content and script fake tags follow the actual bridge tree, not a
substring scan. The rule does not certify runtime URL validity or security.

Unnamespaced srcset candidate URLs use an explicit private inclusion of the same
runtime SrcsetUrlScanner source. Descriptor rejection, data URL commas, decoded
candidate ranges and cancellation retain that scanner's contract. Empty lists and
discarded candidates add no URL Error; image selection, sizes and author
conformance are outside this scope. Each accepted candidate is a resource URL for
the same base-independent LSQ001 checks.

Candidate ranges are in the decoded attribute. They map through that same
HtmlText.Segments to a contiguous original HTML range, then CSharpLiteralMap maps
the range to original C# UTF-16. Equal-length segments can be sliced; transformed
segments must be consumed whole. Missing ranges, gaps, unsafe cuts or exhausted
mapping budget leave the candidate Deferred, without an attribute-wide fallback.
One forward segment cursor per attribute and a shared limit of 262,144 mapping
operations per literal bound this additional phase. Partial/Failed trees and
composite/dynamic input remain outside definite Error conditions.
