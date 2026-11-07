# LithoSharp project templates

Start documentation managed with C# types and builds. The next candidate's
`docs` template connects a custom schema, generated binder/catalog and typed
references to Docs navigation and explicit output quality checks.

This checkout is not a published release. Configure the supplied candidate feed,
replace `<candidate-version>` with its exact version and set the generated
project's `LithoSharpVersion` to match before restoring. The published 2.1.3 Docs
template retains the previous reader-based starting point.

Install the candidate with `dotnet new install LithoSharp.ProjectTemplates::<candidate-version>`
and create a site with `dotnet new lithosharp-docs`, `dotnet new lithosharp-blog`,
`dotnet new lithosharp-empty`, or `dotnet new lithosharp-mdx`.

```sh
dotnet new lithosharp-docs -n MyDocs -o MyDocs
dotnet tool install LithoSharp.Tool --version <candidate-version> --tool-path .tools
# Before building, set MyDocs/MyDocs.csproj LithoSharpVersion to the same candidate
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

The CLI package is separate from the templates. `docs` starts a documentation
site, `blog` uses the Blog template, `empty` demonstrates a custom C# layout, and
`mdx` adds the optional MDX worker setup. Generated projects export an
`ISiteFactory` that the CLI compiles and runs as trusted site code.

Templates target .NET 10. For a candidate feed,
set `LithoSharpVersion` to its exact candidate version. MDX additionally requires
Node.js 24.13.0 and explicit worker restore. See the
[Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md)
for build, serve and interactive component examples.

For an end-to-end typed-content example with compiler diagnostics and generated
output checks, see the [Docs sample](https://github.com/Htkym/lithosharp/blob/main/samples/LithoSharp.DocsSample/README.md).

The Docs project includes `GuideFrontMatter.cs` (required title, optional description
and order), `Guides.Binder`, generated routes and two valid Markdown pages.
`AdditionalFiles` and runtime loading share the `content` root, nested relative
IDs with extensions, and underscore-prefixed exclusions. `RenderDocs` supplies
the shared Docs shell; the template alone owns `index.html`. Quality fails on
Error with external HTTP disabled, in both the CLI and generated Program.
Existing projects opt in through the [migration guide](https://github.com/Htkym/lithosharp/blob/main/docs/migration-2.0.md),
not by regenerating over their files.
