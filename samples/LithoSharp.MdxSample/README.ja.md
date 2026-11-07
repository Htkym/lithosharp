# MDXとReactのsample

[English](README.md)

このsampleはrepositoryのprojectを参照します。公開packageを使う場合は
[MDX Quick Start](../../docs/quickstart.ja.md)から始めてください。.NET 10 SDK（10.0.300以降の対応するfeature band）とNode.js 24.13.0を使います。

repository rootで実行します。

```sh
npm ci --prefix src/LithoSharp.Mdx/worker --ignore-scripts --no-audit --no-fund
dotnet run --project samples/LithoSharp.MdxSample -c Release
dotnet run --project src/LithoSharp.Tool -- check-output --output samples/LithoSharp.MdxSample/.artifacts/site --base-url https://example.com/ --format json
```

出力は`samples/LithoSharp.MdxSample/.artifacts/site`です。HTTPで配信してください。
`file:` URLではmodule読み込みとservice workerを利用できません。browser test用serverは
`node tests/fixtures/mdx-browser/serve.mjs samples/LithoSharp.MdxSample/.artifacts/site 4317`で起動します。

静的MDX、page hydrationへのfallback、load/idle/visible/media/manualの明示island、
sandbox内のlive codeを示します。factoryではlocal search、progressive navigation、offlineを有効にしています。
`--page-hydration`は比較用mode、`--offline-disabled`はoffline終了用revision、
`--next-revision`は更新用fixtureを生成します。

`check-output`は現在のローカルHTML/CSSと参照先・anchorの関係を調べます。ブラウザーのhydration、offline動作、現在の原稿との一致を検証するものではありません。対話機能にはサンプルのブラウザーfixtureを使ってください。MDXとcomponent codeはビルド実行者の権限で動くため、信頼できるものだけを使います。

共通のfront matter処理はSyntamark 1.2.0を使いますが、MDX本文はworkerが解析します。エディターのMDX symbolは見出し単位であり、Markdownのセクション全体を使うアウトラインとは別です。独自C#スキーマと生成した参照は[Docsサンプル](../LithoSharp.DocsSample/README.ja.md)を参照してください。

`SiteFactory.cs`はrepository sourceからworkerを解決します。NuGet consumerでは
`new MdxOptions(context.ProjectDirectory)`でlibraryの隣にある同梱workerを解決し、そのdirectoryを
明示的にrestoreできます。[MDX](../../docs/mdx.ja.md)、[性能](../../docs/performance.ja.md)、
[既知の制約](../../docs/known-limitations.ja.md)も参照してください。
