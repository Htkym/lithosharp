# Docusaurus 移行

[English](docusaurus-migration.md)

`migrate docusaurus` は Docusaurus サイトを解析し、`--output` で別出力先へ
変換する。JavaScript 設定は実行しない。`docusaurus.config`、sidebar、plugin
は参照扱いのまま警告になる。入力ツリーは上書きしない。
変換時は、移行元と出力先のパスに親ディレクトリも含めてディレクトリリンクがあると拒否する。
リンクを経由せず、互いに重ならない実ディレクトリのパスを指定する。

移行対象は MDX、Markdown、静的資産、front matter、category、sidebar、版、
言語である。部品の判定は [MDX 互換表](mdx.md#docusaurus-compatibility) にある。

## コマンド

```sh
lithosharp migrate docusaurus ./website --output ./converted \
  --expected-routes ./expected.json --base-url https://example.com/docs/
```

`--output` がなければ読取り専用の試行になる。従来の`--expected-routes` JSON
文字列配列はraw pathのordinal exact比較を続ける。route分類と出典情報を付ける
場合はobject oracleを使う。

```json
{
  "sourceVersion": "3.10.2",
  "basePath": "/old-site/",
  "routes": [
    { "path": "/old-site/docs/start/", "kind": "document", "locale": "en" },
    { "path": "/old-site/docs/category/", "kind": "categoryIndex", "locale": "en" },
    { "path": "/old-site/blog/tags/dotnet/", "kind": "blogTag", "locale": "en" }
  ]
}
```

`kind`は`document`、`categoryIndex`、`blogIndex`、`blogAuthor`、`blogTag`、
`blogArchive`、`blogPagination`、`other`、`unclassified`から選ぶ。`locale`は
追加の分母ではなくfacetとして扱う。`document`だけが文書page set比較の対象に
入り、派生routeは数と理由を示して除外する。文書routeにも`reason`を指定すると、page setから
明示的に除外できる。oracleがなければreportは
`NotCompared`となる。不足と余剰が空でも比較合格を意味しない。解析対象source
treeのhash、oracle hash、宣言または`package.json`から検出したDocusaurus versionも出す。

raw public pathのexact比較は既定のまま維持する。`--compare-normalized-pages`を
明示すると、文書page setを追加比較する。適用規則はreportに列挙する。sourceと
targetのbase pathはsegment境界でのみ除去し、末尾slashを正規化する。path segmentは
strict UTF-8で一度だけdecodeし、Unicode NFCを適用して再encodeする。比較はordinalで
大文字小文字を区別し、case-foldや再帰decodeはしない。この比較はexit codeを変更しない。
従来の文字列配列oracleはrouteが未分類のままなので、正規化page setは`NotCompared`のままである。
文書比較には`document`種別を持つobject oracleを使う。
`--source-version`と`--source-base-path`でoracle情報を指定または上書きできる。

終了コードは解析・変換完了が`0`、処理失敗が`1`、使い方の誤りが`2`、変換不能が`3`である。
JSON reportは既存fieldを維持したまま、route比較の範囲とcomponent機能差を追加する。
route一致は宣言されたpage setだけを対象とし、原本サイト全体の互換性を意味しない。

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

生成は末尾 slash の route を出す。既定比較はraw public pathを使い、追加のpage set
比較だけが明示したslash形式とbase pathを正規化する。比較対象のpage集合は原本build
から取得する。生成category index、blog index/author/archive/pagination/tagなどの派生route
に1:1の元文書はない。oracleで別分類し、除外数と理由をreportに出す。文書page setの
一致をサイト全体の互換性とは扱わない。

## 移行済みサイトの手動手順

- 版依存の `UpgradeGuide` デモは静的注記にする。
- `react-medium-image-zoom` の import は除き、素の `<Zoom>` で描画する。
- `LiteYouTubeEmbed` デモは YouTube link にする。
- `ColorModeToggle` デモは静的注記にする。
- `raw-loader` の表示デモは静的注記にする。
- Themed image と inline SVG の live デモは静的注記や画像にする。
- 外部 bare import は移行先に入れるか書き換える。`@docusaurus/useBaseUrl` は静的 path にする。

手動component変更は、外観変更、操作変更、静的化、削除、未検証に分類する。YouTube
埋め込みをlinkへ変える場合など、機能を失う置換は非同等と記録する。未検証componentは
未検証のまま報告し、自動適用候補にはしない。

## 未対応の入力

任意の plugin と theme、一覧外の theme 上書き、JavaScript 設定の実行、
code editor からの `react-live` import、`useBaseUrl` と `useDocusaurusContext`
に相当品はない。report が符号付きで指摘し（`LSMIG001`、`LSMIG002`、
`LSMIG003`）、黙って誤描画せず build で落とす。
