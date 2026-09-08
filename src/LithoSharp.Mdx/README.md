# LithoSharp.Mdx

Opt-in MDX 3 and React build-time rendering. The worker is trusted build code,
not a sandbox. Restore its locked dependencies explicitly with
`npm ci --ignore-scripts --no-audit --no-fund` in the worker directory. Normal
Markdown builds do not require Node or this package. Use .NET 10 and Node.js
24.13.0; the lockfile pins MDX 3.1.1, React 19.2.4 and esbuild 0.25.12.
See the [MDX guide](https://github.com/Htkym/lithosharp/blob/main/docs/mdx.md),
[Quick Start](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md) and
[Known limitations](https://github.com/Htkym/lithosharp/blob/main/docs/known-limitations.md).
