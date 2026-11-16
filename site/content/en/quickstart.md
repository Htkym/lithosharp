---
title: Build this site
supported_version: "3.0"
prerequisites: [introduction]
sidebar_position: 2
---

Install the SDK selected by `site/global.json` and PowerShell 7. The site is a
standalone NuGet consumer: it has no project reference to the implementation.
`LithoSharpVersion` defaults to `3.0.0`; this is the intended version, not a claim
that the package is already published.

From the repository root, select a package version available in your approved
feed and a new output directory:

```powershell
./site/Build.ps1 -LithoSharpVersion <available-version> -PackageFeed <feed> -Output <new-directory>
```

The script creates an isolated NuGet configuration and package cache in a short
working directory, restores the package consumer and its explicitly mapped tests,
builds in Release, generates the site, then verifies the snippets. The output is
for local preview with `https://example.invalid/` as its default base URL. Pass
`-BaseUrl` to check an explicitly chosen hosting URL or subpath. No deployment
or publication-baseline update is performed.

After packages are formally available, omit `-PackageFeed` to use NuGet.org.
Keep all LithoSharp packages at the same version. Source files, route declarations,
tests and generator metadata live together under `site/`; changes to example
membership require rebuilding the generated catalog.

Use [operations and verification](../operations/) to interpret failures. An
unavailable restore, skipped test or missing execution record is not a successful
site verification.
