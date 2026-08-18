---
title: "Installation"
date: "2026-01-03T09:00:00Z"
summary: "Install LithoSharp and create a documentation site."
sidebar_position: 1
tags:
  - guide
---

## Add the package

Add LithoSharp to a .NET project, then read Markdown content with
`MarkdownPostReader`.

## Generate the site

Pass the content to `SiteGenerator.GenerateAsync`. The default template is
`DocsSiteTemplate`, so no template selection is required.
