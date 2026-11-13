# 生成サイトの互換性契約

<a id="生成サイト互換性契約"></a>

[English](compatibility-contract.md)

この文書は LithoSharp 3.0.0 の生成サイトの互換性契約を説明します。
1.0.0 で定めた基準をもとにしており、実装の詳細は互換性の保証に含みません。
[3.0 の移行ガイド](migration-3.0.ja.md)はネイティブ YAML と画像処理の変更を説明します。
以前の Testing API の破壊的変更は、生成サイトの互換性とは分け、[2.x の履歴資料](migration-2.0.ja.md)に残しています。

## API の互換性

既存 API は代替 API と移行方法を用意するまで維持します。
非推奨にする場合は、代替 API、移行方法、削除予定のバージョンを明記します。
`SiteGenerator.GenerateAsync`、`MarkdownPostReader`、`SiteCustomization`、`ISiteTemplate` の既存の呼び出し方法を維持します。

公開シグネチャは各ライブラリの `PublicAPI.Shipped.txt` と `PublicAPI.Unshipped.txt` で管理します。
API の変更時には、これらのアナライザーの基準ファイルも更新してください。
CI では公開済みパッケージを基準に NuGet パッケージの互換性も検証します。

2.x では例外として `LithoSharp.Testing.SiteTestDocument.Document` を削除し、読み取り専用の `Snapshot` API へ移行しました。
これは意図したソース・バイナリ互換性の破壊であり、[移行表](Testing-v2-api.ja.md#移行)を用意しています。
任意の API 削除を認めるものではありません。

3.0 では、YamlDotNet の属性とバインドの動作を意図的に置き換え、Skia に依存する画像生成を削除します。
対応する代替手段と制限は、[移行](migration-3.0.ja.md)と[アセット・画像](assets-and-images.ja.md)に記載しています。

<a id="markdownの解析と検証範囲"></a>

## Markdown の解析と検証範囲

runtime とコンパイラーに組み込む Source は、[バージョン一覧](../eng/version-matrix.json)で選んだ固定の Syntamark 2 の組み合わせを使います。
既定のプロファイルは `lithosharp-markdown/1` のままです。
`commonmark/0.31.2` や `syntamark-docs/2` を明示的に選ぶと解釈は変わりますが、別の MDX 本文パイプラインを置き換えるものではありません。
HTML、見出し ID、重複スラッグ、目次、検索文、ルートの方針は LithoSharp が担当します。

原文位置は BOM と改行を含む元の UTF-16 スナップショットに対応します。
ヘッダー専用の走査と選択解析は異なる結果の契約であり、増分解析や全項目の検証を保証するものではありません。
欠けた情報、要求していない情報、取得できない情報を、正常な空の結果として扱いません。
`NoContext` ではプロジェクトのスキーマやルートを確定できず、`Partial` は取得できない情報の妥当性を証明しません。
`Unknown` のカタログ情報から不存在を確定することもできません。
エディターは取得した静的スキーマでバインドし、DTO のコンストラクター、セッター、実行時コールバックを実行しません。
任意の C# 検証には、引き続き明示的に信頼したビルドが必要です。
[Source Generator](source-generators.ja.md)、[言語サーバー](language-server.ja.md)、[品質検査](site-quality.ja.md)を参照してください。

<a id="url-とルート"></a>

## URL の規則

以下の規則は従来の Markdown 投稿、追加ページ、組み込みの Docs/Blog テンプレートに適用します。
型付きページは明示したディレクトリインデックス形式も含め、宣言した `SiteRoute` を使います。
本文に記述したリンクは相対 URL や外部 URL を維持できます。
以下のルート相対 URL の規則は、ジェネレーターが管理するリンクを対象とします。

- 出力パスの公開 URL 区切りには `/` を使い、設定した出力ディレクトリの配下に解決します。
- Canonical URL は `SiteSettings.BaseUrl` と成果物の相対パスを組み合わせます。明示的な `.html` 接尾辞を維持します。
- ジェネレーターが管理するナビゲーションと検索のリンクは、`BaseUrl` のパス部分を含むルート相対 URL です。たとえば `https://example.com/product/` を設定すると、`/product/posts/intro.html` になります。
- Markdown ファイルはコンテンツルートからのディレクトリ構造を維持します。`guides/install.md` は `posts/guides/install.html` になります。
- ソーシャル画像は明示した `CommonSocialImage` または出力ごとの `SocialImages` を使います。Core はルートのハッシュを使う画像を暗黙には生成しません。
- 追加ページでは `SiteExtraPage.RelativePath` を出力パス、canonical URL、ナビゲーションリンク、任意のサイトマップ項目にそのまま使います。

従来の Markdown 投稿と追加ページは、`SitePage<TContent>` に変換できます。
互換用の `DocsRouteConvention` と `BlogRouteConvention` は既存の相対 `.html` 出力パスと `SiteRoute.ForFile` を使います。
ディレクトリインデックス形式へ自動変更することはありません。
Markdown ページの ID は `markdown:{normalized-output-path}`、追加ページの ID は `extra:{normalized-output-path}` です。
ルート型とページ ID 型が区切りと Unicode を正規化するため、ID はホストの OS に依存しません。
これらの変換は従来の公開ジェネレーターとテンプレートの呼び出し方法に対応します。

<a id="公開条件"></a>

Markdown の front matter は、`draft`、`publish_from`、`publish_until`、`environments` も受け付けます。
開始時刻は含み、終了時刻は含みません。環境の一覧が空の場合は、すべての環境を許可します。
生成の開始時に、従来の Markdown 投稿と追加ページを共通ページモデルへ変換し、解決済みのビルド時刻と `SiteGenerationOptions.EnvironmentName` で絞り込みます。
環境名の比較は大文字と小文字を区別せず、既定値は `Production` です。
追加ページには公開条件がなく、既定で公開されます。

公開対象外の Markdown 投稿は、テンプレート入力、Docs のナビゲーションと前後リンク、Blog の一覧・アーカイブ・タグ、検索データ、RSS、サイトマップ、`llms.txt`、投稿別ソーシャル画像に含まれません。
`SiteGenerationResult.PostCount` は、この絞り込み後に残る Markdown 投稿数です。
公開メタデータは絞り込みの前に検証します。
公開対象のページでルートが不正な場合は、ソースパスを含む `InvalidRoute` 診断を返します。
公開対象外のページは生成ルートを持たないため、ルート検証には参加しません。

## ルート値の契約

`LithoSharp.Routing.SiteRoute` は、ジェネレーターが使う不変のルート値です。
有効な従来のルートは公開パスを維持します。
NuGet 0.2.0 からの挙動の変更は、[0.3.0 の移行ガイド](history/migration-0.3.ja.md)を参照してください。

- `ForFile` は、`posts/intro.html` のような相対ルートを、公開パス `/posts/intro.html` と出力パス `posts/intro.html` に対応させます。
- `ForDirectoryIndex` は、`guides/intro` を `/guides/intro/` と `guides/intro/index.html` に対応させます。空のパスまたは `/` はサイトのルートを表し、`index.html` を出力します。
- 絶対 HTTP(S) URL の `baseUrl` は正規化したパス部分だけを公開パスへ加えます。たとえば `https://example.test/product/` は公開パスの先頭に `/product/` を加えます。出力パスには加えません。
- 相対ルートの入力では `/` と `\` を受け付け、連続する区切りをまとめます。公開パスは常に `/` を使い、出力パスもすべての OS で `/` 区切りの相対パスです。
- `baseUrl` のパスは出力ファイル名ではなく URI パスの規則に従います。エスケープ表現と内部の連続区切りを維持するため、`CON`、`docs:v2`、`app;v=1` もそのまま公開パスに使います。リテラルのバックスラッシュ、エンコードされた区切り、ドットセグメント、先頭の連続区切り、不正または曖昧な URI は拒否します。
- 相対ルートでは、リテラルの Unicode と、それに相当する UTF-8 のパーセント表現を同じルートとして扱います。出力セグメントは NFC の Unicode、公開セグメントは正規のパーセント表現を使います。`%20` などの有効な表現は出力ファイル名のために復号し、公開パスでは再度エンコードします。描画する URL は有効な非 ASCII の UTF-8 表現を復号しますが、空白、予約文字、URI で安全でない ASCII のエスケープは維持します。
- ファイルルートは `.html` や `index.html` といった名前を維持します。ファイルルートの末尾の `/` は無効です。ディレクトリインデックス形式は常に公開パスの末尾に `/` を付け、出力パスだけに `index.html` を加えます。
- クエリ、フラグメント、相対または非 HTTP(S) の base URL、不正なパーセント表現、不正な UTF-8 のエスケープ、絶対ルートパス、Windows のルート付きパス、エンコードされた区切り、`.` と `..` のセグメントは、出力パスを公開する前に拒否します。OS をまたいで安全でないファイル名や予約されたファイル名は、出力ファイルを作る相対ルートで拒否します。`baseUrl` のパスセグメントには、このファイル名の規則を適用しません。
- 等値比較とハッシュは、正規化した公開パスと出力パスを、大文字と小文字を区別する序数規則で比較します。ホストのファイルシステムでの大文字と小文字の扱いには依存しません。

ジェネレーターは出力ディレクトリを変更する前にテンプレートの描画を完了します。
公開対象のページ、組み込みテンプレートの成果物、任意の共通成果物、カスタムテンプレートが宣言するすべてのファイルを、一つの `SiteRouteTable` に登録します。
不透明なカスタムテンプレートが物理的な所有権を持つのは、返したファイルだけです。
省略したページも概念上の公開ルートの重複検査に参加しますが、実ファイルの衝突は作りません。
返されたすべてのファイルが `SiteRoute` に解決される必要があります。
不正なファイルは診断して除外したうえで、検証を失敗させます。
重複、大文字と小文字だけの違い、予約済み成果物との衝突、ファイルとディレクトリの祖先衝突は、`SiteRouteValidationException` になります。
この例外は互換性のため `InvalidOperationException` を継承します。
`Diagnostics` は、安定した ID、メッセージ、取得できる定義元の位置を提供します。

<a id="出力"></a>

## Docs の成果物

既定の `DocsSiteTemplate` は次を出力します。

| 成果物 | 規則 |
| --- | --- |
| ホーム | `index.html` |
| ページが見つからない場合にホストから配信するページ | `404.html` |
| Markdown ページ | `posts/{content-relative-path}.html` |
| 追加ページ | `{SiteExtraPage.RelativePath}` |
| スタイルシート | `assets/site.css` |
| スクリプト | `assets/site.js` |
| LLM 向けの要約 | `GenerateLlmsTxt` が有効な場合の `llms.txt` |

Docs の既定出力には `archives.html`、`tags.html`、`search.html`、`search-index.json`、`assets/search.js`、`feed.xml`、`sitemap.xml` は含まれません。
`EnableSearch = true` を明示すると、検索用の成果物を追加します。
ホスト側では、存在しない URL に対して `404.html` を HTTP 404 で配信し、設定した `BaseUrl` のサブパスを扱う必要があります。
このページの生成はホストを設定するものではありません。

## Blog の成果物

明示的に選ぶ `BlogSiteTemplate` は次を出力します。

| 成果物 | 規則 |
| --- | --- |
| ホーム | `index.html` |
| ページが見つからない場合にホストから配信するページ | `404.html` |
| アーカイブ | `archives.html` |
| タグ | `tags.html` |
| 検索ページ | `search.html` |
| 検索データ | `search-index.json` |
| Markdown ページ | `posts/{content-relative-path}.html` |
| 追加ページ | `{SiteExtraPage.RelativePath}` |
| スタイルシート | `assets/site.css` |
| サイトのスクリプト | `assets/site.js` |
| 検索用のスクリプト | `assets/search.js` |
| RSS 2.0 フィード | `feed.xml` |
| サイトマップ | `sitemap.xml` |
| LLM 向けの要約 | `GenerateLlmsTxt` が有効な場合の `llms.txt` |

RSS のチャネルリンクは正規化した絶対 `BaseUrl` です。
各項目の `link` と `guid` は投稿の canonical URL で、投稿の読み取り順で最初の最大 20 件を含めます。

サイトマップは絶対 canonical URL を、ホーム、アーカイブ、タグ、設定順の対象追加ページ、検索、読み取り順の投稿という意味のある順序で含みます。
`lastmod` は、投稿の項目だけに付けます。

`search-index.json` は、`site`、解決済みの生成時刻 `generated`、`documents` を含みます。
各文書の URL はサイトのルート相対パスです。
`search.html` は、インデックス URL に内容のフィンガープリントをクエリとして付けます。
生成時刻とフィンガープリントの値そのものは、リリース間の互換性の対象ではありません。

## 任意の共通成果物

favicon の必要な入力ファイルがすべて揃っている場合は、両テンプレートが次のファイルを `assets/favicon/` 以下へコピーします。

- `favicon.ico`
- `apple-touch-icon.png`
- `android-chrome-192x192.png`
- `android-chrome-512x512.png`
- `favicon-16x16.png`
- `favicon-32x32.png`
- `favicon-48x48.png`
- `favicon-64x64.png`
- `favicon-96x96.png`
- `favicon-128x128.png`
- `favicon-180x180.png`
- `favicon-192x192.png`
- `favicon-256x256.png`
- `favicon-512x512.png`

同じ条件で `site.webmanifest` も出力します。
favicon の探索からソーシャル画像を生成することはありません。
既存画像を明示的に設定するか、任意の PNG 変換と出力記述子を登録してください。

HTML は、設定した共通画像または出力ごとのソーシャル画像を、Open Graph と Twitter Card の絶対 URL で参照します。
画像を出力しない場合は、画像メタデータを省略し、Twitter Card を `summary` にします。
favicon と manifest へのリンクは、favicon の一式が揃った場合だけ出力します。

`clean: false` は、無関係なファイルを保持します。
一方、直前に成功したビルドでジェネレーター所有として記録され、現在の検証済みビルド計画にないファイルは削除します。
決定的な所有権マニフェストは出力とともに原子的に確定し、`SiteGenerationResult.GeneratedFiles` には含めません。
マニフェスト導入前の出力ツリーは、所有権を安全に判定できないため保持します。

## 原子的な出力と移植性の境界

出力トランザクションは、大文字と小文字の違いと Unicode 正規化の違いを保守的にまとめたロック別名を使い、同じユーザーで動作する協調プロセスを直列化します。
所有権サイドカーと回復登録には、これとは別に、解決済みのディレクトリの識別情報をそのまま使います。
このため、NFC と NFD の名前のように物理的に異なるディレクトリが、所有権状態を共有することはありません。
防ぐのは同じユーザー間の協調的な競合であり、特権ユーザーや悪意のある管理者、書き込み権限を持つ別ユーザー、必要な原子的 rename を保証しないファイルシステムは対象外です。
Windows の owner/group/DACL と Unix の permission mode は、可能な範囲で保持します。
Unix の ACL、拡張属性、所有者は、移植性のある保証ではありません。
一部のクリーンアップに失敗した場合は、回復用の古い保留登録が残ることがあります。

型付きローダーの入力エラーは、正規化したソースパスと任意の行・列を持つ構造化診断として返します。
構成やファイルシステムの失敗は、例外として扱います。
ルートとコレクションキーは、NFC 正規化と大文字と小文字を区別する序数規則を使い、出力は `/` 区切りです。
大文字と小文字だけが異なる出力の衝突は変更前に拒否するため、拡張機能の作成者は表記を明示的に選ぶ必要があります。

<a id="html-と公開-api"></a>

## HTML の互換性

互換性の対象は、シリアライズしたバイト列ではなく、意味上の構造です。
空白、字下げ、属性順、無害なラッパーの変更、同等のエスケープ表現は、契約に含みません。

次の項目は契約に含みます。

- 主要なランドマークと、その意味のある順序。
- 組み込みのスタイルやスクリプトが使う、以下の CSS クラスと状態クラス。
- canonical、ナビゲーション、前後リンク、目次、RSS、検索、アセット、投稿へのリンク。
- 適用されるページでの、文書の言語、タイトル、説明、テーマ色、canonical URL、Open Graph、Twitter Card、投稿の公開メタデータ、RSS 検出、favicon・manifest、CSS・スクリプトのメタデータ。
- ナビゲーションラベル、`aria-current`、メニュー操作用 ID、検索の状態表示領域、`rel="prev"` と `rel="next"` などの、アクセシビリティ上の関係。
- Docs のサイドバー順または Blog の投稿順から決まる、本文とナビゲーションの順序。

Docs ページは次の主要な順序を維持します。

1. `header.docs-header`
2. `div.docs-shell`
3. `aside.docs-sidebar`
4. `main.docs-main`
5. `article.docs-content`
6. 任意の `aside.post-toc.docs-toc`
7. `footer.docs-footer`

Docs のナビゲーションは `docs-nav-list`、`docs-nav-folder`、`docs-nav-link`、`is-ancestor`、`is-current` を維持します。
投稿の前後リンクは `docs-pagination`、`docs-pagination-previous`、`docs-pagination-next` を維持します。

Blog ページは次の主要な順序を維持します。

1. `header.site-header`
2. `main`
3. ページ固有の本文
4. `footer.site-footer`

Blog のナビゲーションは `site-nav-shell`、`site-nav`、`site-header-search`、`site-menu-toggle`、`rss-nav-link` を維持します。
投稿ページは `post-layout`、`article.post`、`post-title`、`summary`、`tags`、`post-toc`、`toc-nav`、`toc-list` を維持します。
一覧と検索の構造は `hero`、`eyebrow`、`card`、`archive-row`、`archive-post-list`、`tag-cloud`、`tag-link`、`search-box`、`search-scope`、`search-status`、`search-results` を維持します。

HTML のバイト単位での一致と、変動する生成時刻は、この契約の対象外です。
