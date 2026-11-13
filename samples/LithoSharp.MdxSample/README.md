# MDX and React sample

[日本語](README.ja.md)

This sample uses the repository projects. For released packages, start with the
[MDX Quick Start](../../docs/quickstart.md). Use the .NET 10 SDK (10.0.300 or
a later supported feature band) and Node.js 24.13.0.

The repository targets LithoSharp 3.0.0. The selected Syntamark 2 component pair
and unpublished candidate state are recorded in the [version matrix](../../eng/version-matrix.json).

From the repository root:

```sh
npm ci --prefix src/LithoSharp.Mdx/worker --ignore-scripts --no-audit --no-fund
dotnet run --project samples/LithoSharp.MdxSample -c Release
dotnet run --project src/LithoSharp.Tool -- check-output --output samples/LithoSharp.MdxSample/.artifacts/site --base-url https://example.com/ --format json
```

Output is `samples/LithoSharp.MdxSample/.artifacts/site`. Serve it over HTTP;
`file:` URLs do not support module loading or service workers.

The content demonstrates static MDX, page-hydration fallback, explicit islands
with load/idle/visible/media/manual strategies, and sandboxed live code. The
factory enables local search, progressive navigation and offline support.
`--page-hydration` selects the comparison mode; `--offline-disabled` produces the
retirement revision and `--next-revision` produces the update fixture.

`check-output` checks the current local HTML/CSS and target/anchor relationships,
not browser hydration, offline behavior or correspondence with current sources.
Check those interactions in the browser. Only build trusted MDX
and component code; they execute with the build account's permissions.

Shared front-matter handling uses the selected Syntamark 2 runtime/Source pair,
while MDX body parsing stays in the worker. Editor MDX symbols remain heading-based, separate from native
Markdown's section-subtree outlines. For custom C# schemas and generated
references, follow the [Docs sample](../LithoSharp.DocsSample/README.md).

`SiteFactory.cs` intentionally resolves the worker from the repository source.
A NuGet consumer can use `new MdxOptions(context.ProjectDirectory)` to resolve
the packaged worker beside the library, then explicitly restore that directory.
See [MDX](../../docs/mdx.md), [performance](../../docs/performance.md) and
[Known limitations](../../docs/known-limitations.md).
