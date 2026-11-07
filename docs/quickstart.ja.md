# Quick Start

[English](quickstart.md)

.NET 10 SDKを用意します。最低対応版は10.0.300です。MDXにはNode.js 24.13.0も必要ですが、
Markdownだけなら不要です。`serve`が使うASP.NET Core shared frameworkはSDKに含まれます。
新しいディレクトリで作業してください。以下は2.1.3を対象にした手順です。候補feedを使う場合は、提供された実際の固定候補版を指定してください。

## InstallとMarkdown Docsの作成

```sh
dotnet new install LithoSharp.ProjectTemplates::2.1.3
dotnet tool install LithoSharp.Tool --version 2.1.3 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release --port 4317
```

Windowsでは`.tools/lithosharp.exe`を使います。`http://localhost:4317/`を開き、確認後は
Ctrl+Cでserverを止めます。templateのinstallは利用者のtemplate catalogへ反映されます。
ツールは`.tools`内だけに入ります。

`MyDocs/content/hello.md`を追加します。

```markdown
---
title: Hello
date: 2026-01-02T09:00:00Z
summary: My first page.
---

# Hello

This is **Markdown** rendered by .NET.
```

再buildすると`MyDocs/dist/posts/hello.html`が生成されます。title、base URL、content directoryは
`MyDocs/DocsSiteFactory.cs`で設定します。公開前に実際の配置先URLへ変更してください。
従来のreaderにはtitleとdateが必要です。既定のsummary validatorはvalidationを要求したときに動きます。
MDXのDocsプリセットで使うtyped documentation loaderは別のスキーマを持ち、従来のdate/summary fieldを必須にしません。

## 型付きコンテンツと検出範囲

C#で定義したfront matter型と、生成される参照を試すには[Docsサンプルの体験手順](../samples/LithoSharp.DocsSample/README.ja.md#cの型定義から生成物の検査まで)を使います。正常に生成し、必須値を一箇所変更して`LSG006`を確認し、元に戻して生成物を検査する順で進めます。参照矛盾の`LSA1402`と、実行時の値のため確定できない例も分けています。

エディターの構文診断、コンパイル時の独自C#スキーマ検証、生成物の内部リンク検査は対象が異なります。`NoContext`や`Partial`で得られない情報を検証済みとは扱いません。既存出力は[check-output](generated-output-checks.ja.md)で検査できますが、現在の原稿との一致は別途再生成して確認します。

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
cache directoryは公開出力の外に置きます。開発用reload scriptはHTTP応答へ挿入し、本番HTMLには書き込みません。
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
[2.1.3への更新](migration-2.0.ja.md)、[Testing](testing.ja.md)、[性能](performance.ja.md)、[既知の制約](known-limitations.ja.md)も参照してください。
