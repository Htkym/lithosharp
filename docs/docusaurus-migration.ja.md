# Docusaurus 移行

[English](docusaurus-migration.md)

`migrate docusaurus` は Docusaurus サイトを解析し、`--output` で別出力先へ
変換する。JavaScript 設定は実行しない。`docusaurus.config`、sidebar、plugin
は参照扱いのまま警告になる。入力ツリーは上書きしない。

移行対象は MDX、Markdown、静的資産、front matter、category、sidebar、版、
言語である。部品の判定は [MDX 互換表](mdx.md#docusaurus-compatibility) にある。

## コマンド

```sh
lithosharp migrate docusaurus ./website --output ./converted \
  --expected-routes ./expected.json --base-url https://example.com/docs/
```

`--output` がなければ読取り専用の試行になる。`--expected-routes` は公開 path
の JSON 配列を受けて変換 route を照合する。`--base-url` と `--default-locale`
で前提を定める。終了コードは正常が `0`、使い方の誤りが `2`、要手動または
変換不能が `3` である。出力 JSON report は `schemaVersion`、位置と置換付きの
文書別判定、版と著者の manifest、変換 route と不足と余剰、終了コードを持つ。

## 判定

| 判定 | 意味 |
| --- | --- |
| `Automatic` | 変更なしで変換した。 |
| `Convertible` | 機械的な修正を記録して変換した（import 除去、slug と id の修正）。 |
| `ManualActionRequired` | 警告付きで変換した。文書化した手動手順を適用する。 |
| `Unsupported` | 変換しない。明示的な置換が必要である。 |

## 実行される fence

最上位の ` ```mdx-code-block ` fence は表示用ではなく実行される MDX である。
中の import は巻き上げ、JSX は描画され、後段の本文が利用できる。外側 fence
内の sample は表示のままである。移行は実行 fence を最上位 code として扱い、
import を判定する。

## route の保持

`versions.json` が prefix を決める。`docs/` は未公開の `next` 版になり、
先頭版が prefix なし path で出る。path の `@` はそのまま残す。重複する文書
ID には番号を付ける。欠けた front matter の title は導出して記録する。日付の
ない directory 記事（blog の release folder など）は日付 slug ではなく
directory 経路になる。

生成は末尾 slash の route を出す。`trailingSlash: false` の原本は flat な
`.html` のため、比較時は slash 形式を正規化する。page 集合は原本のものを使う。
生成 category index、blog の authors と archive と pagination と tags、debug
画面に 1:1 の元文書はなく、移行 route の範囲外である。月別 archive と
directory index は文書化した上積みとして出す場合がある。

## 移行済みサイトの手動手順

- 版依存の `UpgradeGuide` デモは静的注記にする。
- `react-medium-image-zoom` の import は除き、素の `<Zoom>` で描画する。
- `LiteYouTubeEmbed` デモは YouTube link にする。
- `ColorModeToggle` デモは静的注記にする。
- `raw-loader` の表示デモは静的注記にする。
- Themed image と inline SVG の live デモは静的注記や画像にする。
- 外部 bare import は移行先に入れるか書き換える。`@docusaurus/useBaseUrl` は静的 path にする。

## 未対応の入力

任意の plugin と theme、一覧外の theme 上書き、JavaScript 設定の実行、
code editor からの `react-live` import、`useBaseUrl` と `useDocusaurusContext`
に相当品はない。report が符号付きで指摘し（`LSMIG001`、`LSMIG002`、
`LSMIG003`）、黙って誤描画せず build で落とす。
