---
title: Upgrade to 3.0
supported_version: "3.0"
prerequisites: [introduction]
---

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

The website project defaults to `3.0.0` for its intended package contract. During
development, pass an actually available candidate version and feed to `Build.ps1`.
Successful candidate consumption does not establish a formal package or site
publication. The script does not merge, deploy or advance a publication baseline.
