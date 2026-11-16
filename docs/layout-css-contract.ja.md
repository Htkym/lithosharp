# レイアウトとコンポーネント CSS の契約

この契約は LithoSharp 3.0.0 の組み込みレイアウトに適用します。
意味を持つクラスと操作用のフックを対象とし、バイト単位で同じ HTML や、任意の独自テンプレートは対象にしません。
組み込みの外枠を置き換える場合は、[生成サイトの互換性の契約](compatibility-contract.ja.md)と
[empty テンプレートの独自レイアウト](../templates/LithoSharp.ProjectTemplates/content/empty/EmptyLayout.cs)を参照してください。

組み込みコンポーネントは既存の Docs と Blog のクラスを維持します。
色の変更には、`SiteThemeOptions.AdditionalCss` と、既存の `--accent`、`--accent-contrast`、`--muted`、`--border` 変数を使います。
これらの C# レイアウトコンポーネントに、追加のスタイルシート依存関係やクライアントフレームワークは必要ありません。

`BlogPageLayout` は Blog の文書の外枠、`DocsPageLayout` は Docs の外枠を担当します。
Docs は描画済みのサイドバーと目次を `PageLayoutContent` で受け取ります。
`DocsNavigationComponent` は、事前計算した `SiteTemplateNavigationNode` から下表のサイドバークラスを生成し、
渡したツリーや追加リンクを並べ替えません。
`CurrentUrl` は、現在の項目に `is-current` と `aria-current`、それを含むフォルダーに `is-ancestor` を付けます。

| 対象 | クラス |
|---|---|
| Blog のヘッダーとナビゲーション | `site-header`, `brand`, `site-nav-shell`, `site-home-link`, `site-icon-button`, `site-nav`, `site-menu-toggle`, `rss-nav-link` |
| Blog のフッター | `site-footer` |
| Docs のレイアウト | `docs-body`, `docs-header`, `docs-brand`, `docs-shell`, `docs-main`, `docs-content`, `docs-footer` |
| Docs のナビゲーション | `docs-sidebar`, `docs-nav-list`, `docs-nav-folder`, `docs-nav-link`, `is-current`, `is-ancestor` |
| 目次 | `post-toc`, `docs-toc`, `toc-nav`, `toc-track`, `toc-indicator`, `toc-list`, `toc-depth-1`, `toc-depth-2`, `toc-depth-3` |
| 前後のリンク | `docs-pagination`, `docs-pagination-previous`, `docs-pagination-next` |
| ヘッダーの検索 | `site-header-search`, `browser-search`, `visually-hidden` |

操作できる外側の UI を置き換える場合は、既存の `data-*` 属性、要素 ID、`aria-controls`、アクセシブルなラベルを維持してください。
同梱スクリプトがメニューや目次の動作に使います。
パンくずは、ナビゲーションとして意味を持つ HTML を描画しますが、新しいスタイルの約束は追加しません。
配置と見た目は独自のレイアウトが決めます。

スクリプト用のフックは、`site-menu`、`data-site-menu-toggle`、`data-site-nav`、`docs-sidebar`、
`data-docs-menu-toggle`、`data-docs-sidebar`、`post-toc-title`、`data-toc-item`、`data-toc-link` です。
ヘッダー検索の契約には、`header-search-input`、対応するラベル、ネイティブの GET フィールド `q` と `type="search"` を含めます。
`aria-current="page"`、`rel="prev"`、`rel="next"` もナビゲーションの意味の一部です。

[Docs の設定](docs-template.ja.md)で任意に有効にするヘッダー検索は、固定した入力 ID を付けずに `aria-label` を使い、検索ページでは省きます。
単独の `SearchFormComponent` と Blog のヘッダーは `header-search-input` を維持します。
追加フッターの内容は `site-footer-additional`、トピックの開閉とカードは `docs-nav-topic`、`docs-topic-cards`、`docs-topic-card` を使います。

<a id="テーマ切り替え"></a>

## テーマの切り替え

Docs と Blog は、読者が配色を選ぶまで OS の設定に従います。
選択は、同じオリジンのローカルストレージに `lithosharp-theme` として保存します。
JavaScript がない場合も OS の配色を適用しますが、切り替えは表示しません。

`SiteThemeOptions.EnableThemeSwitching = false` にすると、組み込みの切り替え UI とテーマ設定のスクリプトを省きます。
既定値は `true` です。無効の場合は、組み込みのスタイルシートが OS の配色に従わず、ライトを使います。
ダークに固定するには、`AdditionalCss = ":root { color-scheme: dark; }"` を指定します。
組み込みのヘッダーと head コンポーネントを使う独自テンプレートも、この設定を引き継ぎます。
独自の操作部品ではこの設定に明示的に従う必要があります。

```csharp
var customization = new SiteCustomization
{
    Theme = new SiteThemeOptions
    {
        EnableThemeSwitching = false,
        AdditionalCss = ":root { color-scheme: dark; }"
    }
};
```

`AdditionalCss` を省くと、ライトに固定します。
設定を変えたら再生成してください。増分ビルドでも、影響する HTML、CSS、JavaScript を更新します。
切り替えを無効にしている間は、保存した選択を無視します。
有効にしている場合は、ストレージが制限されていても、現在のページ内では切り替えられます。

保存した配色は、CSS を読み込む前に、head のインラインスクリプトで復元します。
厳密な Content Security Policy では、そのスクリプトの許可が必要です。
配色には、CSS の `light-dark()` 対応が必要です。
