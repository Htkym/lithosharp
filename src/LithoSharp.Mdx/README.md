# LithoSharp.Mdx

Opt-in MDX 3 and React build-time rendering. The worker is trusted build code,
not a sandbox. Restore its locked dependencies explicitly with
`npm ci --ignore-scripts --no-audit --no-fund` in the worker directory. Normal
Markdown builds do not require Node or this package. Runtime and integration
examples are documented in `docs/mdx.md` in the repository.
