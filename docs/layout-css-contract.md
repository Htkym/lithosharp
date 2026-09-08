# Layout and component CSS contract / レイアウトとCSSの契約

The built-in components retain the existing Docs and Blog classes. Override colors
through `SiteThemeOptions.AdditionalCss`; use the existing `--accent`,
`--accent-contrast`, `--muted`, and `--border` variables. These C# layout components
do not require an additional stylesheet dependency or client framework.

組み込みコンポーネントは、既存のDocs／Blogのクラスを維持します。
色の調整には `SiteThemeOptions.AdditionalCss` と、既存の `--accent`、
`--accent-contrast`、`--muted`、`--border` を使います。
これらのC# layout componentに追加のスタイルシート依存やclient frameworkは不要です。

`BlogPageLayout` owns the Blog document shell. `DocsPageLayout` owns the Docs shell
and accepts the rendered sidebar and table of contents through `PageLayoutContent`.
`DocsNavigationComponent` produces the sidebar classes below from a precomputed
`SiteTemplateNavigationNode`; it does not reorder the supplied tree or additional
links. `CurrentUrl` adds `is-current` and `aria-current`, and marks containing folders
with `is-ancestor`.

`BlogPageLayout` はBlogの文書構造を描画します。`DocsPageLayout` はDocsの文書構造を描画し、
`PageLayoutContent` から描画済みのサイドバーと目次を受け取ります。
`DocsNavigationComponent` は、事前計算済みの `SiteTemplateNavigationNode` から下記の
サイドバー用クラスを出力し、ツリーや追加リンクを並べ替えません。`CurrentUrl` に一致する
リンクには `is-current` と `aria-current` を付け、その親フォルダーには `is-ancestor` を付けます。

| Surface / 対象 | Classes / クラス |
| --- | --- |
| Blog header and navigation / ヘッダーとナビゲーション | `site-header`, `brand`, `site-nav-shell`, `site-home-link`, `site-icon-button`, `site-nav`, `site-menu-toggle`, `rss-nav-link` |
| Blog footer / フッター | `site-footer` |
| Docs layout / 文書レイアウト | `docs-body`, `docs-header`, `docs-brand`, `docs-shell`, `docs-main`, `docs-content`, `docs-footer` |
| Docs navigation / 文書ナビゲーション | `docs-sidebar`, `docs-nav-list`, `docs-nav-folder`, `docs-nav-link`, `is-current`, `is-ancestor` |
| Table of contents / 目次 | `post-toc`, `docs-toc`, `toc-nav`, `toc-track`, `toc-indicator`, `toc-list`, `toc-depth-1`, `toc-depth-2`, `toc-depth-3` |
| Previous and next / 前後リンク | `docs-pagination`, `docs-pagination-previous`, `docs-pagination-next` |
| Header search / ヘッダー検索 | `site-header-search`, `browser-search`, `visually-hidden` |

Keep the existing `data-*` attributes, element IDs, `aria-controls`, and accessible
labels when replacing interactive chrome: the bundled scripts use them for menus
and table-of-contents behavior. Breadcrumbs render semantic navigation without a
new styling promise; custom layouts decide their placement and appearance.

The script-facing hooks are `site-menu`, `data-site-menu-toggle`, `data-site-nav`,
`docs-sidebar`, `data-docs-menu-toggle`, `data-docs-sidebar`, `post-toc-title`,
`data-toc-item`, and `data-toc-link`. The header search contract includes
`header-search-input`, its matching label, and the native GET fields `q` and
`type="search"`. `aria-current="page"`, `rel="prev"`, and `rel="next"` remain part
of navigation semantics.

メニューや目次を置き換えるときは、既存の `data-*` 属性、要素ID、`aria-controls`、
読み上げ用ラベルを維持してください。同梱スクリプトが操作に使います。
パンくずはナビゲーションとしてのHTMLを出力し、配置と見た目は独自レイアウトで決めます。

スクリプトが参照するフックは、`site-menu`、`data-site-menu-toggle`、
`data-site-nav`、`docs-sidebar`、`data-docs-menu-toggle`、`data-docs-sidebar`、
`post-toc-title`、`data-toc-item`、`data-toc-link` です。ヘッダー検索では、
`header-search-input` と対応するラベル、GETフィールドの `q`、`type="search"` を維持します。
ナビゲーションの意味を表す `aria-current="page"`、`rel="prev"`、`rel="next"` も契約に含みます。
