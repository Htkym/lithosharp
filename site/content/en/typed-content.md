---
title: Typed content and references
supported_version: "3.0"
prerequisites: [quickstart]
---

`WebsiteModels.cs` declares `ExampleFrontMatter` and a static collection with
`InputRoot = "examples"`. The project supplies its Markdown inputs as
`AdditionalFiles`, with one entry ID and one route per file. The generator emits
the binder, schema, catalog and typed references. `FromCatalog` checks runtime
membership against those declarations before loading the content.

The [running English example](../../examples/en/typed-content/) uses a required title,
an enum and `ContentRequiredWhen("status", "Published")` on `SupportedVersion`.
Member aliases use `ContentName`; ignored members use `ContentIgnore`.
The reader rejects duplicate or colliding names, unsupported YAML constructs and
invalid typed values. A partial parse does not prove a valid mapping.

Ordinary handbook pages use `DocumentSemanticRuleSet.Documentation`: published
pages require `supported_version`; `prerequisites` refer to documents in the same
collection, version and locale. The `quickstart` prerequisite on this page is an
actual reference. Missing or unpublished targets are diagnosed only with adequate
evidence. Unknown publication or incomplete coverage can return `Deferred`.
OKF metadata follows its separate policy.

Do not instantiate content DTOs to produce editor completions. Explicit trusted
inspection exports static schema descriptors; typing consumes those descriptors.
Changing C# schema definitions or input membership invalidates the relevant evidence.
The complete schema and catalog contract is in the repository's
[generator guide](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Generators/README.md).
