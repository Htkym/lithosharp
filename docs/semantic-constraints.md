# Document semantic constraints

Ordinary documentation can declare constraints beyond individual CLR types.
The same rule evaluator accepts captured build metadata and editor metadata;
it never executes a component, project, or custom validator in the editor.
OKF bundles keep their separate conformance and publication policies.

For a custom content model, declare a conditional input requirement:

```csharp
public sealed class GuideFrontMatter
{
    public string? Status { get; set; }

    [ContentRequiredWhen("status", "stable")]
    public string? SupportedVersion { get; set; }
}
```

The condition uses the YAML name and compares decoded scalar text ordinally.
Several attributes are alternatives: any matching condition requires the field.
The dependent field must be present, non-null, and nonempty; whitespace-only
strings and empty sequences or mappings fail. A C# initializer does not supply
missing input. Plain YAML null does not match the literal `"null"`; a quoted
string containing `null` does. The generated compiler check, generated binder,
reflection binder, and generated editor schema share these rules. Publication
decisions and custom validation still require a build.

The documentation extension has an explicit opt-in for the standard constraints:

```csharp
docs.AddCollection(new DocumentationCollection("guide", variants)
{
    SemanticRules = DocumentSemanticRuleSet.Documentation
});
```

Published documents then require `supported_version`, and `prerequisites` contains
document IDs in the same collection, version, and locale:

```yaml
title: Deployment
supported_version: '3.0'
prerequisites: [installation, configuration]
```

The real build publication policy determines whether a document is published.
Published documents cannot refer to captured unpublished prerequisites. Unlisted
documents are published targets. Custom rules can allow cross-version or
cross-language references explicitly; use a `DocumentKey` or a mapping containing
`collection`, `version`, `locale`, and `id` for an explicit target identity.
Routes always come from the existing `DocumentCatalog`.

`DocumentSemanticRuleSet.Evaluate` returns `Passed`, `Failed`, `Deferred`, or
`NotApplicable` for each rule. An unavailable catalog or publication decision is
`Deferred`, never a validated success. A missing document is an error only when
the snapshot explicitly covers the complete reference universe. Known targets
can still establish existence in a partial snapshot. `SemanticResults` and
`SemanticSnapshot` expose the latest documentation capture; a failed recapture
invalidates previous successful assessments.

Diagnostics use `LSC105` for a declared conditional requirement, `LSDOC210` for a
runtime required field, `LSDOC211` for invalid or missing references, `LSDOC212`
for a published-to-unpublished reference, `LSDOC213` for a version or locale
mismatch, and informational `LSDOC214` for deferred validation. Cause locations
come from the original parsed input, with related locations for known targets.
