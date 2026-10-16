# LithoSharp project templates

The 2.0.0 line is an unreleased candidate. Install the exact reviewed `.nupkg`
from its candidate feed and create a site with `dotnet new lithosharp-docs`, `dotnet new lithosharp-blog`,
`dotnet new lithosharp-empty`, or `dotnet new lithosharp-mdx`.

Templates target .NET 10 and default to 2.0.0 packages. For a candidate feed,
set `LithoSharpVersion` to its exact candidate version. MDX additionally requires
Node.js 24.13.0 and explicit worker restore. See the
[Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md)
for build, serve and interactive component examples.
