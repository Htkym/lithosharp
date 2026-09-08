# MDX and React sample

[日本語](README.ja.md)

This sample uses the repository projects. For released packages, start with the
[MDX Quick Start](../../docs/quickstart.md). Use .NET 10 and Node.js 24.13.0.

From the repository root:

```sh
npm ci --prefix src/LithoSharp.Mdx/worker --ignore-scripts --no-audit --no-fund
dotnet run --project samples/LithoSharp.MdxSample -c Release
```

Output is `samples/LithoSharp.MdxSample/.artifacts/site`. Serve it over HTTP;
`file:` URLs do not support module loading or service workers. The browser test
server is `node tests/fixtures/mdx-browser/serve.mjs samples/LithoSharp.MdxSample/.artifacts/site 4317`.

The content demonstrates static MDX, page-hydration fallback, explicit islands
with load/idle/visible/media/manual strategies, and sandboxed live code. The
factory enables local search, progressive navigation and offline support.
`--page-hydration` selects the comparison mode; `--offline-disabled` produces the
retirement revision and `--next-revision` produces the update fixture.

`SiteFactory.cs` intentionally resolves the worker from the repository source.
A NuGet consumer can use `new MdxOptions(context.ProjectDirectory)` to resolve
the packaged worker beside the library, then explicitly restore that directory.
See [MDX](../../docs/mdx.md), [performance](../../docs/performance.md) and
[Known limitations](../../docs/known-limitations.md).
