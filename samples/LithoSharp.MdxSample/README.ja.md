# MDXとReactのsample

[English](README.md)

このsampleはrepositoryのprojectを参照します。公開packageを使う場合は
[MDX Quick Start](../../docs/quickstart.ja.md)から始めてください。.NET 10とNode.js 24.13.0を使います。

repository rootで実行します。

```sh
npm ci --prefix src/LithoSharp.Mdx/worker --ignore-scripts --no-audit --no-fund
dotnet run --project samples/LithoSharp.MdxSample -c Release
```

出力は`samples/LithoSharp.MdxSample/.artifacts/site`です。HTTPで配信してください。
`file:` URLではmodule読み込みとservice workerを利用できません。browser test用serverは
`node tests/fixtures/mdx-browser/serve.mjs samples/LithoSharp.MdxSample/.artifacts/site 4317`で起動します。

静的MDX、page hydrationへのfallback、load/idle/visible/media/manualの明示island、
sandbox内のlive codeを示します。factoryではlocal search、progressive navigation、offlineを有効にしています。
`--page-hydration`は比較用mode、`--offline-disabled`はoffline終了用revision、
`--next-revision`は更新用fixtureを生成します。

`SiteFactory.cs`はrepository sourceからworkerを解決します。NuGet consumerでは
`new MdxOptions(context.ProjectDirectory)`でlibraryの隣にある同梱workerを解決し、そのdirectoryを
明示的にrestoreできます。[MDX](../../docs/mdx.ja.md)、[性能](../../docs/performance.ja.md)、
[既知の制約](../../docs/known-limitations.ja.md)も参照してください。
