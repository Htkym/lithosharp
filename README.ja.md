# LithoSharp

[![build](https://github.com/Htkym/lithosharp/actions/workflows/build.yml/badge.svg)](https://github.com/Htkym/lithosharp/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/LithoSharp.svg)](https://www.nuget.org/packages/LithoSharp)

[English](README.md) | [日本語](README.ja.md)

C#、Markdown、MDX、Reactと増分ビルドを使える、型安全な.NET向け静的サイト・ドキュメント生成器です。
.NETのコードと一緒に文書を管理し、生成時にコンテンツやリンクを検証して、通常の静的ファイルとして配置できます。

バージョンは **1.0.0** です。

## サンプルサイト

同梱のサイト定義から生成した画面です。標準のDocsとBlogは、ライト・ダーク切り替え、
画面幅に応じたメニュー、キーボード操作に対応します。
`SiteThemeOptions.EnableThemeSwitching = false` で切り替えを無効にできます。
詳しくは[テーマの設定](docs/layout-css-contract.md#theme-switching--テーマ切り替え)を参照してください。

| Docsのライト配色 | Blogのダーク配色 |
| --- | --- |
| [![サイドバー、本文、目次を表示したDocsサンプル](docs/images/docs-light.png)](samples/LithoSharp.DocsSample) | [![先頭記事とナビゲーションを表示したBlogサンプル](docs/images/blog-dark.png)](samples/LithoSharp.Sample) |

## 主な機能

- 型付きC# content collection、route、reference。厳密なschema検証と、任意のRoslyn source generator・build診断。
- Markdownと任意のMDX 3。Reactによる生成時HTML、page hydration、明示islandによるselective hydration。
- Docs、Blog、独立ページ、複数Docs collection、version、locale、sidebar、local search。
- 依存graph、永続cache、invalidation reason、artifact ownershipによる増分ビルドの確認。
- routeとownershipを検証したうえでのstaging、output transaction、公開前のrollback。
- link、anchor、canonical、redirect、SEOに対する構造化診断。
- XML形式の.NET API文書、OpenAPI 3 JSON、正確なxref、検証済みexampleの取り込み。
- site、route、artifact、DOM、component、layoutを検証する.NET向けTesting API。
- fingerprint付きasset、responsive image、C# renderingと信頼するMDX pluginの拡張点。

## Quick Start

.NET 10 SDKを用意して実行します。

```sh
dotnet new install LithoSharp.ProjectTemplates::1.0.0
dotnet tool install LithoSharp.Tool --version 1.0.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release
```

Windowsの実行ファイルは`.tools/lithosharp.exe`です。`MyDocs/content`を編集し、
`DocsSiteFactory.cs`で設定します。生成した`MyDocs/dist`は静的HTTP hostへ配置できます。
独立したinstall、Markdown、MDX、対話componentの手順は[Quick Start全体](docs/quickstart.ja.md)を参照してください。

既存applicationでは`dotnet add package LithoSharp --version 1.0.0`でライブラリを追加し、
直接呼び出せます。CLI hostは必須ではありません。

## 使用例

| 目的 | 入口 |
| --- | --- |
| Markdown Docs、typed content、画像 | [Docs sample](samples/LithoSharp.DocsSample) |
| Blog、フィード、検索 | [Blog sample](samples/LithoSharp.Sample) |
| MDX、React island、offline navigation | [MDX sample](samples/LithoSharp.MdxSample/README.ja.md) |
| versioning、i18n、Blog/Pages、API docs | [MDXガイド](docs/mdx.ja.md)と[使用例の案内](docs/quickstart.ja.md#使用例と次の手順) |

## 必要な環境

.NET 10が必要です。公開準備ではSDK 10.0.300を使っています。CLIの開発serverはSDKに含まれる
ASP.NET Core shared frameworkも使います。MarkdownだけならNode.jsとReactは不要です。
MDXはNode.js 24.13.0と明示的なworker restoreが必要です。lockfileはMDX 3.1.1、React 19.2.4、
esbuild 0.25.12を固定しています。本番出力の配信には静的HTTP hostだけで十分です。

## ドキュメント

- [CLIとsite factory](docs/cli.ja.md)、[MDXと文書サイト](docs/mdx.ja.md)
- [型付きMarkdown collection](docs/typed-markdown-collections.md)、
  [型付きYAML](docs/typed-yaml-collections.md)、[source generator](docs/source-generators.md)
- [Build graph](docs/build-graph.ja.md)、[増分ビルド](docs/incremental-builds.ja.md)
- [Assetと画像](docs/assets-and-images.ja.md)、[サイト品質](docs/site-quality.md)
- [Testing](docs/testing.ja.md)、[HTML/CSS契約](docs/layout-css-contract.md)、[互換性契約](docs/compatibility-contract.ja.md)

## 1.0.0への更新

利用しているLithoSharpのパッケージとCLIを1.0.0に揃えて更新し、サイトを再生成して、
コンテンツの警告、リンク、独自CSSの表示を確認してください。既存の公開シグネチャは維持していますが、
Markdownコンパイラーの変更に伴い、脚注や定義リストなど一部のMarkdig拡張は非対応になりました。
更新前に[Markdownの制約](docs/known-limitations.ja.md#markdown-の互換性)と
[1.0.0の変更履歴](CHANGELOG.ja.md#100)を確認してください。

[APIの互換性契約](docs/compatibility-contract.ja.md#api-の互換性)と
[1.xのTooling互換方針](docs/cli.ja.md#構造化出力と-1x-の互換性)も参照してください。
0.2.0から更新する場合は、[0.3への移行ガイド](docs/migration-0.3.ja.md)も必要です。
MDXの導入は引き続き任意です。

## 既知の制約

MDXとC#のsite codeは信頼するbuild codeとして実行します。HTML safetyはコードのsandboxではありません。
大規模MDXでは多くのinteractive entryを再bundleする場合があります。任意のDocusaurus plugin、
trimming、Native AOTは非対応です。実行環境を選ぶ前に[既知の制約](docs/known-limitations.ja.md)を確認してください。

## 性能

0.3.0で測定した静的ページfixtureでは、selective hydrationによりJSが449,585から15,817 bytes、
hydration rootが1から0になりました。全islandやfallbackの結果ではありません。
10,000ページMDX corpusのcoldは274.30秒、no-opは105.13秒でした。100件と1,000件の
競合比較の要点と、1.0.0候補で再測定したMarkdownの結果も併記しています。
[測定条件・制約・再現手順](docs/performance.ja.md)を参照してください。

## 貢献とライセンス

buildと検証の手順は[CONTRIBUTING](CONTRIBUTING.md)を参照してください。
MIT licenseです。[LICENSE](LICENSE)と[third-party notices](THIRD-PARTY-NOTICES.md)も確認してください。
