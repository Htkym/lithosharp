# LithoSharp project templates

Start documentation managed with C# types and builds. The 2.2.0
`docs` template connects a custom schema, generated binder/catalog and typed
references to Docs navigation and explicit output quality checks.

Install the published template package with `dotnet new install LithoSharp.ProjectTemplates::2.2.0`
and create a site with `dotnet new lithosharp-docs`, `dotnet new lithosharp-blog`,
`dotnet new lithosharp-empty`, or `dotnet new lithosharp-mdx`.

```sh
dotnet new lithosharp-docs -n MyDocs -o MyDocs
dotnet tool install LithoSharp.Tool --version 2.2.0 --tool-path .tools
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

The CLI package is separate from the templates. `docs` starts a documentation
site, `blog` uses the Blog template, `empty` demonstrates a custom C# layout, and
`mdx` adds the optional MDX worker setup. Generated projects export an
`ISiteFactory` that the CLI compiles and runs as trusted site code.

Templates target .NET 10 and use LithoSharp 2.2.0 by default. MDX additionally requires
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
