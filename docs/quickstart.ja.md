# Quick Start

[English](quickstart.md)

.NET 10 SDKを用意してください。最低対応バージョンは10.0.300です。MarkdownだけのDocsには
Node.jsとReactは不要です。任意のMDXにはNode.js 24.13.0と明示的に復元したworkerを使います。
`serve`が使うASP.NET Core shared frameworkはSDKに含まれます。

以下のコマンドはNuGetで公開済みのToolとTemplates 2.2.0を導入します。
パッケージの選択は[配布とインストール](distribution.ja.md)を参照してください。

## 型付きMarkdown Docsを作る

新しいディレクトリで実行してください。

```sh
dotnet new install LithoSharp.ProjectTemplates::2.2.0
dotnet tool install LithoSharp.Tool --version 2.2.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
```

Windowsでは`.tools/lithosharp.exe`を使います。テンプレートは利用者のtemplate catalogへ
インストールされ、Toolは`.tools`内に入ります。生成プロジェクトは既定で2.2.0を使います。異なるバージョンを混ぜたり、
既存サイトへテンプレートを上書きしたりしないでください。

## C#の型とMarkdownを編集する

`GuideFrontMatter.cs`には必須の`Title`、任意の`Description`と`Order`、生成collectionの
`Guides`を定義しています。プロジェクトは同じバージョンのCore、Generators、Analyzersを
参照し、コンパイル用パッケージは非公開の依存にしています。factoryは`Guides.Binder`で
Markdownを読み、`Guides.Catalog.GetRoute(entry.Id)`でルートを取得します。その後に
`RenderDocs`でDocsの枠とナビゲーションを構成します。このスキーマに日付の必須項目はありません。

`MyDocs/content/getting-started.md`を編集します。

```markdown
---
title: Getting started
description: A guide checked with the project build.
order: 1
---

# Getting started

This is **Markdown** rendered by .NET.

## First build

Build the site before distributing its output.
```

C#スキーマへ項目を加えた場合はMarkdownの値も変更し、表示に使う項目ならselectorも変更して
ください。テンプレートには、もう一つの正常なページ`content/index.md`が含まれます。
意図的な異常例は[DocsSampleの体験手順](../samples/LithoSharp.DocsSample/README.ja.md#cの型定義から生成物の検査まで)に分離しています。

コンパイル時の`AdditionalFiles`と実行時loaderは同じ`content`をrootにします。
IDは相対pathと拡張子を含み、`getting-started.md`と`reference/getting-started.md`を区別します。
ルートは`guide/<拡張子を除いた相対path>/`です。区切りにはOSによらず`/`を使います。
`_`で始まるファイルとディレクトリは、両方で除外します。既定の入力規則を変える場合は、
追加、改名、除外の後もコンパイルと実行時の対象がそろうようにしてください。
実行時のファイルが生成Catalogにない場合は、推測のURLへ変更せず、ルート取得に失敗します。
coverageは実際のコンパイル入力から決まり、固定値でClosedを保証しません。

## 生成した参照を使う

factoryには、次の生成参照を使うリンクが含まれます。

```csharp
Guides.Entries.Content_Getting_Started.GetUrl(rendering.Site.BaseUrl)
```

付属factoryと同じように、HTML属性には`ToAttributeValue()`の結果を使ってください。
生成名は宣言した入力に対応します。改名後に再ビルドすると、利用できる名前も更新されます。
Catalogと重複する手書きのルート辞書は作りません。独自binder、参照、実行時検証の詳細は
[型付きcollection](typed-markdown-collections.md)の英語リファレンスを参照してください。

## ビルド、検査、プレビュー

`DocsSiteFactory.cs`でサイト名と実際の公開先base URLを設定してください。
新しいfactoryはQualityを明示的に有効にし、Errorで失敗する閾値を設定しています。
外部HTTP検査は無効です。Coreの既定値`Quality = null`は変わりません。
CLIと`dotnet run`は同じfactoryを使います。生成したProgramは品質診断を表示し、
読み込みや検証に失敗した場合は失敗終了します。

```sh
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp check MyDocs -c Release --format json
.tools/lithosharp check-output --output MyDocs/dist --base-url https://example.com/ --format json
.tools/lithosharp serve MyDocs -c Release --port 4317
```

`check-output`には実際のbase URLを指定してください。`http://localhost:4317/`と
`http://localhost:4317/guide/getting-started/`を開きます。ホームは`dist/index.html`です。
コンテンツのページには`dist/guide/index/index.html`と
`dist/guide/getting-started/index.html`があります。ホームの生成はDocsテンプレートだけが
担当します。`serve`は最初にビルドし、保存した出力を配信して変更を監視します。
再ビルドしない読み取り専用のプレビューではありません。Ctrl+Cで終了できます。
配布物を変更せずに確認する場合は、静的HTTPホストから`dist`を配信してください。

| コマンド | 対象 | 成功から分かること |
| --- | --- | --- |
| `build` | コンパイルしたプロジェクト、factory、生成物 | 出力確定前に、生成と設定済みQualityの条件を満たしました。 |
| `check` | 新しく生成する一時出力 | 設定した公開先へ出力せず、品質を検査しました。 |
| `check-output` | 指定したディレクトリの保存済みファイル | その成果物を検査しました。現在の原稿との一致は確定しません。 |
| `preflight --mode static`・`--mode trusted` | 明示した原稿・コンパイルした信頼済みfactoryとCatalog | 報告されたPartialな工程の結果です。最終HTMLの描画やサイト全体の検証は行いません。 |

必須タイトルの欠落はコンパイル時に`LSG006`、静的に確定したentryとルートの矛盾は
`LSA1402`、最終出力のリンク先・anchorの欠落は`LSQ001`・`LSQ002`になります。
エディターの構文・見出しの検査は入力のたびに独自C#スキーマを実行するものではありません。
`NoContext`、`Partial`、`Unknown`を完全な検証と扱わないでください。
[診断](diagnostics.ja.md)、[preflight](cli.ja.md)、[出力検査](generated-output-checks.ja.md)も参照してください。

## このDocsプロジェクトのCI

サイトのツール・パッケージ設定ではバージョンを2.2.0にそろえてください。
配布する出力を生成し、同じ出力を検査します。

```sh
dotnet restore MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp check-output --output MyDocs/dist --base-url https://example.com/ --format json
```

CIは非ゼロの終了コードで停止するようにしてください。生成時にはfactoryの明示的なQualityと
Error閾値が適用され、最後のコマンドは保存済み成果物を検査します。どちらかが失敗した場合は
公開しないでください。このMarkdownのCIにnpmの復元は不要です。既存プロジェクトは
[移行手順](migration-2.0.ja.md#既存docsを型付きコンテンツへ移す)に従い、明示的に設定を変更してください。

## MDXと対話componentの追加

install済みのtoolとtemplateで別のMDXサイトを作ります。

```sh
.tools/lithosharp new mdx MyMdx -o MyMdx
dotnet build MyMdx -c Release
.tools/lithosharp restore-mdx MyMdx/bin/Release/net10.0/worker
.tools/lithosharp build MyMdx -c Release
```

worker restoreは同梱lockfileを使う明示的なnetwork操作です。通常の生成ではnpm依存をinstallしません。
npm importを追加した場合はprojectの依存を別途restoreしてください。MDXとcomponentには信頼できるコードだけを使います。

`MyMdx/content/_components/Counter.tsx`を作ります。

```tsx
import {useState} from 'react';

export default function Counter({initial = 0}: {initial?: number}) {
  const [count, setCount] = useState(initial);
  return <button onClick={() => setCount(count + 1)}>Count {count}</button>;
}
```

`MyMdx/content/counter.mdx`を作ります。

```mdx
---
title: Counter
---
import Counter from './_components/Counter.tsx';

# Counter

The heading and button are rendered at build time.

<Island component={Counter} props={{initial: 3}}
  schema={{type: 'object', properties: {initial: {type: 'integer'}}, additionalProperties: false}}
  strategy="load" />
```

templateは`Hydration = "selective"`を選びます。このislandはload時に起動します。
通常の`<Counter initial={3} />`はpage hydrationへfallbackします。TypeScriptはtranspileのみなので、
型検査が必要なら別途type checkerを実行してください。

```sh
.tools/lithosharp build MyMdx -c Release
.tools/lithosharp check MyMdx -c Release --format json
.tools/lithosharp serve MyMdx -c Release --port 4317
```

`http://localhost:4317/guide/counter/`を開き、**Count 3**を押すと**Count 4**になります。
生成ファイルは`MyMdx/dist/guide/counter/index.html`です。本番ファイルの配信にNode processは不要です。

## 本番出力

開発serverを止め、`SiteSettings.BaseUrl`を配置先URLへ変更してRelease buildを実行します。
`dist`一式を静的HTTP hostへ配置し、内部用の`.lithosharp-*` ownership metadataは除外してください。
cache directoryは公開出力の外に置きます。開発用reload scriptは開発serverがHTTP応答へ挿入するため、本番HTMLには書き込まれません。
更新時は、古いbrowser sessionが参照するhash付きassetを保持してください。

## 使用例と次の手順

| 目的 | 既存のsampleまたは検証fixture |
| --- | --- |
| Markdown、typed generator、集約ページ、画像 | [Docs sample](../samples/LithoSharp.DocsSample/README.ja.md)、`--asset-demo --check` |
| 従来のBlog、feed、search | [Blog sample](../samples/LithoSharp.Sample/README.ja.md) |
| React island、progressive navigation、live code、PWA | [MDX sample](../samples/LithoSharp.MdxSample/README.ja.md) |
| 複数version/locale、Blog/Pages、API reference | [MDXガイド](mdx.ja.md)と[実行可能な統合fixture](../tests/LithoSharp.Tests/DocumentationTests.cs) |
| XML API buildと検証済みコード例 | [documentation verification](../eng/Test-DocumentationVerification.ps1) |
| 独自C# layout | `lithosharp new empty MySite -o MySite` |

testは高度な設定を実行できる例であり、追加のtemplate製品ではありません。
[2.2.0への更新](migration-2.0.ja.md)、[Testing](testing.ja.md)、[性能](performance.ja.md)、[既知の制約](known-limitations.ja.md)も参照してください。
