# 変更履歴

[English](CHANGELOG.md)

## 1.0.0

- 実行時の Markdig 依存を Litho Markdown コンパイラーへ置き換えました。HTML、見出し、リンク、画像、検索文字列を同じ解析結果から取得し、永続キャッシュは内容を検証して再利用します。
- 文書 inspection、workspace の snapshot、front matter schema、navigation の相互参照、構造化診断、バージョン付きの CLI・serve 出力を安定契約として追加しました。
- Docusaurus の移行分析、別ディレクトリへの変換、route 比較、元文書の fingerprint を持つ修正候補を追加しました。移行時に設定コードを実行せず、未対応の構文は文書化した手順で対応します。
- MDX の対応コンポーネントを追加し、`mdx-code-block` の処理後も診断の元行番号を保つようにしました。
- inspection、schema、navigation の snapshot を書き換えから保護し、目次の二重エンコードとリンク経由の移行先検証を修正しました。

既存の公開シグネチャは維持しています。Markdown の互換性は[記載した範囲](docs/known-limitations.ja.md#markdown-の互換性)に限られ、脚注や定義リストなど一部の Markdig 拡張には対応していません。更新後は再生成し、コンテンツの警告を確認してください。[Docusaurus 移行手順](docs/docusaurus-migration.ja.md)と[Tooling 互換方針](docs/cli.ja.md#構造化出力と-1x-の互換性)も参照してください。

## 0.3.1 — 2026-09-09

- 標準のDocs・Blogのレイアウト、書体、余白、画面幅に応じたナビゲーションを刷新しました。
- OS設定と保存済みの選択に対応するライト・ダーク配色を追加しました。
  `SiteThemeOptions.EnableThemeSwitching` は既定で `true` です。`false` にすると
  ボタンと設定の保存・復元処理を省き、ライト配色に固定します。
  `AdditionalCss` によるダーク固定も可能です。
- 本文へのスキップリンク、フォーカス表示、Escキーの処理、JavaScript無効時のナビゲーションを改善しました。
- 共通のNuGetアイコンを更新し、READMEにサンプルサイトの画像を追加しました。

既存の公開署名とURLは維持します。生成するHTML・CSS・JavaScriptは変わるため、
再生成後に独自CSSの表示を確認してください。[テーマの設定](docs/layout-css-contract.md#theme-switching--テーマ切り替え)も参照してください。

## 0.3.0 — 2026-09-08

### 追加

- 型付きMarkdown、YAML、JSON、CSV collection、route、reference、schema、source generationとbuild診断。
- 依存graph、永続cache、invalidation report、artifact ownership、stagingを使う出力transaction、
  品質検査、redirect、fingerprint付きasset。
- 任意の画像変換、.NET Testing API、CLI/site factory、Docs・Blog・空のC# layout・MDXの4種類のtemplate。
- 任意のMDX 3とReact。生成時HTML、page hydration、明示islandとselectiveな起動戦略。
- versioned・多言語Docs、複数collection、Blog/Pages、local search、対応範囲内のDocusaurus alias、
  XML/OpenAPIのreference pageと正確なxref。
- progressive browser navigation、任意のoffline機能、sandbox内のlive code、同意に基づくanalytics。

### 変更

- 公開条件を従来の投稿にも適用し、renderと派生成果物の生成前に除外します。出力routeとownershipの検証を強化しました。
- 欠落したsocial imageへのmetadataを出さなくなりました。テキスト出力の正規化と内容に基づく
  search fingerprintにより再現性を改善しました。
- CoreにAngleSharp 1.7.3を追加しました。`LithoSharp`に加えて任意の6 package IDを提供します。
  既存Markdown applicationでの採用は必須ではありません。

### 性能

- 測定した静的ページfixtureでは、selective hydrationによりJSが449,585から15,817 bytes、
  hydration rootが1から0になりました。
- Markdown 100/1,000ページの反復測定では、時間・割当量・常駐メモリの増加が中央値で基準版の10%以内でした。
- 大規模MDXの増分renderでは、多くのinteractive entryをbundleする場合が残ります。
  条件付きのWindows測定であり、競合比較ではありません。[性能](docs/performance.ja.md)を参照してください。

### 互換性と移行

実NuGet 0.2.0に対して、非互換な公開署名とobsolete警告の追加はありませんでした。独立したlegacy
consumerは、Docs、Blog、custom templateのソース・バイナリ更新検証を通過しました。
以前は無視していた公開条件のkey、危険なbase URL、診断文言の厳密な比較には挙動の変更があります。
出力バイト列も変わります。rollbackを含む[0.2.0からの移行](docs/migration-0.3.ja.md)を確認してください。

0.3.0は.NET 10向けです。MDXだけはNode.js 24.13.0と明示restoreが必要で、lockfileは
MDX 3.1.1、React 19.2.4、esbuild 0.25.12を固定しています。今後の0.x minor releaseでは
公開APIが変わる可能性があります。信頼境界、大規模生成、browser検証範囲、AOT、AVIFの状況は
[既知の制約](docs/known-limitations.ja.md)を参照してください。

## 0.2.0

MarkdownのDocs・Blog出力、front matter、custom template、共通のsite customizationを提供する
公開済みCore packageです。0.3.0の互換性基準として引き続き利用できます。
