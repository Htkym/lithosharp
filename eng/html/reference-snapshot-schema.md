# Internal Quality page and CSS snapshots

The shared owner supplies Quality facts from its existing source/profile flight.
Declared Quality raw demand obtains raw material from that same flight and checks
active element/attribute correspondence. Decoded-only durable cache hits bind
snapshots without reconstructing raw data or parsing HTML. No second tree or shared
build foundation is introduced. Raw coverage is NotRequested on decoded-only hits.

Each consumer binds an immutable snapshot to the final output/public route,
registered PageId, and the existing DocumentKey (collection, version, locale, ID).
Artifacts without registered identities carry null PageId/Document. Equal HTML
shares parsing, while route/variant identity and base-dependent URL resolution
remain distinct. Bound snapshots are never put into the decoded durable cache.

Page snapshots additionally hold title, all active id/a[name] anchor occurrences
(including duplicates), metadata, canonical observations, element positions,
literal inline CSS references, and coverage per projection. Metadata and anchor
observations stay immutable; site membership, reachability and orphan decisions are
evaluated against all collected page and CSS snapshots. Quality no longer rescans
elements or CSS strings during graph/rule evaluation.

References preserve the existing Quality attribute visitation order and duplicates:
href, src, poster, object data, form action, accepted srcset candidates, og:image
content. Base and canonical observations are retained with distinct roles and stay
outside the ordinary LSQ001/002 reference loop. Base observations resolve against
the original page URL; other references use the effective base. Canonical, metadata,
orphan, external-link and declared asset-closure rules retain their existing IDs,
messages and positions. Foreign namespaced attributes retain the existing exclusion.

Each reference owns its element ordinal, attribute name, candidate ordinal, role,
navigation/resource flags, raw attribute value, HTML-decoded attribute value,
unresolved URL candidate, resolved URL (when URI construction succeeds), URL issue,
and original UTF-16 attribute-value span/mapping. For srcset, Raw/Decoded/Source
describe the containing attribute; UnresolvedValue identifies the candidate. An
exact candidate source range is not inferred from substring matching. A missing
og:image content produces the existing empty-resource diagnostic and has null raw,
decoded and source values. Unknown positions are never manufactured.

`Candidate` supplements that attribute container with the actual URL lexeme and
decoded-value mapping. Srcset candidates use the scanner's URL start/end offsets.
CSS candidates use its quoted/unquoted/import argument offsets and CSS escape
segments. These offsets compose with the HTML attribute or style text mapping;
external CSS composes with the file's UTF-16 identity map. Repeated equal URL values
remain distinct occurrences. No string search or attribute-container span is used
as a candidate position.

CSS snapshots reuse the existing scanner under `literal-css-urls/1`: accepted
quoted/unquoted/escaped url() references and top-level stylesheet @import strings.
Comment/string exclusion and EOF recovery remain the scanner's existing behavior.
Unresolved url() or top-level import operands yield Partial with explicit reasons.
Required Partial collection throws before site-wide diagnoses or atomic publish;
it is not used to prove assets unreachable. CSS variables outside literal URL/import
observations, cascade, conditions and full CSS evaluation are outside this profile.

External stylesheets bind references to their final artifact URL. Known candidate
raw pieces and original ranges are retained; a single span is supplied only for
contiguous known origins. HTML style-block origins require declared raw demand;
decoded-only cache records cannot recreate them. No full stylesheet is retained in
the resulting reference snapshot.

The source-only factory also captures title, all active h1-h6 material, style text,
and requested search body/index/sections from the same tree. Heading opening-tag
positions are explicitly distinguished from text ranges. Metadata name/property
values bind the already captured raw attributes. Search value/schema semantics are
unchanged: its material uses the same text traversal and sibling stop rules.

Mapped values have contiguous positive decoded partitions; original ranges may
overlap or be noncontiguous. Complete transformed segments or exact one-to-one
slices retain origins. Partial slices of a multi-value transformation become
Unknown with a reason. Synthetic HTML/section separators have null origins;
normalization composes real whitespace origins when known and explicitly marks
unmapped separators otherwise. No zero offset or guessed single range is supplied.

Value coverage and source coverage are separate per projection/request. Auxiliary
title/headings/search position Partial cannot fail valid Quality values. Declared
raw URL/CSS candidate positions remain required; missing requested mappings reject
safely. No raw request means NotRequested, including warm decoded-only hits.
Search source material is dropped at the Quality phase; previously acquired finite
immutable values remain usable. Parser/tree Partial and unresolved required CSS
collection still reject before publication.

Snapshots are internal read-only data on SiteQualityReport. They survive owner
release through immutable finite values; no source/tree/build cancellation object
is retained. Default no-raw consumers retain the decoded-cache path and rebind current
PageId/DocumentKey even on warm hits. Required Partial/Failed parsing, raw mapping or
literal CSS collection fails before the atomic publish boundary, preserving output.

Selectors, all projections, full dynamic CSS evaluation, public CLR contract changes
and performance certification are excluded.
