# Docsテンプレートの設定

[English](docs-template.md)

## 型付きコンテンツとDocsの表示

LithoSharp 2.2.0の`docs`テンプレートは、`GuideFrontMatter`、
`Guides.Binder`、生成Catalogのルート、`RenderDocs`を使います。Blog向けの日付は不要です。
生成するファイル、入力規則、Qualityの設定、コマンドは[Quick Start](quickstart.ja.md)を
参照してください。既存サイトのreaderと表示は、明示的に変更するまで維持されます。
Coreの既定値`Quality = null`や、汎用の`RenderDocument`の挙動は変わりません。

読み込み済みの型付きcollectionから、表示する項目を明示的に選びます。

```csharp
var docs = collection.RenderDocs(
    entry => new DocsPagePresentation
    {
        Title = entry.FrontMatter.Title,
        Description = entry.FrontMatter.Description,
        Order = entry.FrontMatter.Order
    },
    (entry, context) => context.RenderMarkdown(entry.Body),
    rendererFingerprint: "docs-layout:v1",
    isThreadSafe: true);
```

この例はテンプレートの`GuideFrontMatter`と、正常に読み込んだ
`ContentCollection<GuideFrontMatter, string>`を使います。`LithoSharp.Content`をimportし、
戻り値を`SiteGenerationOptions.ContentCollections`に登録してください。
付属factoryのように`SiteCustomization.Template`で`DocsSiteTemplate`を指定します。
rendererが返すのは本文であり、完全なHTML文書を重ねて作るものではありません。
`context.RenderMarkdown`から、コンパイル済みのHTMLと見出しを共通のDocsレイアウトへ渡します。
adapterがMarkdownを再解析することはありません。

`DocsPagePresentation`には`ParentId`、`Version`、`Locale`、`Unlisted`も指定できます。
独自スキーマの項目名をこれらに固定する必要はありません。ルート、公開条件、
ナビゲーションは同じ確定済みentryを使います。draftと除外したentryは復活しません。
unlistedのページと`DerivedSurfaces.Navigation`に参加しないentryは、サイドバーと前後リンクに
含めません。ナビゲーションはcollection・version・localeの区切りを保ちます。
公開情報にdocument keyがある場合は、そのversionとlocaleを使い、表示selectorと矛盾すると失敗します。
型付きentryのラベル、概要、表示順には`Title`、`Description`、`Order`を使います。
後述する従来のfront matter項目名はlegacy reader向けであり、独自C#スキーマの項目名を固定するものではありません。
ホーム、開始リンク、任意のトピックカードは標準テンプレートが生成するため、ホームと同じ
出力先へ別のページを登録しないでください。全ページの本文をツリーへ複製せず、表示情報を共有します。

renderer fingerprintには、表示selector、本文描画、取り込んだ設定を識別できる値を使い、
外部依存も宣言してください。`isThreadSafe`はcallbackを並行実行できる場合だけ指定します。
通常のcollectionと同じcache・build graphの規則を使います。既存URLやスキーマを維持する場合は
[移行手順](migration-2.0.ja.md#既存docsを型付きコンテンツへ移す)を参照してください。

## 標準の表示設定

標準のDocsテンプレートは追加の設定なしで使えます。必要な機能だけを有効にしてください。

```csharp
var customization = new SiteCustomization
{
    Text = SiteText.Japanese,
    Template = new DocsSiteTemplate
    {
        EnableHeaderSearch = true,
        CollapseTopics = true,
        ShowTopicCards = true,
        CompactHome = true,
        ShowStartLink = false
    },
    Footer = new SiteFooterOptions
    {
        Text = "Review this guide against the current product before use.",
        Links =
        [
            new NavigationLink("License", SiteUrl.FromAbsolute("https://example.com/license")),
            new NavigationLink("Contact", SiteUrl.ForFile("contact.html", "https://example.com/guide/"))
        ]
    }
};
```

この設定を`ISiteFactory`が返す`SiteDefinition.Customization`に指定するか、`GenerateWithOptionsAsync`の`customization`へ渡します。テンプレートの設定は表示を変えるもので、ページの存在はcontent loader、C#スキーマ、ルート宣言で決まります。型付きコンテンツと検査付きの生成は[Docsサンプル](../samples/LithoSharp.DocsSample/README.ja.md)を参照してください。

サイト内のフッターリンクを作るときは、実際のベースURLを使い、リンク先のページも生成してください。
フッターのテキストとリンクのラベルはエスケープされます。絶対URLには
`SiteUrl.FromAbsolute`で検証したHTTPまたはHTTPSのURLを使います。ファイルやディレクトリへの
リンクには通常の検証済みルートを作るヘルパーを使います。フッターに任意のHTMLを渡す設定はありません。
標準の生成元表示は残り、その後に追加のテキストとリンクが表示されます。標準のDocsとBlogの
すべての文書が対象です。`Footer`を省略すると、従来のフッターを維持します。レイアウトから
`FooterComponentProps.AdditionalContent`に同じデータを渡すこともできます。

| 設定 | 既定値 | 動作 |
| --- | --- | --- |
| `EnableSearch` | `false` | ローカル検索のページ、インデックス、スクリプトとサイトマップを生成します。 |
| `EnableHeaderSearch` | `false` | ローカル検索も有効にし、ヘッダーにGETの`q`フォームを表示します。 |
| `CollapseTopics` | `false` | トピックを開閉できる標準HTMLを使い、現在のページを含む親トピックを初期展開します。 |
| `ShowTopicCards` | `false` | 最上位のナビゲーショントピックと、その配下の項目をホームページに表示します。 |
| `CompactHome` | `false` | ホームページの紹介領域の余白と見出しの大きさを抑えます。 |
| `ShowStartLink` | `true` | 最初の文書ページがある場合に、閲覧を始めるリンクを表示します。 |

ヘッダー検索は`SiteText`のラベルとサイトのベースパスを使います。検索ページでは既存の入力欄を
残し、ヘッダーのフォームを表示しません。ヘッダーのフォームは`aria-label`を使い、固定の入力IDを
付けないため、本文の見出しIDと衝突しません。検索結果の表示にはJavaScriptが必要です。
JavaScriptが無効でも、ナビゲーションと標準HTMLによるトピックの開閉は利用できます。
検索フォームのあるヘッダーは、JavaScriptが無効な場合はページと一緒にスクロールします。
JavaScriptが有効な場合は上部に固定され、折り返し後の高さを測定します。

トピックの概要ページへのリンクは開閉操作の見出しとは分かれています。トピックを閉じると、
再び開くまでその中のリンクは表示されません。ルート、ナビゲーションの順序、現在のページを示す印、
目次、前後のページへのリンクは変わりません。組み込みのナビゲーション部品を使う独自レイアウトでは、
`DocsNavigationComponentProps.CollapseTopics`で同じ開閉機能を有効にできます。

カードは既存の順序付きナビゲーションツリーから作られます。ルート直下の項目ごとにカードを表示し、
対応するページがあれば、そのページへの概要リンクとフロントマターのプレーンテキストの`summary`を
使います。対応するページがない場合は、トピック名をリンクなしで表示し、配下へのリンクは残します。
要約がない場合は説明文を省略します。入れ子のフォルダーも既存の順序と階層を維持します。
カードを別の一覧に登録する必要はありません。たとえば、`content/getting-started.md`は
`content/getting-started/installation.md`を含むトピックの概要ページになります。

## 設定・部品・独自レイアウトの使い分け

| 対象 | 標準の設定でできること | 独自レイアウトで再利用できる部品 | 独自レイアウトやテンプレートで扱うこと |
| --- | --- | --- | --- |
| ヘッダー | `EnableHeaderSearch`で標準位置の検索を有効にします。 | `SearchFormComponent`はフォーム、`DocsHeaderComponent`は標準のサイト名とメニューを描画します。 | ヘッダーの内容、操作部品の順序、フォームの配置を変えます。 |
| フッター | `SiteCustomization.Footer`でプレーンテキストとリンクを追加します。 | `FooterComponent`に`FooterComponentProps.AdditionalContent`を渡せます。 | 生成元表示やフッターの構造を置き換えます。 |
| ナビゲーション | フロントマターの`sidebar_position`と`sidebar_label`で順序とラベルを設定し、`CollapseTopics`で開閉を有効にします。 | `DocsNavigationComponent`は既存の`SiteTemplateContext.Navigation`ツリーを描画します。 | 別のツリーを渡すか、ナビゲーションの配置や構造を変えます。 |
| ホーム | `ShowTopicCards`、`CompactHome`、`ShowStartLink`で標準のホームページを調整します。 | `DocsPageLayout`は`PageLayoutContent`から本文と任意のサイドバー・目次を受け取ります。 | ホームページの内容や各領域の配置を変えます。 |

型付きページの独自レイアウトには`IPageLayout<TPage>`を使います。テンプレートの出力全体を
組み立てる場合は`ISiteTemplate`を使います。レイアウトの描画時に渡される`context`には、
サイトのテキストとテーマが含まれます。各部品はレイアウト内に配置する`IHtmlContent`を返します。

```csharp
var search = context.Render(new SearchFormComponent(),
    SiteUrl.ForFile("search.html", context.Site.BaseUrl));

var footer = context.Render(new FooterComponent(),
    new FooterComponentProps(context.Site.Title, Docs: true)
    {
        AdditionalContent = new SiteFooterOptions { Text = "See the license for terms of use." }
    });

var sidebar = context.Render(new DocsNavigationComponent(),
    new DocsNavigationComponentProps(navigation, [], currentUrl) { CollapseTopics = true });
```

ここで、`navigation`は`SiteTemplateContext.Navigation`から得た既存のツリー、`currentUrl`は
現在のページを示す検証済みの`SiteUrl`です。ナビゲーション部品は渡された順序を維持します。
検索部品だけでは検索用のファイルを生成しないため、標準のDocs出力と組み合わせる場合は
`EnableSearch`も有効にしてください。単独の検索フォームは1文書に1つだけ置き、検索ページでは
省略します。`DocsHeaderComponent`には検索の配置を指定する引数がなく、トピックカードには
独立した公開部品がありません。標準の配置で足りる場合は、設定を使ってください。

サイト固有の文字組、色、余白には`SiteThemeOptions.AdditionalCss`を使います。
それ以外の配置や構造を変える場合は、組み込みのレイアウトや部品を再利用するか、
`ISiteTemplate`を実装してください。[レイアウトの契約](layout-css-contract.md)も参照できます。
これらの設定はテーマエディターを提供するものではありません。

AngleSharpを使った生成後のHTML処理を削除する場合は、必要な注意書きとリンクを`Footer`へ移し、
必要なDocsの機能を有効にしてください。削除するのはそのHTML処理と、その処理だけを検証していた
テストです。ソースや本文の検査と、配布する出力の検査は残します。独自のHTML処理を残すサイトでは、
その依存関係と検証も引き続き保守する必要があります。[更新手順](migration-2.0.ja.md)と
[生成済み出力の検査](generated-output-checks.ja.md)も参照してください。
