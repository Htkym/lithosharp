---
title: Upgrade to LithoSharp 4.0
supported_version: "4.0"
prerequisites: [introduction]
---

## Upgrade from 3.x

Update the packages, CLI and project templates together to 4.0.0. For each OKF
bundle, add a root `index.md` with quoted `okf_version: "0.2"` to keep the
previous rules. Choose `"0.3"` only after reviewing changes to relative paths,
verification and timestamps. Run `lithosharp okf validate --input knowledge`
before generating the site. Ordinary Markdown does not require this declaration.
See the [OKF guide](../knowledge/) for details.

## Upgrade from 2.x or earlier

Use the [complete 3.0 migration guide](../../reference/en/migration-3.0/)
before upgrading an existing site. The guide covers the fixed Syntamark pair,
native YAML and content attributes, strict DTO binding, optional PNG processing,
explicit social images, Markdown profiles, typed components, acquired editor
schemas, cache compatibility and verification evidence.

The migration replaces YamlDotNet attributes with `ContentName` and `ContentIgnore`.
Core image registration becomes separate from optional image processing. The
legacy Markdown profile remains the default; select the documentation profile
explicitly when adopting directives. Keep a new output directory and compare URLs
and anchors against a successful published baseline.
