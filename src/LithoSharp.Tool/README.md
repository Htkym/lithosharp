# LithoSharp.Tool

.NET 10 CLI for C# static sites. Requires the .NET SDK and ASP.NET Core shared framework.

```sh
dotnet tool install LithoSharp.Tool --version 0.3.1 --tool-path .tools
dotnet new install LithoSharp.ProjectTemplates::0.3.1
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

On Windows use `.tools/lithosharp.exe`. MDX is optional and requires Node.js
24.13.0 plus explicit worker restore; see the
[Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md).

Use `check --format text|json|sarif` for quality validation, `inspect --format json`
for build graph and cache information, and `clean` to remove unchanged owned files.
`build --clean` performs a full output replacement. Sites export one public
parameterless `ISiteFactory`; ordinary library callers can use the same definition.

See the [CLI guide](https://github.com/Htkym/lithosharp/blob/main/docs/cli.md) for
factory examples, output behavior, development server limitations and deployment support.
