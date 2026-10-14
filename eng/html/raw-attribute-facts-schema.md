# Internal raw attribute material

`HtmlRawAttributeFacts` is an internal material snapshot. It captures the
repaired active tree and its surviving attributes, including adjusted element
and attribute namespaces. `Ordinal` is active-tree traversal order; it does not
restore original token order, inactive template content, or removed duplicate
attributes. This slice adds no public CLR API or cache schema.

Raw names/values retain original UTF-16 spans, decoded strings and frozen
mapping segments. `HtmlTextBuilder.Append` partitions the decoded string into
positive-length contiguous segments. Source segments may have gaps or overlap and need
not have the same length as decoded segments (CRLF and character references).
`Build` bounds `Source` by the first and last segment. An empty or boolean value
has an empty decoded string, zero-length `Source` and no segments.

Capture checks those structural mapping rules, including attribute-Source
containment, without parsing or decoding again. Nonempty values with missing
or incomplete mappings, or out-of-attribute segments, cause `RawAttributes`
coverage to be `Partial` with a reason. Valid surviving attributes remain
available; an empty successful result is distinct from `Partial`/`Failed`.
Other projections are `NotRequested` by this producer, not substitutes for the
existing owner's Quality/search results. Collections and mapping are copied
before retention; the full source string and tree are not retained.

`HtmlFacts.ParseWithRawAttributes` accepts only source/options/token, calls the
existing Parse once and passes its exact source/tree to a private capture method.
No raw capture entry accepts a caller-supplied tree. Existing Parse still throws
for Partial/Failed trees, so those paths do not return fabricated empty facts.

The owner declares immutable Search/Quality raw demand at construction, before
any flight. It replaces its Parse call with the factory when raw is required,
and builds search/Quality from the returned Facts. Same source/mode/context
consumers share the flight. Required mapping coverage must be Complete.
Search-only raw is dropped at BeginQuality; Quality raw is retained until
Release. Acquired immutable material is not mutated by phase release. No tree
enters the retained Task result. Undeclared or late raw requests fail without
reparsing. Consumer cancellation does not cancel another consumer's parse.

Durable cache schema remains decoded-only. Required Quality raw bypasses that
hit and uses the owner; raw/span is never reconstructed from the stored DTO.
Default raw demand is None, preserving the existing warm/no-op path. An internal
generation option selects the consumer plan; no public CLR API is added.
Required incomplete tree/material errors propagate before atomic publication.
Memory/cost parity, full snapshot binding and all LSQ parity are not certified.

Mapping basis: `src/LithoSharp/HtmlParsing/HtmlTextBuilder.cs` (`Append`, `Build`),
`HtmlTokenizer.cs` (`Read`, `AppendRead`, `ReadReference`, attribute state machine),
and `CompactHtmlTreeBuilder.Foreign.cs` (`AdjustAttributes`).
