# LithoSharp

[![build](https://github.com/Htkym/lithosharp/actions/workflows/build.yml/badge.svg)](https://github.com/Htkym/lithosharp/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/LithoSharp.svg)](https://www.nuget.org/packages/LithoSharp)

[English](README.md) | [日本語](README.ja.md)

C#、Markdown、MDX、Reactと増分ビルドを使える、型安全な.NET向け静的サイト・ドキュメント生成器です。
.NETのコードと一緒に文書を管理し、生成時にコンテンツやリンクを検証して、通常の静的ファイルとして配置できます。

このbranchでは**0.3.0**を公開準備中です。以下のinstall手順は公開後に利用でき、
ローカルの候補版検証では生成したNuGet packageを使います。

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
dotnet new install LithoSharp.ProjectTemplates::0.3.0
dotnet tool install LithoSharp.Tool --version 0.3.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release
```

Windowsの実行ファイルは`.tools/lithosharp.exe`です。`MyDocs/content`を編集し、
`DocsSiteFactory.cs`で設定します。生成した`MyDocs/dist`は静的HTTP hostへ配置できます。
独立したinstall、Markdown、MDX、対話componentの手順は[Quick Start全体](docs/quickstart.ja.md)を参照してください。

既存applicationでは`dotnet add package LithoSharp --version 0.3.0`でライブラリを追加し、
直接呼び出せます。CLI hostは必須ではありません。

## 使用例

| 目的 | 入口 |
| --- | --- |
| Markdown Docs、typed content、画像 | [Docs sample](samples/LithoSharp.DocsSample) |
| 従来のBlog、feed、search | [Blog sample](samples/LithoSharp.Sample) |
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

## 0.2.0からの更新

検証した従来のソースとバイナリは0.3.0でも動作します。出力のserialization、欠落画像のmetadata、
searchのfingerprint、安全性検証には確認が必要です。[移行ガイド](docs/migration-0.3.ja.md)と
[変更履歴](CHANGELOG.ja.md)を参照してください。任意packageの追加に伴って、既存Markdownサイトが
MDXを採用する必要はありません。0.xの間は、今後のminor releaseで公開APIが変わる可能性があります。

## 既知の制約

MDXとC#のsite codeは信頼するbuild codeとして実行します。HTML safetyはコードのsandboxではありません。
大規模MDXでは多くのinteractive entryを再bundleする場合があります。任意のDocusaurus plugin、
trimming、Native AOTは非対応です。実行環境を選ぶ前に[既知の制約](docs/known-limitations.ja.md)を確認してください。

## 性能

測定した静的ページfixtureでは、selective hydrationによりJSが449,585から15,817 bytes、
hydration rootが1から0になりました。全islandやfallbackの結果ではありません。
10,000ページMDX corpusのcoldは274.30秒、no-opは105.13秒でした。
[測定条件・制約・再現手順](docs/performance.ja.md)を参照してください。同等条件の競合benchmarkはありません。

## 貢献とライセンス

buildと検証の手順は[CONTRIBUTING](CONTRIBUTING.md)を参照してください。
MIT licenseです。[LICENSE](LICENSE)と[third-party notices](THIRD-PARTY-NOTICES.md)も確認してください。
