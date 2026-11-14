---
title: Markdown profiles and components
supported_version: "3.0"
prerequisites: [typed-content]
---

Choose a profile explicitly through `SiteGenerationOptions.Markdown.ProfileId`.
`lithosharp-markdown/1` remains the compatibility default, `commonmark/0.31.2`
selects CommonMark, and `syntamark-docs/2` enables documentation extensions.
Front matter binding and publication rules remain independent of the profile.
Changing profiles invalidates compatible caches; compare generated anchors and HTML.
The [Syntamark contract guide](../../reference/en/syntamark-contracts/) explains
the fixed runtime/Source pair, UTF-16 positions, selected availability, incremental
sessions, native YAML, footnotes and proofreading boundaries.

This page renders a registered C# component with typed literal props:

:::component name="version-badge" version="3.0"
:::

`WebsiteFactory` selects the documentation profile and registers `VersionBadge`
with `GeneratedBadges.Binder` and a configuration fingerprint. `VersionBadgeProps`
is the single C# schema for binding and static editor attributes. This component
encodes its value before creating HTML. Attributes do not execute C# expressions;
ordinary typing does not execute the renderer.

Declare every file, value and asset a component reads. Reference props additionally
need explicit semantic rules, the current `DocumentKey` and adequate catalog
evidence. An unavailable context cannot establish that a target exists or is absent.
Unknown props and unsupported syntax remain diagnostics rather than arbitrary HTML.

The documentation profile also parses footnotes.[^profile]

[^profile]: This is an actual footnote emitted from the selected profile.
