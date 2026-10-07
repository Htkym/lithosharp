# LithoSharp 1.1 の機能と更新手順

[English](release-1.1.md)

Core 1.1.0はNuGetで、[LithoSharp for VS Code](https://marketplace.visualstudio.com/items?itemName=htkym.lithosharp)はMarketplaceで配布している。小さな編集でも出力全体のコピーが発生する点は、[性能上の制約](performance.ja.md#110の性能上の制約)として残る。

## 機能表

| 領域 | Core 1.1 | VS Code拡張 |
| --- | --- | --- |
| Markdown | Litho compiler、LIT001/002診断、opt-inのLIT003/004/005助言、project対応の検査snapshot | 元ID付きLIT診断、版数管理表示 |
| MDX | plugin/bundle/SSR実行なしの解析専用検査、lock固定worker（MDX 3.1.1、React 19.2.4、esbuild 0.28.2） | debounce付きLSP session、Outline symbols、キー入力でbuildしない |
| build/serve | generation追跡、出力別cache報告/回収、構造化`serve`停止 | project別build/serve状態機械、実出力preview shell |
| 移行 | route oracle・正規化ページセット比較・コンポーネント機能差レポート付きDocusaurus解析/変換 | —（CLI駆動） |
| Tooling契約 | `lithosharp capabilities`、加算的な1.x JSON envelope、schema `"1.0"` | stdio上の言語server `lithosharp`。Core assemblyの版数を返す |

1.1には、次の制限がある。

- previewは保存済み文書のみ。未保存bufferのpreviewはない。
- MDX workerはTypeScriptを変換するだけで、型検査はしない。
- browser検証はChromiumのみ。Firefox/Safariは主張しない。
- CLI/site hostのtrimmingとNative AOTは非対応のままである。
- AVIFは信頼済みの`avifenc`を明示設定しない限り未検証である。
- 1.1.0ではWatchとEditorの長時間検証は実施していない。高速編集と復旧は30分の連続稼働を証明しない。

## 1.0から1.1への更新

1. 7つのCoreパッケージとツールを同時に更新する。1.0と1.1は混在させない。同じ1.1.0版を使う。
2. siteを再buildし、content警告・link・custom CSSを見直す。
3. Markdown compilerは1.0から不変である。footnote、definition list等のMarkdig拡張は非対応のままである（[既知の制限](known-limitations.ja.md#markdown-の互換性)参照）。
4. `markdown-compat --advisory on`と`--project`はEditor検査と同一解析であり、既定出力を変えない。

SkiaSharpを4.153.1へ更新した。この版ではRAWとDNGを読み込めない。画像変換の入力や
サイトのアイコンに使う前にPNGかJPEGへ変換する。詳しくは[資産と画像](assets-and-images.ja.md)を参照。
YamlDotNet 18.1.0では、デシリアライザーの既定の再帰上限が130になった。
`MarkdownFrontMatterYaml.Deserialize<T>`にも適用されるため、深い入力は上限を外さず構造を浅くする。
型付きコンテンツのローダーは独自の厳格パーサーを使い、静的コンテンツ生成器の既存のネスト上限64は維持する。

KaTeXを0.16.22から0.16.47へ更新した。同梱CSSの`font-display: block`により、
フォントを読み込む間、組版された数式が一時的に表示されないことがある。

DOMPurifyを3.4.16へ更新した。DOM node入力を`IN_PLACE: true`で処理し、
DOMPurify自身の除去規則が入力rootの除去を記録した場合、`TypeError`をスローする。hookが
除去規則を変えてrootを禁止した実測ケースでは、3.4.15が除去済みnodeを返し、3.4.16が例外を投げた。
hookが単にrootをdetachする場合まで、この受入差に含めるものではない。独自呼出しでは例外を処理し、失敗した入力・結果を破棄する。除去したnodeを安全な
コンテンツとして使い回したり、シリアライズしたりしない。この限定した互換性差を受け入れ、
[上流のXSS対策](https://github.com/cure53/DOMPurify/security/advisories/GHSA-6688-9rhm-gjv2)を維持する。
通常のFlow/Class/State tooltipは文字列を渡すため、このDOM node＋`IN_PLACE`分岐には入らない。
DOMの完全一致を主張するものではない。

更新時の範囲は[既知の制約](known-limitations.ja.md)も参照。

## APIとschemaの方針

公開シグネチャは維持し、新規APIは1.x内で追加する。構造化CLI/serve出力は
schema `"1.0"`を維持し、未知JSON fieldは無視する。それ以外は2.0待ちである。
[互換性契約](compatibility-contract.ja.md)と
[CLI互換性](cli.ja.md#構造化出力と-1x-の互換性)参照。

## 必要条件とversion組合せ

- .NET 10 SDK。最低対応版は10.0.300である。ソースジェネレーターは`netstandard2.0`でRoslyn 4.14.0向けであり、利用側も対応SDKが必要である。プレビュー版は使わない。
- `serve`はSDK付属のASP.NET Core shared frameworkも使う。
- Linuxでは[.NETのネイティブ依存](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual#dependencies)も必要である。
  `libstdc++.so.6`はUbuntuの`libstdc++6`、Alpineの`libstdc++`に含まれる。
  SkiaSharp 4.153.1もこのC++ランタイムを使い、`Linux.NoDependencies`パッケージでも必要になる。
- Markdown-onlyのsiteにNode.jsは要らない。MDXはNode.js 24.13.0と明示のworker restoreが必要である。任意のNode versionやnpm packageは認定しない。
- 1つのリポジトリではCore/Tool/Generatorを同一バージョンで揃える。拡張機能は1.x言語サーバーとstdioで通信し、`serverInfo`にCoreのバージョンを出力する。CLIとサーバーでCoreアセンブリが一致しない組み合わせは非対応である。拡張機能はVS Code `^1.139.0`が必要である。
