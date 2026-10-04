# LithoSharp 1.1 の機能と更新手順

[English](release-1.1.md)

候補状態：Core 1.1と拡張0.1.0は検証候補である。NuGet packageの版数を1.1.0へ更新した。
最終の配布検証は未完で、拡張はMarketplaceにない。*未測定*の数値は
最終のV110-25測定後に差し替える。ここで先行して高速化を主張しない。

## 機能表

| 領域 | Core 1.1 | VS Code拡張 0.1.0 |
| --- | --- | --- |
| Markdown | Litho compiler、LIT001/002診断、opt-inのLIT003/004/005助言、project対応の検査snapshot | 元ID付きLIT診断、版数管理表示 |
| MDX | plugin/bundle/SSR実行なしの解析専用検査、lock固定worker（MDX 3.1.1、React 19.2.4、esbuild 0.28.2） | debounce付きLSP session、Outline symbols、キー入力でbuildしない |
| build/serve | generation追跡、出力別cache報告/回収、構造化`serve`停止 | project別build/serve状態機械、実出力preview shell |
| 移行 | route oracle・正規化page set比較・component機能差report付きDocusaurus解析/変換、固定3サイト再現corpus | —（CLI駆動） |
| Tooling契約 | `lithosharp capabilities`、加算的な1.x JSON envelope、schema `"1.0"` | stdio上の言語server `lithosharp`。Core assemblyの版数を返す |

1.1には、次の制限がある。

- previewは保存済み文書のみ。未保存bufferのpreviewはない。
- MDX workerはTypeScriptを変換するだけで、型検査はしない。
- 配布は未実施。拡張はlocal VSIXから入れる。
- browser検証はChromiumのみ。Firefox/Safariは主張しない。
- CLI/site hostのtrimmingとNative AOTは非対応のままである。
- AVIFは信頼済みの`avifenc`を明示設定しない限り未検証である。
- 先の検証候補はWindows、Linux、macOSのCIに成功した。現候補の最終の複数OS検証と30分soakは未完で、現行soakの証拠はWindowsでの高速編集である。

## 1.0から1.1への更新

1. 7つのCore packageとtoolを同時に上げる。1.0と1.1の混在はしない。公開後は同じ1.1.0版を使う。local RCの最終検証は未完である。
2. siteを再buildし、content警告・link・custom CSSを見直す。
3. Markdown compilerは1.0から不変である。footnote、definition list等のMarkdig拡張は非対応のままである（[既知の制限](known-limitations.ja.md#markdown-の互換性)参照）。
4. `markdown-compat --advisory on`と`--project`はEditor検査と同一解析であり、既定出力を変えない。

SkiaSharpを4.153.1へ更新した。この版ではRAWとDNGを読み込めない。画像変換の入力や
サイトのアイコンに使う前にPNGかJPEGへ変換する。詳しくは[資産と画像](assets-and-images.ja.md)を参照。
YamlDotNet 18.1.0では、デシリアライザーの既定の再帰上限が130になった。
`MarkdownFrontMatterYaml.Deserialize<T>`にも適用されるため、深い入力は上限を外さず構造を浅くする。
型付きコンテンツのローダーは独自の厳格パーサーを使い、静的コンテンツ生成器の既存のネスト上限64は維持する。

## APIとschemaの方針

公開signatureは維持し、新規APIは1.x内で加算する。追加は
`src/LithoSharp/PublicAPI.Unshipped.txt`に記録する。構造化CLI/serve出力は
schema `"1.0"`を維持し、未知JSON fieldは無視する。それ以外は2.0待ちである。
[互換性契約](compatibility-contract.ja.md)と
[CLI互換性](cli.ja.md#構造化出力と-1x-の互換性)参照。

## 必要条件とversion組合せ

- .NET 10 SDK。最低対応版は10.0.300で、現在の検証にはSDK 10.0.401とruntime 10.0.12を使う。source generatorは`netstandard2.0`でRoslyn 4.14.0相手であり、利用側も対応SDKが必要である。preview版は使わない。
- `serve`はSDK付属のASP.NET Core shared frameworkも使う。
- Linuxでは[.NETのネイティブ依存](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual#dependencies)も必要である。
  `libstdc++.so.6`はUbuntuの`libstdc++6`、Alpineの`libstdc++`に含まれる。
  SkiaSharp 4.153.1もこのC++ランタイムを使い、`Linux.NoDependencies`パッケージでも必要になる。
- Markdown-onlyのsiteにNode.jsは要らない。MDXはNode.js 24.13.0と明示のworker restoreが必要である。任意のNode versionやnpm packageは認定しない。
- 1つのrepositoryではCore/Tool/Generatorを同一versionで揃える。拡張0.1.0は1.x言語serverとstdioで話し、`serverInfo`にCore versionを出す。CLIとserverでCore assemblyが食い違う組合せは非対応である。拡張はVS Code `^1.139.0`が必要である。
