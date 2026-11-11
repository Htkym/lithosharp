# 生成サイト互換性契約

[English](compatibility-contract.md)

この文書はLithoSharp 2.2.0で維持する生成サイトと公開APIの互換性の基準です。1.0.0で定めた基準をもとにしています。生成サイトの互換性と、2.xにおけるTesting APIの破壊的変更は区別します。[更新手順](migration-2.0.ja.md)も参照してください。
実装の詳細は互換性の保証には含みません。
HTML の空白や属性順など、意味を変えないシリアライズ上の差異は契約に
含みません。

NuGet 0.2.0からの挙動の変更は[移行ガイド](history/migration-0.3.ja.md)を参照してください。

## Markdownの解析と検証範囲

runtimeとコンパイラーに組み込むsourceは、固定したSyntamark 1.2.0のペアを使います。既定のprofileは`lithosharp-markdown/1`のままです。この依存更新でCommonMarkを選択したり、別のMDX本文解析を置き換えたりはしません。HTML、見出しID、重複slug、目次、検索文、ルートの規則はLithoSharpが担当します。

原文位置はBOMと改行を含む元のUTF-16 snapshotに対応します。header専用走査と必要な情報だけの解析は結果の契約が異なり、差分解析や全項目の検証を保証するものではありません。欠けた情報、要求していない情報、取得できない情報を、正常な空の結果として扱いません。`NoContext`ではprojectのスキーマやルートを確定できず、`Partial`は取得できない情報の妥当性を証明しません。`Unknown`のcatalog情報から不存在を確定することもできません。独自C#スキーマはコンパイルとサイト生成で検証し、言語サーバーのsnapshot bindingでは検証しません。[Source Generator](source-generators.md)、[言語サーバー](language-server.md)、[品質検査](site-quality.md)を参照してください。

## URL とルート

- 出力パスと公開 URL は `/` 区切りです。出力は指定した出力ディレクトリ
  配下に解決されます。
- `SiteRoute.ForFile("posts/intro.html")` は公開パス
  `/posts/intro.html` と出力パス `posts/intro.html` を作ります。
- `SiteRoute.ForDirectoryIndex("guides/intro")` は公開パス
  `/guides/intro/` と出力パス `guides/intro/index.html` を作ります。
- `BaseUrl` は絶対 HTTP(S) URL のパス部分だけを公開パスへ加えます。
  出力パスには加えません。
- 相対ルートでは `/` と `\` を受け付け、連続する区切りをまとめます。
  Unicode は NFC に正規化し、Unicode の UTF-8 パーセント表現も同じ
  ルートとして扱います。
- 絶対パス、`.` と `..`、不正なパーセント表現、エンコードされた区切り、
  クエリやフラグメント、OS で安全でない名前は拒否します。
  `BaseUrl` の URI パスもドットセグメントや曖昧な入力を拒否します。
- 大文字と小文字だけが異なる出力パス、予約済み成果物との衝突、
  ファイルとディレクトリの祖先衝突は、生成前に診断されます。

生成器はページ、組み込み成果物、任意成果物、カスタムテンプレートの
出力を `SiteRouteTable` へ登録してから出力を変更します。問題があれば
`SiteRouteValidationException` をスローし、`Diagnostics` に安定した ID、
メッセージ、定義元の位置を含めます。

既存の Markdown 投稿ではコンテンツルートからの相対パスを維持します。たとえば
`guides/install.md` は `posts/guides/install.html` になります。追加ページは
`SiteExtraPage.RelativePath` を出力パスに使います。`DocsRouteConvention` と
`BlogRouteConvention` も既存の `.html` 形式を維持し、ディレクトリインデックス形式へ
自動変更しません。ページ ID の接頭辞は Markdown が `markdown:`、追加ページが
`extra:` で、後ろに正規化した出力パスを続けます。

`SiteRoute.ForDirectoryIndex` の空のパスまたは `/` はサイトのルートを表し、
`index.html` を出力します。ファイルルートの末尾の `/` は無効です。
相対ルートの出力ファイル名は NFC に正規化し、公開パスでは正規のパーセント表現を使います。
HTML に描画する URL は有効な非 ASCII の UTF-8 表現を復号しますが、空白や予約文字の
エスケープは維持します。ルートの等値比較は大文字と小文字を区別する序数比較です。

`BaseUrl` のパスは出力ファイル名ではなく URI として扱います。内部の連続区切りや
エスケープ表現を維持し、`CON`、`docs:v2`、`app;v=1` などもそのまま公開パスに使います。
ただし、先頭の連続区切り、バックスラッシュ、エンコードされた区切り、ドットセグメント、
不正または曖昧な URI は拒否します。

不透明なカスタムテンプレートは返したファイルだけの物理的な所有権を持ちます。
省略したページも公開ルートの重複検査には参加しますが、実ファイルの衝突は作りません。
返されたファイルのルートが不正な場合は診断し、検証を失敗させます。
`SiteRouteValidationException` は互換性のため `InvalidOperationException` を継承します。

## 公開条件

Markdown の `draft`、`publish_from`、`publish_until`、`environments` は
出力前に評価されます。開始時刻は含み、終了時刻は含みません。
`environments` の空の一覧または未指定は全環境を許可し、既定の環境名は
`Production` です。環境名の比較は大文字と小文字を区別しません。

公開対象外の投稿はテンプレート入力、Docs ナビゲーション、Blog の一覧・
アーカイブ・タグ、ページ内の前後リンク、検索、RSS、サイトマップ、
`llms.txt`、投稿別ソーシャル画像のいずれにも含まれません。
`SiteGenerationResult.PostCount` は公開対象となった Markdown 投稿数です。
公開メタデータはフィルター前に検証します。公開対象外のページは生成ルートを持たないため、ルート検証には参加しません。追加ページには公開条件がなく、既定で公開されます。

## 出力

Docs テンプレートは `index.html`、`posts/{path}.html`、CSS、JavaScript、
必要に応じて `llms.txt` を出力します。Blog テンプレートはこれに加えて
一覧、アーカイブ、タグ、検索、RSS、サイトマップを出力します。
Canonical URL、サイト内リンク、検索データ、RSS、サイトマップは
`BaseUrl` のパスを含む正規化済みルートを使います。

Docs の既定出力には検索、アーカイブ、タグ、RSS、サイトマップは含まれません。
`EnableSearch = true` で `search.html`、`search-index.json`、`assets/search.js` を追加します。
Blog は `archives.html`、`tags.html`、検索の3ファイル、`feed.xml`、`sitemap.xml` を出力します。
両テンプレートの CSS と JavaScript は `assets/site.css` と `assets/site.js` です。

RSS のチャネルリンクは正規化した絶対 `BaseUrl`、各項目の `link` と `guid` は
投稿の canonical URL です。投稿の読み取り順で最大20件を含めます。
サイトマップはホーム、アーカイブ、タグ、設定順の対象追加ページ、検索、読み取り順の
投稿を含み、`lastmod` は投稿だけに付けます。
検索データには `site`、解決済みの生成時刻 `generated`、`documents` を含めます。
検索ページはインデックス URL に内容のフィンガープリントをクエリとして付けます。
生成時刻やフィンガープリントの値そのものはリリース間の互換性対象ではありません。

favicon の必要な入力がすべて揃っている場合は `assets/favicon/` 以下へコピーし、
`site.webmanifest` と対応する HTML のリンクを生成します。必要なファイルは
`favicon.ico`、`apple-touch-icon.png`、`android-chrome-192x192.png`、
`android-chrome-512x512.png`、および `favicon-{size}x{size}.png` です。
`size` は 16、32、48、64、96、128、180、192、256、512 の全サイズが必要です。
`android-chrome-192x192.png` があれば、既定のソーシャル画像
`assets/social/og-default.png` と投稿別の画像を生成します。投稿別画像のパスは
`assets/social/posts/{hash}.png` で、`hash` は正規化したページ出力ルートの
小文字の SHA-256 です。

ソーシャル画像を生成しない場合は、Open Graph と Twitter Card の画像メタデータを
省略し、Twitter Card を `summary` にします。存在しない画像を指す旧版の挙動は維持しません。

`clean: false` は無関係なファイルを保持します。一方、直前に成功した生成で
ジェネレーター所有として記録され、現在の検証済みビルド計画にないファイルは削除します。
決定的な所有権マニフェストは出力とともに原子的に確定し、`GeneratedFiles` には
含めません。マニフェスト導入前の出力ツリーは所有権を安全に判定できないため保持します。

## 原子的な出力と移植性の境界

出力トランザクションは正規化した出力識別子を使い、同じユーザーで動作する
協調プロセスを直列化します。これは同じユーザー間の協調的な競合を防ぐものであり、
特権ユーザーや悪意のある管理者、書き込み権限を持つ別ユーザー、原子的な rename
を保証しないファイルシステムへの防御ではありません。Windows の owner/group/DACL
と Unix の permission mode は可能な範囲で保持しますが、Unix の ACL、拡張属性、
所有者は移植性のある保証ではありません。クリーンアップに失敗した場合は、回復用の
保留登録が残ることがあります。

同一ユーザー内の協調プロセスは大文字小文字と Unicode 正規化を保守的にまとめた
ロック別名で直列化します。所有権サイドカーと回復登録には、これとは別に解決済みの
出力ディレクトリ名をそのまま使います。このため、NFC と NFD のように物理的に異なる
ディレクトリが所有権状態を共有することはありません。

型付きローダーの入力エラーは正規化されたソースパスと任意の行・列を持つ構造化
診断として返します。構成やファイルシステムの失敗は例外として扱います。ルートと
コレクションキーは NFC 正規化後に序数、大文字と小文字を区別して比較し、出力は
常に `/` 区切りです。拡張機能では大文字・小文字の方針を明示してください。
大文字と小文字だけが異なる出力衝突は変更前に拒否されます。

## HTML と公開 API

Docs と Blog の主要なランドマーク、CSS クラス、アクセシビリティ属性、
ナビゲーション、目次、前後リンク、メタデータは互換性の対象です。

Docs の主要な順序は `header.docs-header`、`div.docs-shell` 内の
`aside.docs-sidebar` と `main.docs-main`、本文の `article.docs-content`、
任意の `aside.post-toc.docs-toc`、`footer.docs-footer` です。
ナビゲーションは `docs-nav-list`、`docs-nav-folder`、`docs-nav-link`、
`is-ancestor`、`is-current` を維持します。前後リンクは `docs-pagination`、
`docs-pagination-previous`、`docs-pagination-next` を維持します。

Blog の主要な順序は `header.site-header`、`main` とページ固有の本文、
`footer.site-footer` です。ナビゲーションは `site-nav-shell`、`site-nav`、
`site-header-search`、`site-menu-toggle`、`rss-nav-link` を維持します。
投稿は `post-layout`、`article.post`、`post-title`、`summary`、`tags`、
`post-toc`、`toc-nav`、`toc-list` を維持します。一覧と検索は `hero`、
`eyebrow`、`card`、`archive-row`、`archive-post-list`、`tag-cloud`、
`tag-link`、`search-box`、`search-scope`、`search-status`、`search-results` を維持します。

言語、タイトル、説明、テーマ色、canonical URL、Open Graph、Twitter Card、
投稿の公開メタデータ、RSS 検出、favicon・manifest、CSS・スクリプトの参照は、
適用されるページで互換性の対象です。ナビゲーションラベル、`aria-current`、
メニュー操作用 ID、検索の状態表示、`rel="prev"` と `rel="next"` などの
アクセシビリティ上の関係も維持します。HTML のバイト単位での一致は保証しません。

## API の互換性

既存 API は代替 API と移行方法を用意するまで維持します。非推奨にする場合は、
代替 API、移行方法、削除予定のバージョンを明記します。
`SiteGenerator.GenerateAsync`、`MarkdownPostReader`、`SiteCustomization`、
`ISiteTemplate` の既存の呼び出し方法を維持します。

公開シグネチャは各ライブラリの `PublicAPI.Shipped.txt` と `PublicAPI.Unshipped.txt` で
管理します。API の変更時にはアナライザーの基準ファイルも更新してください。
CI では公開済みパッケージを基準に NuGet パッケージの互換性も検証します。

2.xでは例外として`LithoSharp.Testing.SiteTestDocument.Document`を削除し、読み取り専用の`Snapshot` APIへ移行しました。これは意図したソース・バイナリ互換性の破壊であり、[移行表](Testing-v2-api.md#migration)を用意しています。任意のAPI削除を認めるものではありません。
