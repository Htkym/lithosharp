# Markdown profiles

Choose an exact Markdown profile in the generation options. Front matter binding,
publication rules and OKF conformance are separate settings.

```csharp
var options = new SiteGenerationOptions
{
    Markdown = new MarkdownOptions
    {
        ProfileId = MarkdownOptions.DocumentationProfile,
    },
};
```

`MarkdownOptions` is in `LithoSharp.Content`. The supported profile identifiers are:

| Profile | Behavior |
|---|---|
| `lithosharp-markdown/1` | Existing LithoSharp syntax and heading presentation. This remains the default. Footnote notation stays literal and produces the existing `LIT001` advisory. |
| `commonmark/0.31.2` | CommonMark interpretation, including HTML token boundaries. Documentation extensions such as GFM tables, math and structured directives are not enabled. Recognized HTML is encoded for display; it is never executed as raw HTML. |
| `syntamark-docs/2` | Documentation syntax with native footnotes and structured directives. Typed components additionally require explicit registrations. |

All three profiles receive the Markdown body after the content loader separates
the YAML header. A body beginning with `---` is body syntax, not another header.
The CommonMark parser's treatment of a standalone document does not change the
typed content loader's front matter requirements.

The documentation profile renders numbered footnotes in first-reference order.
Repeated references have individual backlinks. Generated IDs avoid collisions
with heading IDs. The first definition of a label is used; duplicates produce
`LMD030`. An unresolved reference remains literal and produces `LMD031`.
Unreferenced definitions and duplicate definition bodies are not displayed.
Code fences and inline code keep their literal notation.
When a footnote marker occurs inside a link label, its navigable marker appears
immediately after that link to keep the HTML free of nested anchors.

Definition lists, generic attributes and grid tables remain unsupported. Candidate
detection and optional compatibility advisories do not enable their rendering.

For direct editor inspection, pass the same options:

```csharp
var inspection = DocumentInspection.Inspect(path, unsavedText,
    new DocumentInspectionOptions { Markdown = options.Markdown });
```

An acquired project snapshot carries its selected profile to the language server.
Changing the profile invalidates the workspace's retained result and the site's
persistent Markdown cache. Registered component implementations execute only in
trusted generation; editor inspection uses static metadata.

To migrate an existing site, retain the default profile while changing unrelated
configuration. Then select the new profile and inspect the generated heading
text, anchors, tables, HTML literals and footnotes. Use the publication baseline
comparison to review lost URLs or anchors before publishing. A successful build
does not update that baseline.
