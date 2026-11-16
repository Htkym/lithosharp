---
title: Quick start
supported_version: "3.1"
prerequisites: [introduction]
sidebar_position: 2
---

Create a documentation site, edit a page and preview it locally.

## Prerequisites

Install the .NET 10 SDK, version 10.0.300 or later. Node.js and React are not
required for this Markdown-only example.

Run the commands below from a new working directory. Do not create the project
over an existing site.

## Install and create a site

```sh
dotnet new install LithoSharp.ProjectTemplates::3.1.0
dotnet tool install LithoSharp.Tool --version 3.1.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
```

On Windows, use `.tools/lithosharp.exe` in place of `.tools/lithosharp`.
The template is installed in your user template catalog; the CLI is installed
locally in `.tools`. Keep LithoSharp packages, templates and the CLI at the same version.

## Edit your first page

Open `MyDocs/content/getting-started.md` and replace its content:

```markdown
---
title: Getting started
description: My first documentation page.
order: 1
---

# Getting started

Welcome to my documentation site.

## First steps

Edit this page and preview the result.
```

The generated project keeps the main settings in these files:

- `GuideFrontMatter.cs`: the C# content schema, including the required title.
- `content/`: your Markdown pages.
- `DocsSiteFactory.cs`: the site title, base URL, content loading and layout.

## Build and preview

```sh
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release --port 4317
```

Open `http://localhost:4317/` for the home page or
`http://localhost:4317/guide/getting-started/` for the page you edited.
The development server builds the site and watches for changes. Stop it with Ctrl+C.

## Prepare to publish

Set your site's title and deployment base URL in `DocsSiteFactory.cs`, then build
again. The Docs template enables output quality checks and fails on errors;
external HTTP checks are disabled.

Deploy the generated `MyDocs/dist` directory to a static HTTP host. The commands
above do not deploy your site.

## Next steps

- [Typed content](../typed-content/): define schemas and use generated references.
- [Documentation and languages](../documentation/): organize navigation and translated pages.
- [Tooling](../tooling/): use the CLI and editor integration.
- [Operations and verification](../operations/): understand checks and publication.