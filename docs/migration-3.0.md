# Upgrade to LithoSharp 3

LithoSharp 3 is being developed against the fixed Syntamark 2 runtime and Source
pair. The [version matrix](../eng/version-matrix.json) distinguishes the selected
candidate from the intended release versions. Candidate consumption and local
validation do not establish formal publication.

Keep the existing site and published output while testing the upgrade in a new
output directory. Compare public URLs and anchors against a baseline captured
from a successful publication. A build never promotes that baseline.

## Native YAML and content attributes

Remove direct YamlDotNet dependencies when the application no longer uses them
for other purposes. Replace `YamlMember(Alias = "name")` with
`ContentName("name")` and `YamlIgnore` with `ContentIgnore`, both in
`LithoSharp.Content`. Default member names use snake_case. Names are normalized
to NFC, and colliding normalized names are rejected.

The native YAML reader supports a documented subset rather than every YAML
feature accepted by YamlDotNet. Anchors, aliases, tags, merge keys, complex keys
and multiple documents are rejected. Syntax errors, unsupported features and
resource limits remain distinct diagnostics. A partial parse never certifies a
valid typed mapping. Runtime and compiler typed-content readers share a nesting
limit of 64; the independent native document API has its own options.

`MarkdownFrontMatterYaml.Deserialize<T>` now reads DTO mappings through the same
strict binder as content collections. It requires a supported DTO with a public
parameterless constructor. Scalar, sequence, dictionary and untyped-object roots
are outside this helper's scope. Use the native Syntamark YAML document API when
the application needs syntax nodes or permissive metadata views. The dedicated
`Deserialize(string)` post helper retains its legacy unknown-field behavior.

`MarkdownFrontMatterYaml.Serialize(PostFrontMatter)` writes deterministic new
metadata with quoted strings. It does not preserve the formatting or original
bytes of an existing YAML document. OKF preserved export and original-byte
retention are separate operations.

## Core image references and optional PNG processing

Core no longer installs SkiaSharp or native image libraries. Register existing
assets and describe their URLs, dimensions and responsive variants explicitly.
Core does not decode images to discover dimensions or generate missing formats.
Supply a fallback variant and dimensions wherever the descriptor requires them.
See [assets and images](assets-and-images.md).

`LithoSharp.Images` supplies optional PNG processing. Its initial codec accepts
8-bit, non-interlaced grayscale, RGB, grayscale-alpha and RGBA PNG inputs within
explicit resource limits. Palette, interlaced and other bit-depth inputs require
conversion before this pipeline. JPEG, WebP and AVIF variants can be registered
as existing files; this package does not encode those formats.

Replace implicit social-image generation with `CommonSocialImage`, a per-output
`SocialImages` entry, or an explicitly registered `SocialImageAsset`. The optional
PNG compositor combines declared pixels and logos. It does not provide the old
font-based text renderer or an external AVIF process fallback.

## Markdown profiles and typed components

The default profile remains `lithosharp-markdown/1`. Select `commonmark/0.31.2` or
`syntamark-docs/2` explicitly through `SiteGenerationOptions.Markdown`. Front matter
binding and document publication policy remain separate from this choice.
Changing the profile invalidates compatible parse and render caches; review the
resulting headings, anchors, extensions and HTML before publication.

The documentation profile enables structured component attributes and footnotes.
Register typed components explicitly and declare their configuration identity and
file, value or asset dependencies. Component attributes contain literal values;
they do not evaluate C# expressions. Reference rules require the current document
identity and adequate catalog evidence. Incomplete context cannot prove that a
reference exists or is absent.

## Static inputs and editor evidence

Use `MarkdownContentCollectionLoader<T>.FromCatalog` to opt into the input set and
routes emitted by a generated catalog with an explicit input root. The original
loader constructor remains available. Added, missing or moved static inputs
require rebuilding that catalog. Physical paths retain their spelling while
identity comparisons normalize Unicode and reject collisions.

Editor schemas are acquired through an explicit trusted inspection or build.
Typing does not execute user constructors, setters, validation delegates or
components. Static schema validation, catalog-dependent rules and checks requiring
a build have different evidence. Schema changes and input-membership changes
invalidate acquired evidence.

## Cached artifacts and verification records

Image-transform metadata and collection-diagnostic cache formats have changed.
An incompatible record is a cache miss. A missing diagnostic outcome is unknown,
not a successful empty validation.

Translation review records identify exact source and translation revisions. A
build does not advance them. Snippet verification executes only explicitly mapped
trusted projects and tests; changed inputs invalidate earlier success. A test
process exiting successfully without evidence of executed tests does not verify a
snippet. These results do not create OKF trust attestations or establish the
technical truth of a document.
