---
title: "Getting started with the API"
date: "2026-01-05T09:00:00Z"
summary: "Generate a site in a few lines using the PageSharp API."
tags:
  - guide
  - pagesharp
sources:
  - type: github
    name: pagesharp/pagesharp
---

## The shape of the API

The whole flow is three calls: read posts, optionally validate, then generate.

## A minimal program

Point `MarkdownPostReader` at a content folder, pass the posts to `SiteGenerator`,
and write the result to an output directory. Customization such as UI text, theme
colors, extra pages, and content validation is supplied through `SiteCustomization`.

## Customizing output

Turn on `GenerateLlmsTxt` to emit an `llms.txt` summary for language models, swap the
theme brand prefix, or add your own `IContentValidator` to enforce house rules on every
post.
