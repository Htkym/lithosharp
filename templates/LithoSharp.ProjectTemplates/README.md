# LithoSharp project templates

Install version 2.1.3 with `dotnet new install LithoSharp.ProjectTemplates::2.1.3`
and create a site with `dotnet new lithosharp-docs`, `dotnet new lithosharp-blog`,
`dotnet new lithosharp-empty`, or `dotnet new lithosharp-mdx`.

```sh
dotnet new lithosharp-docs -n MyDocs -o MyDocs
dotnet tool install LithoSharp.Tool --version 2.1.3 --tool-path .tools
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

The CLI package is separate from the templates. `docs` starts a documentation
site, `blog` uses the Blog template, `empty` demonstrates a custom C# layout, and
`mdx` adds the optional MDX worker setup. Generated projects export an
`ISiteFactory` that the CLI compiles and runs as trusted site code.

Templates target .NET 10 and default to 2.1.3 packages. For a candidate feed,
set `LithoSharpVersion` to its exact candidate version. MDX additionally requires
Node.js 24.13.0 and explicit worker restore. See the
[Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md)
for build, serve and interactive component examples.

For an end-to-end typed-content example with compiler diagnostics and generated
output checks, see the [Docs sample](https://github.com/Htkym/lithosharp/blob/main/samples/LithoSharp.DocsSample/README.md).
