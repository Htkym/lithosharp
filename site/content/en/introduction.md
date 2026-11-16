---
title: Introduction
supported_version: "3.0"
sidebar_position: 1
---

LithoSharp is a static-site generator for .NET teams. Write documentation in Markdown,
define content requirements and site configuration in C#, and publish static files.
This guide covers LithoSharp 3.0.

## Start here

New to LithoSharp? [Create your first documentation site](../quickstart/) with the
Docs template. You need the .NET 10 SDK; Markdown-only sites do not require Node.js
or React.

Already using LithoSharp 2.x? Read [migration to 3.0](../migration/) before updating
your packages.

## What you can build

- Documentation sites with navigation, a table of contents, local search and multiple languages.
- Typed content collections with C# schemas, generated references and build diagnostics.
- API reference pages from .NET XML documentation and OpenAPI definitions.
- Blogs and standalone pages, with optional MDX and React for interactive content.

Use the [feature guide](../features/) to find the configuration, examples and limits
for each capability.

## From content to published pages

C# schemas check content requirements, while output quality checks inspect generated
HTML and links. These checks have different scopes: a valid content model does not
by itself guarantee that every published link works. Build and inspect your site
before deploying its output.

For the next step, explore [typed content](../typed-content/),
[documentation and languages](../documentation/) or [API documentation](../api/).