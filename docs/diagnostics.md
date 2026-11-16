# Diagnostic reference

[English](diagnostics.md) | [日本語](diagnostics.ja.md)

Start with the code and the stage that reported it. This index gives repair steps
for representative diagnostics; the linked references define their full contracts.
A clean editor buffer or successful build does not prove that every stage ran.
`NoContext`, `Partial`, `Unknown`, `Open` and stale results leave work unverified.

The extension links codes only in its own Problems collection. Generator and
Analyzer diagnostics remain owned by the compiler host; this index links their
references without duplicating or replacing those diagnostics. Unknown codes keep
their plain display. A diagnostic without a source location has no implied line.

## Front matter

`LSM001`–`LSM008` and `LSM014`–`LSM015` report front matter errors during Markdown content loading.
Editor inspection can report syntax facts available for the current buffer;
compiler inputs use the Generator's `LSG` codes instead.

| Code | Cause and edit target |
| --- | --- |
| `LSM001` | Required front matter is absent. Add a YAML header to the Markdown source. |
| `LSM002` | The front matter has no closing marker. Close the header in the Markdown source. |
| `LSM003` | The header is empty. Supply its required fields. |
| `LSM004` | YAML is malformed or violates the accepted document structure. Fix the reported YAML in the source header. |
| `LSM005` | A mapping repeats a key. Remove or rename the duplicate key. |
| `LSM006` | YAML uses an anchor or alias. Replace it with explicit values. |
| `LSM007` | A mapping key is not a non-null string. Correct the key. |
| `LSM008` | The YAML root is not a mapping. Use named fields at the header root. |
| `LSM014` | YAML uses a feature outside the native subset, such as an explicit tag or directive. Use supported syntax. |
| `LSM015` | Native YAML parsing reached a resource limit. The message identifies the limit; unavailable values are not bound. |

Fixing the header does not validate arbitrary C# schemas, dynamic routes or final
HTML. Save and run `lithosharp build <project>` for runtime loading, or use explicit
`lithosharp preflight --mode static --input <file>` for bounded source inspection.
See [front matter boundaries](typed-markdown-collections.md#front-matter-delimiters)
and [preflight stages and coverage](../src/LithoSharp.Tool/README.md#preflight).

## Front matter binding

`LSC101`–`LSC104` report schema-binding errors during content loading. Editor
inspection can check a supported built-in schema when current project context is
available; it does not execute custom constructors, setters or validators on typing.

| Code | Cause and edit target |
| --- | --- |
| `LSC101` | The schema does not recognize a field. Correct its YAML name or intentionally update the C# schema. |
| `LSC102` | A field that disallows null received null. Supply a value or deliberately change the schema's nullability. |
| `LSC103` | A value cannot bind to the declared type. Correct its YAML value or shape against the C# member. |
| `LSC104` | A required field is missing. Add it to the header using the schema's YAML name. |

Check aliases and snake_case names before editing the schema. Fix the original
content or C# model, not a generated binder. Run `lithosharp build <project>`;
`dotnet build <project>` checks only the explicitly registered compiler inputs.
Neither proves dynamic references or final HTML. See the
[binding rules](typed-markdown-collections.md#yaml-rules) and
[editor/runtime boundary](typed-markdown-collections.md#diagnostics-and-failure-behavior).

## LSM013

Markdown analysis could not establish the required facts, for example because of
invalid input or an exhausted parser resource budget. It is an Error during content
loading/body compilation and can also be reported by inspection. Read the message
for the actual failure; unavailable front matter boundaries are not proof of a
missing header. Edit the affected source or included code when it exceeds a limit.

Unavailable syntax facts, schema checks that depend on them, and final output remain
unverified. Re-run the same inspection or `lithosharp build <project>` after the fix.
Restarting the language server does not remove a source limit. See
[body analysis and budgets](typed-markdown-collections.md#code-inclusions-and-body-analysis)
and [known limitations](known-limitations.md).

## LSG005

The Source Generator found invalid YAML or an unknown field in a registered
`AdditionalFiles` input during compilation. Fix the source header's structure,
keys or schema field names. Do not edit generated output.

Run `dotnet build <project>` after editing. Only declared compiler inputs are
checked; dynamically loaded content, constructor-established defaults and final
HTML are not established by this result. See the
[Generator diagnostic contract](source-generators.md#diagnostics-and-dynamic-content).

## LSG006

The Source Generator found a value incompatible with the declared front matter
type, including a missing required value, disallowed null or wrong value shape.
It runs during compilation on registered `AdditionalFiles` inputs. Correct the
source YAML against the C# model, or intentionally change that model; do not patch
the generated binder or reference.

Run `dotnet build <project>`. Compiler success does not validate dynamically
discovered inputs, arbitrary runtime defaults/validators, dynamic routes or final HTML.
Continue with `lithosharp build <project>` and output checking as needed. See the
[Generator diagnostic contract](source-generators.md#diagnostics-and-dynamic-content).

## LSA1402

The Analyzer proved that a typed `ContentRef<T>` pairs a known entry ID with a
different public route in the same fresh, Closed generated catalog. During
compilation it reports the original route argument, with the ID as a related
location. Prefer the generated reference, or correct the handwritten ID/route pair
and its source metadata if that declaration was wrong.

Run `dotnet build <project>`. Unknown IDs, dynamic routes, Open/stale catalogs and
unsupported reference forms are outside this contradiction proof; absence of this
diagnostic does not certify them or final output. See the
[Analyzer's exact proof conditions](../src/LithoSharp.Analyzers/README.md#lsa1402).

## LSA9001

An explicitly enabled `RequireStaticCoverage` compiler policy could not prove a
requested scope. Read its scope, state and reason. An Open, Stale, Unavailable,
Unknown or Deferred result is not evidence of a broken link or a dynamic producer.

Review the requesting C# call/producer selection and its registered compiler
inputs. Refresh stale inputs and use a supported static form where practical.
Run `dotnet build <project>` again. Runtime `lithosharp build <project>` and output
checking cover different stages; they do not turn the static result into Complete.
The policy does not certify whole-site publication, anchors or assets. See the
[opt-in policy and supported scopes](../src/LithoSharp.Analyzers/README.md#lsa9001).

## LSQ001

Generated-output checking found an invalid, unsupported or required-but-empty URL,
or an internal target that does not exist. This runs during enabled generation
Quality checks or explicit output inspection. Edit the content link, layout,
asset registration or route declaration that produced the URL, then regenerate.
If an output file was changed after generation, inspect that change too.

Use `lithosharp check <project>` to generate and check a temporary site, or
`lithosharp check-output --output <directory> --base-url <url>` to inspect existing
bytes. An ordinary Build result does not establish that Quality ran. Existing
output checks do not prove source freshness, and external HTTP checks require
explicit opt-in. See [Quality rules](site-quality.md) and
[output-checking boundaries](generated-output-checks.md).

## LSQ002

Generated-output checking found a fragment whose anchor is absent from the target
page. It runs at the same stage as `LSQ001`. Correct the source link fragment or
the heading/HTML ID emitted by the target layout, and regenerate affected pages.
Do not assume a valid C# reference proves the final HTML anchor exists.

Use `lithosharp check <project>`, or
`lithosharp check-output --output <directory> --base-url <url>` for existing files.
The result applies to the inspected HTML,
not unbuilt source changes, dynamic browser content or unchecked external pages.
See [Quality rules](site-quality.md) and
[output-checking boundaries](generated-output-checks.md).
