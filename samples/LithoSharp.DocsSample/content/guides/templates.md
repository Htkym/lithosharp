---
title: "Templates"
date: "2026-01-05T09:00:00Z"
summary: "Choose the built-in layout or implement a custom template."
sidebar_position: 1
tags:
  - guide
  - templates
---

## Built-in templates

`DocsSiteTemplate` is the default. Set `Template = new BlogSiteTemplate()` in
`SiteCustomization` when a blog layout is needed.

## Custom templates

Implement `ISiteTemplate` to generate custom HTML pages and text assets. The template
receives a `SiteTemplateContext` with site metadata, content, UI text, and helpers.
