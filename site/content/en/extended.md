---
title: Extended workflows
supported_version: "3.0"
prerequisites: [quickstart]
---

The public surface also includes workflows outside this site's default build.
Use their actual sample projects and prerequisites before enabling them.

| Workflow | Source and detailed guide | Boundary |
|---|---|---|
| Project templates | `LithoSharp.ProjectTemplates`, [distribution](../../reference/en/distribution/) | Choose docs/blog/MDX/empty; generated projects still require restore |
| Blog and publication | `samples/LithoSharp.Sample`, [quickstart](../../reference/en/quickstart/) | Explicit dates, environments, feeds and output paths |
| MDX/React and offline | `samples/LithoSharp.MdxSample`, [MDX](../../reference/en/mdx/) | Trusted Node/worker restoration and declared dependencies; not started by this Markdown-only handbook |
| YAML collections | [typed YAML](../../reference/en/typed-yaml-collections/) | Dedicated typed mapping loader; not an arbitrary YAML serializer |
| C# layout and theme | [layout contract](../../reference/en/layout-css-contract/) | Match layout/CSS regions and declared renderer identity |
| Docusaurus import | [migration guide](../../reference/en/docusaurus-migration/) | Inspect the migration report; unresolved or unsupported constructs need review |
| Output and testing | `LithoSharp.Testing`, [testing](../../reference/en/testing/) | Generated-output checks, isolated fixtures and explicit baselines |
| Performance | [performance](../../reference/en/performance/) | Controlled same-machine measurements; no speed claim from a smoke check |

The coverage map records these as existing-sample workflows. This site's build
does not certify MDX, offline updates, external search or every template. Follow
their dedicated checks and retain the actual results.
