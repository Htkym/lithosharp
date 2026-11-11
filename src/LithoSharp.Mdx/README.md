# LithoSharp.Mdx

Add optional MDX and React to documentation managed with LithoSharp's C# types and builds. Markdown-only Docs do not need this package or Node.js; use MDX when interactive components or its documentation features fit the site.

Use LithoSharp 2.2.0 for this package and the other LithoSharp packages in your project.

```sh
dotnet add package LithoSharp.Mdx --version 2.2.0
```

Use matching LithoSharp Core and Tool versions.

Opt-in MDX 3 and React build-time rendering. The worker is trusted build code,
not a sandbox. Restore its locked dependencies explicitly with
`npm ci --ignore-scripts --no-audit --no-fund` in the worker directory. Normal
Markdown builds do not require Node or this package. Use .NET 10 and Node.js
24.13.0; the lockfile pins MDX 3.1.1, React 19.2.4 and esbuild 0.28.2.

The Syntamark 1.2.0 update supplies shared front matter handling, not a replacement
MDX body parser. Editor inspection uses an analysis-only worker and does not run
imported modules, plugins or server-side rendering. Its heading-based outline is
separate from Markdown's section hierarchy. A missing worker means unavailable
MDX analysis, not successful Markdown fallback. Build-time MDX execution remains
trusted application code.

See the [MDX guide](https://github.com/Htkym/lithosharp/blob/main/docs/mdx.md),
[Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md) and
[Known limitations](https://github.com/Htkym/lithosharp/blob/main/docs/known-limitations.md).
