---
title: API documentation
supported_version: "3.0"
prerequisites: [quickstart]
---

The reference uses the four runtime libraries from the selected NuGet packages.
Signatures, exported types and members, visibility, parameters, constraints and
inheritance come from their actual assembly metadata. Descriptions come from the
matching compiler XML. Private helpers and members of non-exported types are
excluded; public and protected members of exported types are included.

| Library | English | Japanese |
|---|---|---|
| Core | [LithoSharp](../../api/en/lithosharp/) | [LithoSharp](../../api/ja/lithosharp/) |
| MDX and documentation | [LithoSharp.Mdx](../../api/en/lithosharp.mdx/) | [LithoSharp.Mdx](../../api/ja/lithosharp.mdx/) |
| Optional PNG processing | [LithoSharp.Images](../../api/en/lithosharp.images/) | [LithoSharp.Images](../../api/ja/lithosharp.images/) |
| Testing | [LithoSharp.Testing](../../api/en/lithosharp.testing/) | [LithoSharp.Testing](../../api/ja/lithosharp.testing/) |
| Shared executable example | [Example](../../api/en/) | [Example](../../api/ja/) |

Both locales keep the same exact API IDs and parameter names. Japanese translates
every selected XML explanation, including summaries, remarks, parameter and return
descriptions. Inline references and code keep their identity. An untranslated new
description fails the build instead of copying English into the Japanese view.
When the package has no declaration-specific XML comment, both views explicitly
say so and display the available structure. Such a card does not invent behavior
or replace the relevant feature guide.

`GenerateDocumentationFile` also writes the XML for
`WebsiteExamples.HasPublishedVersion`, the method included in the typed example.
The site passes compiler XML and its explicit Japanese view to
`ApiReferenceSite.AddXml`, together with `ApiAssemblySource` and `ApiSymbol`.
The same method is compiled by the mapped tests described in [verification](../operations/).
The [implementation](https://github.com/Htkym/lithosharp/blob/main/site/PublicApiDocumentation.cs)
and [localization validator](https://github.com/Htkym/lithosharp/blob/main/site/validate-api-localization.mjs)
show the reproducible metadata and translation checks.

XML IDs retain overload and generic identity. Explicit reference maps connect
cross-assembly targets. Referenced external declarations have separate reference
cards; these are not a full reference for every dependency. Documentation-only XML
cannot establish a signature or visibility. OpenAPI uses explicitly supplied JSON;
external references are diagnosed instead of downloaded. Tool, generator, analyzer,
template and editor entry points are documented in the
[detailed guides](../../reference/en/README/) and their package guides rather than
being mistaken for these four runtime assembly APIs.

`ApiReferenceSite.Compare` separates documentation, signature, visibility,
deprecation and addition/removal changes. A category alone does not prove a
breaking change. Choose snapshots explicitly and retain their inputs. See
[operations](../operations/) for evidence and publication boundaries.

Use `DocumentReferenceSnapshot.FindApiImpacts` to list captured guide links and
mapped snippets for explicitly selected API changes and exact variant keys.
Declare each snippet's API keys with `DocumentSnippet.ApiReferences`. Retain a
reference snapshot from before an API removal so its guide links still resolve.
The [example checks](https://github.com/Htkym/lithosharp/blob/main/site/checks/ExampleTests.cs)
preview removal of the real `HasPublishedVersion` API and find both its guide link
and mapped snippet in each language. See [code example verification](../../reference/en/code-example-verification/)
for the query and its manual scope. It does not extract API uses from C# or prove
that every affected document has been found.
