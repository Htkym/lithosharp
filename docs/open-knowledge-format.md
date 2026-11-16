# Open Knowledge Format

LithoSharp reads local Open Knowledge Format bundles using the pinned [OKF v0.2 specification](https://github.com/GoogleCloudPlatform/open-knowledge-format/blob/ad30107c31c06aec8a7d5636e0d1058118604e6f/SPEC.md). A bundle contains Markdown concepts, optional `index.md` and `log.md` files, and auxiliary files such as SQL, Python and images. Reading and exporting a bundle never execute those files, call a model or connect to a service.

Each concept needs a frontmatter mapping with a nonempty string `type`. Unknown types and nested extension metadata are retained. Missing optional fields, missing indexes and unresolved links do not make a concept structurally invalid.

## Inspect and validate

```sh
lithosharp okf inspect --input knowledge --format json --as-of 2026-11-13T00:00:00Z
lithosharp okf validate --input knowledge --strict-lint --format sarif
```

These commands read the bundle directly. They do not build a C# project or invoke a site factory. `--as-of` accepts a datetime with an explicit offset; omission captures the current time once per operation. `--max-files`, `--max-file-bytes` and `--max-bundle-bytes` limit captured inputs. File discovery currently visits the input tree before the captured-file count limit is applied.

Diagnostics classify structural errors, quality findings, unsupported syntax, resource limits, I/O, cancellation and publication policy separately. The result also distinguishes file-snapshot completeness, analysis completeness and conformance. An unsupported YAML feature or unknown declared specification version is not reported as validated conformance. A warning becomes a failure under explicit `--strict-lint`; structural and execution failures always fail. Cancellation returns exit status 130 and invalid command arguments return 2.

Root `index.md` may declare `okf_version: "0.2"` without `type`. Other index files contain no frontmatter. Log date headings use valid `YYYY-MM-DD` dates; log frontmatter is not prohibited. `README.md`, `AGENTS.md` and underscore-prefixed Markdown files remain concepts.

## Read metadata without changing it

```csharp
using LithoSharp.OpenKnowledge;

var bundle = await OkfBundle.ReadAsync("knowledge", new OkfReadOptions
{
    AsOf = DateTimeOffset.Parse("2026-11-13T00:00:00Z"),
    BaseUrl = "https://example.com/docs/",
});

foreach (var document in bundle.Documents)
{
    Console.WriteLine($"{document.Path}: {document.Metadata?.Type}");
}
```

`OkfDocument.Path` and `ConceptId` retain the original bundle-relative spelling. A concept ID omits the final `.md`; it is separate from its public route. Case and NFC collisions are diagnosed rather than resolved by renaming a file. `Catalog` uses the shared content catalog and routing validation.

`RawText`, `File.GetOriginalBytes()` and `Yaml` expose the original text, a defensive copy of the bytes, and the ordered native YAML tree. Known metadata properties are views over that tree. No numeric, Boolean or null value is coerced into a string `type`. `GetField` retains arbitrary nested values, scalar kinds and scalar styles. Unsupported syntax keeps its captured bytes while exposing unavailable views explicitly.

`Sources` accepts resource descriptions as well as paths and URLs. `UsageWindow`, `Generated`, `Runtime`, `Parameters`, `Computation`, `Executor` and `Attester` retain their source nodes. A legacy `timestamp` supplies only a display fallback when `generated` is absent. A legacy Citations section remains available as raw section text when `sources` is absent.

`verified` mappings and sequences share the `VerificationEvents` view:

| Records | Derived trust tier |
| --- | --- |
| No `verified` key | `Unverified` |
| Empty sequence | `NoRecords` |
| Invalid records only | `Indeterminate` |
| Valid records from non-`human:` actors | `MachineConfirmed` |
| A valid record from a `human:` actor | `HumanReviewed` |

Mixed valid and invalid events keep the derived tier and report `VerificationIsComplete = false`. Producer names are not an allowlist. These tiers summarize declared records; they do not establish an actor's identity or the truth of the content. The reader never adds a generator, verifier or timestamp.

`Freshness` distinguishes no deadline, fresh, stale and invalid values. A record becomes stale at `AsOf >= StaleAfter`, comparing instants across offsets. Date-only and offset-free values do not acquire an assumed time or timezone. Missing `status` reads as `stable`; unknown status values remain available with a quality diagnostic.

## Use the existing Docs pipeline

```csharp
using LithoSharp.Content;

var options = bundle.CreateSiteOptions(new ContentCollectionId("knowledge"));
```

Pass these options to the usual `SiteGenerator.GenerateWithOptionsAsync` call with a `DocsSiteTemplate`. The collection participates in existing Docs navigation, search, heading links and publication metadata. The default HTML output prefix is the collection ID, so the bundle's root index becomes `knowledge/index.html` without replacing the site's home page. Set `outputPrefix` explicitly when another prefix is needed. Raw OKF export keeps its original paths independently of this HTML projection.

Links come from the Markdown parser, including resolved reference links. Code spans, fenced code and escaped link-like text do not become relations. Bundle-root links are resolved against the bundle rather than the site's origin. HTML URLs use the site's base path. Original destinations remain in `Links`; unresolved concepts remain relations and render as nonnavigating spans carrying `data-okf-target`. Existing link checking remains enabled for ordinary site content.

Footnotes retain original labels, display numbers, repeated-reference occurrences and backlinks. `Citations` joins those labels to `Sources[].Id`, so reordering sources does not move attribution to a different source. Ordinary explanatory footnotes also render.

Draft, deprecated, stale and unverified concepts remain readable by default. To change publication, provide an explicit `PageMetadata` mapper and a stable policy fingerprint:

```csharp
using LithoSharp.Pages;

var policyOptions = bundle.CreateSiteOptions(new ContentCollectionId("knowledge"),
    document => new PageMetadata(
        title: document.Metadata?.Title ?? document.ConceptId ?? document.Path,
        draft: document.Metadata?.Status == "draft"),
    policyFingerprint: "hide-declared-drafts/1");
```

This mapping is a site policy. It is not an OKF conformance rule. Links to documents excluded by the chosen policy become nonnavigating in the generated OKF pages. Version and locale can be supplied through the mapper's ordinary `PageMetadata.Document` property. A custom mapper without a fingerprint disables collection caching.

`CreateAuxiliaryAssets(outputPrefix)` publishes captured auxiliary bytes through prepared assets. It does not run image transforms or reopen computation files for execution. `CreateSiteOptions` includes these assets automatically. Membership, parser policy, routes, relation sets, explicit time and captured input hashes participate in build inputs; unresolved targets do not become required file dependencies.

## Preserve an existing bundle

```sh
lithosharp okf export --input knowledge --output exported-knowledge --format json
```

```csharp
await bundle.ExportPreservedAsync("exported-knowledge");
```

Preservation writes a complete captured file set to a new directory outside the input bundle. Every document and auxiliary file keeps its original relative path and bytes, including a UTF-8 BOM, newline style, source IDs and unknown metadata. It does not replace an existing index or output directory. Links, reparse points, unsafe paths and normalized collisions are rejected. Publication occurs by moving a fully staged directory; a failed or canceled operation may leave its private staging directory for inspection.

Preserving bytes and validating syntax are separate results. A fully captured bundle with unsupported YAML can still be preserved; the CLI reports the export and retains its non-successful validation status. An incomplete file capture cannot be exported as a preserved bundle. This guarantee covers local captured files, not a round trip through an external service.

## Create new OKF explicitly

```csharp
var prepared = OkfWriter.Prepare(
[
    new OkfNewConcept("metrics/revenue.md", "type: Metric\ntitle: Revenue",
        "# Revenue\n\nThe declared value is 42.\n"),
]);
await prepared.ExportAsync("new-knowledge");
```

New output uses the provided metadata and Markdown. It does not infer `generated.at`, `status` or OKF sources from an ordinary article's Date, Draft or Sources properties. Timestamp values must have explicit offsets. No producer, verification event or build time is inserted.

For component Markdown or MDX, declare the source body kind and provide a portable Markdown alternative:

```csharp
var concept = new OkfNewConcept("metric.md", "type: Metric", originalMdx,
    OkfSourceBodyKind.Mdx, portableMarkdown: "# Metric\n\nValue: 42.\n");
```

A missing alternative fails conversion while retaining `OriginalMarkdown` in the result. Structured component directives in the selected output are rejected. An explicitly supplied alternative is a producer declaration; it does not guarantee identical behavior in every Markdown implementation. `OkfNewAuxiliary` adds inert byte snapshots. Output remains create-only, and failed conversion cannot export a partial result as portable OKF.
