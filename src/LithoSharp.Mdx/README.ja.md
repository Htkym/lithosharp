# LithoSharp.Mdx

[English](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Mdx/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Mdx/README.ja.md)

LithoSharp の C# の型とビルドで管理するドキュメントに、任意で MDX と React を追加できます。Markdown だけの Docs には、このパッケージも Node.js も不要です。対話型コンポーネントや MDX のドキュメント機能がサイトに適している場合に利用してください。

このパッケージと、プロジェクト内のほかの LithoSharp パッケージには、LithoSharp 3.0.0 を指定してください。

```sh
dotnet add package LithoSharp.Mdx --version 3.0.0
```

LithoSharp Core と Tool のバージョンもそろえてください。

MDX 3 と React によるビルド時のレンダリングは、明示的に有効にして使います。ワーカーは信頼して実行するビルドコードであり、サンドボックスではありません。ワーカーディレクトリで `npm ci --ignore-scripts --no-audit --no-fund` を実行し、ロックされた依存関係を明示的に復元してください。通常の Markdown ビルドには Node もこのパッケージも不要です。.NET 10 と Node.js 24.13.0 を使用してください。ロックファイルでは MDX 3.1.1、React 19.2.4、esbuild 0.28.2 を固定しています。

Syntamark 2 は共通の Markdown 解析とネイティブな YAML フロントマター解析を提供します。MDX 本文には引き続き MDX パーサーを使用します。エディターでの検査は解析専用ワーカーを使い、インポートされたモジュール、プラグイン、サーバー側のレンダリングを実行しません。見出しに基づく MDX のアウトラインは Markdown のセクション階層とは別のものです。ワーカーがない場合は MDX 解析を利用できない状態として扱い、Markdown へのフォールバック成功とはみなしません。ビルド時の MDX 実行は引き続き、信頼して実行するアプリケーションコードです。

[MDX ガイド](https://github.com/Htkym/lithosharp/blob/main/docs/mdx.md)、[クイックスタート](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md)、[既知の制限](https://github.com/Htkym/lithosharp/blob/main/docs/known-limitations.md)を参照してください。
