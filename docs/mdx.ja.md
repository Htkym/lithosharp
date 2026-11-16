# MDX とドキュメントサイト

`LithoSharp.Mdx` は .NET 10 向けの追加パッケージです。従来の Markdown
サイトには Node.js や React は不要です。MDX のビルドには Node.js 24.13.0、
MDX 3.1.1、React 19.2.4、esbuild 0.28.2 を使います。依存関係は worker の
`package-lock.json` で固定しています。公開先は静的 HTTP ホストだけで構いません。

## サイトを作る

以下は 3.0.0 を対象にした手順です。利用する LithoSharp のパッケージと Tool を 3.0.0 にそろえてください。
[バージョン一覧](../eng/version-matrix.json)は選定した開発候補と正式リリースの提供状況を区別します。

```sh
dotnet new install LithoSharp.ProjectTemplates::3.0.0
dotnet tool install LithoSharp.Tool --version 3.0.0 --tool-path .tools
.tools/lithosharp new mdx MyDocs -o MyDocs
dotnet build MyDocs -c Release
.tools/lithosharp restore-mdx MyDocs/bin/Release/net10.0/worker
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release
```

Windows では `.tools/lithosharp.exe` を使います。復元は、`npm ci --ignore-scripts --no-audit --no-fund` でネットワークを
使う明示的な操作です。通常のビルドは npm パッケージを取得しません。`dotnet build` の段階では、NuGet パッケージを復元する場合があります。
サイトの `node_modules` からパッケージを取り込む場合は、サイト固有の npm 依存も別に復元してください。TypeScript は変換のみを
行うため、型検査にはサイトの TypeScript チェッカーを使います。

テンプレートの `ISiteFactory` が `DocumentationSite` を登録します。
API から登録する場合も同じ設定を使えます。

```csharp
var docs = new DocumentationSite(new MdxOptions(projectDirectory)
{
    Cacheable = true,
    Hydration = "selective"
}) { Browser = new() };
docs.AddCollection(new("guide", [
    new("current", "en", Path.Combine(projectDirectory, "docs"), "guide"),
    new("current", "ja", Path.Combine(projectDirectory, "i18n/ja"), "ja/guide")
]) { UseMdx = true });
var options = new SiteGenerationOptions { Extensions = [docs] };
```

API 利用時は最後のビルド後に拡張を破棄してください。CLI の watch はセッション中に
worker を再利用し、終了時に破棄します。C# の変更時は factory を再コンパイルして
読み込み直します。ビルドに失敗しても直前の公開出力を保持します。

## Markdown と MDX の解析範囲

固定した Syntamark 2 の runtime と Source の組み合わせは、共通の Markdown フロントマターの境界と、
ネイティブの Markdown 経路を提供します。MDX worker の JavaScript・JSX の本文パーサーは置き換えません。
製品の Markdown プロファイルの既定値は、`lithosharp-markdown/1` のままです。
ヘッダーだけの走査は、終端区切り後の本文を除き、UTF-16 コード単位で 1 Mi のヘッダーに対応します。
境界を利用できない場合は、ヘッダーがないという成功結果ではなく、実行時に `LSM013`、明示的なジェネレーター入力に `LSG002` を出します。

エディターの Markdown のアウトラインは、シンボルの全範囲に Syntamark の節、選択範囲に見出しを使います。
MDX は別の worker の見出し経路を持ち、シンボルには Markdown の節の部分木ではなく見出し範囲を使います。
どちらも入力のたびに独自の C# スキーマコードを実行しません。
`NoContext` はプロジェクトに結び付く検査が利用できない状態、`Partial` は利用できる解析が不完全な状態です。
[言語サーバーのガイド](language-server.ja.md)を参照してください。

C# スキーマと生成参照の一連の例には、ネイティブ Markdown の [Docs サンプル](../samples/LithoSharp.DocsSample/README.ja.md)を使います。
そのコンパイラー入力の契約は MDX・JavaScript の自動的な型検査器ではありません。

## コンテンツを書く

`MdxContentCollectionLoader<T>` は `.mdx` を読みます。`.md` を MDX として読む
場合は `IncludeMarkdown` を指定します。Docs プリセットでは `UseMdx = true` が
この指定を含みます。名前が `_` で始まるファイルとディレクトリは import 専用です。
front matter は C# の strict binder で検証し、不明なキーや重複を診断します。

`DocumentFrontMatter` では `id`、`slug`、タイトル、説明、サイドバー、タグ、
公開期間、`draft`、`unlisted`、`search_exclude`、タイトルと目次の表示、編集リンク、
前後ページを指定できます。

```mdx
---
title: 操作できるページ
---
import Counter from './_components/Counter.tsx';

# 操作できるページ

この本文は JavaScript が動く前から表示されます。

<Counter initial={3} />
```

JS、JSX、TS、TSX、JSON、CSS、CSS modules、画像、`?raw` import は esbuild で
処理します。import は宣言したプロジェクトまたは復元済み worker の範囲に限ります。
公開ページから draft や未登録の文書を import することはできません。共有する内容は
`_` で始まる partial へ移してください。MDX の export は JavaScript の意味を保ちます。
公開するブラウザーデータは、選んだものだけです。

対応するインポートは、`@theme/Tabs`、`@theme/TabItem`、`@theme/Admonition`、`@theme/Details`、
`@theme/CodeBlock`、`@theme/TOCInline`、`@theme/Card`、`@theme/DocCardList`、`@theme/IdealImage`、
`@theme/ThemedImage`、`@theme/Heading`、`@theme/BrowserOnly`、`@docusaurus/BrowserOnly` です。
`@theme/MDXComponents` は、独自の MDX プロバイダー用の実行時コンポーネントマップに解決します。
描画できるこれらのコンポーネントと、`Link`、`Zoom`、`Translate`、`Highlight`、`TweetQuote`、`Code` は、
実行時コンポーネントマップの、修飾しない JSX 名でも解決します。
`@docusaurus/router` と `@docusaurus/plugin-content-docs/client` のフックは、同梱コンポーネントのための
静的なフォールバック値を使います。出力を確認するか、呼び出しを `usePageContext` に書き換えてください。
`useBaseUrl` と `useDocusaurusContext` はありません。
代わりに、`@lithosharp/runtime` の `usePageContext` から `basePath`、言語、メッセージを読みます。
トップレベルの ` ```mdx-code-block ` フェンスは実行可能な MDX を囲みます。[移行ガイド](docusaurus-migration.ja.md)を参照してください。
プリセットは、GFM、見出しアンカー、ディレクティブの注意書き、Prism コード、KaTeX の数式、Mermaid を提供します。
ブラウザー専用の動的インポートとコード取り込みも含む実行例は、`samples/LithoSharp.MdxSample/content` にあります。
未対応の Docusaurus 別名は明示的に失敗し、任意の Docusaurus プラグインや設定の実行は未対応です。

## Docusaurus 互換性

対応する互換範囲の対象は、Docusaurus 3.10.2、MDX 3.1.1、React 19.2.4 です。
任意の Docusaurus プラグインと設定は未対応です。変換の制限は、[移行ガイド](docusaurus-migration.ja.md)を参照してください。

| 構文・部品 | 判定 | 詳細 |
|---|---|---|
| `Tabs`, `TabItem` | 対応 | サーバーで初期タブを描画し、noscript の代替を持つ状態付きタブ。明示した `Island` 内以外では、ページの hydration が必要。 |
| `Admonition` | 対応 | 静的に使える。Markdown の `:::` ディレクティブも同じ aside の構造を描画する。 |
| `Details` | 対応 | 静的に使える。 |
| `CodeBlock` | 対応 | フェンス付きコードは、共通のタイトル・強調・行番号のメタデータで静的に描画する。コピー操作はブラウザーで hydration する。単独の `<CodeBlock>` はページの hydration が必要。 |
| `Card` | 対応 | リンク付きのタイトルを持つ静的なカード。hydration なしで描画する。 |
| `DocCardList` | 対応 | 属性のない `<DocCardList />` は、同じディレクトリのカードをタイトルと説明付きでコンパイル時に描画する。属性付きは、明示的に失敗する。 |
| `MDXComponents` | 一部対応 | 独自 MDX プロバイダー用の実行時コンポーネントマップの default export に解決する。描画可能なコンポーネントではない。 |
| `TOCInline` | 対応 | 静的に使える。worker の見出しから作るページの `toc` export を受け取り、既定ではレベル 2–3。 |
| `Link` | 対応 | 修飾しないコンポーネントだけに対応し、`@docusaurus/Link` のインポートパスはない。MDX の `a` 要素と修飾しない使用はこれに解決し、安全でないスキームの `javascript:`・`vbscript:`・`data:` と未解決の `xref:` はビルド時の描画に失敗する。未移行コンテンツの `to` 属性は `href` の別名。Markdown 経路は CommonMark の宛先を変更せず描画する。 |
| `Zoom` | 対応 | インポートパスのない、修飾しない画像ズームのラッパー。ズーム操作なしで子を描画する。 |
| `BrowserOnly` | 対応 | `@docusaurus/BrowserOnly` または `@theme/BrowserOnly` からインポートし、静的に fallback を描画する。 |
| `Translate` | 対応 | 修飾しないコンポーネントだけに対応し、`@docusaurus/Translate` のパスはない。翻訳カタログのメッセージか、代替として子を描画する。 |
| `IdealImage` | 対応 | 静的な画像をそのまま渡す。レスポンシブのバリアントは基本画像を描画する。 |
| `ThemedImage` | 対応 | ライトのソースを静的に描画する。色モードの切り替えには hydration が必要。 |
| `Heading` | 対応 | アンカーを自動生成せず、要求した見出しレベルで静的に描画する。 |
| `Highlight`, `TweetQuote`, `Code` | 対応 | サイトの互換マップにある、修飾しないグローバル名。 |
| `@docusaurus/router`, `plugin-content-docs/client` のフック | 一部対応 | SSR で使える静的な代替値で、空の location と現在の version。出力を確認するか、`usePageContext` に書き換える。 |
| `useBaseUrl` | 未対応 | export はない。`@lithosharp/runtime` の `usePageContext` から `basePath` を読む。 |
| `useDocusaurusContext` | 未対応 | export はない。`@lithosharp/runtime` の `usePageContext` を使う。 |
| ほかの `@theme/*` | 未対応 | `Unsupported Docusaurus alias` でビルドが失敗する。 |
| ほかの `@docusaurus/*` | 未対応 | ビルドで解決できず、移行レポートは `LSMIG002` を付ける。 |

ネイティブ変換では、フロントマターは厳密な snake_case キーを使う `DocumentFrontMatter` に対応し、未知・重複キーを診断します。
ルート、サイドバー、カテゴリ、前後のリンクは、`SidebarItem` と `DocumentCategory` を持つ `DocumentCatalog` に対応します。
バージョンと言語は `DocumentVariant`、UI メッセージは言語別の入力ディレクトリを使う `TranslationCatalog` に対応します。
アセットはプロジェクトの範囲を制限する既存のアセット経路を使います。
CommonMark と GFM はプリセットの経路、Litho Markdown の拡張は共通の注意書き・コードメタデータの構造、上表は Docusaurus の解釈層です。
任意のプラグイン・テーマ、一覧外のテーマ上書き、JavaScript 設定の実行は未対応です。
移行は設定を実行せず、`LSMIG001` と `LSMIG003` で報告します。

## Hydration と公開データ

既定値はページ単位の hydration です。React のサーバー描画が最初のツリーを待ち、ブラウザーの
`hydrateRoot` が同じ props と ID prefix を使って引き継ぎます。C# は外側のレイアウトを担当します。
React が管理する DOM を別のスクリプトで書き換えないでください。
`BrowserOnly` は静的 fallback と遅延 import を使えるため、読み込み時に `window` を
参照する部品も分離できます。

`MdxPublicData` は JSON と明示的な schema を受け取ります。オブジェクトには
`additionalProperties: false` が必要です。サービス、関数、内部設定、認証情報を
渡さず、ブラウザーで必要な値だけを選んでください。
Island の属性にも同じ制限付きの JSON スキーマ規則を適用します。

Selective hydration では Island を明示します。`Island` は既定の部品に含まれます。

```mdx
import Counter from './_components/Counter.tsx';

<Island component={Counter} props={{initial: 3}}
  schema={{type: 'object', properties: {initial: {type: 'integer'}}, additionalProperties: false}}
  strategy="visible" />
```

起動条件は `load`、`idle`、`visible`、`media`、`manual` です。`media` には
media query を指定します。`manual` は `detail: { id }` を持つ
`lithosharp:hydrate` イベントで起動します。安全に分離できない部品や共有 Context は
ページ単位に戻します。静的なページには MDX の hydration entry を生成しません。
同じ `MdxSite` の共有依存は共通 chunk になります。`StaticComponents` は作者による
静的動作の宣言であり、自動的に安全性を証明する設定ではありません。

## Docs、検索、拡張

文書は集合、バージョン、言語、安定 ID で識別します。variant を明示的に登録し、URL prefix、
表示名、バナー、noindex、RTL、切替先がない場合の文書 ID を設定します。
自動、手動、混在サイドバーは、パンくずと前後リンクの情報を共有します。
`_category_.json` または YAML でカテゴリの順序、表示名、初期展開、導入ページを
指定できます。unlisted は直接アクセスできますが、ナビゲーション、検索、タグ、フィード、サイトマップには出しません。

`GitMetadata = true` は Git から最終 commit の日時と著者を取得します。
未 commit のファイルには Git の更新情報を付けません。front matter の
`last_update` にある `date` と `author` が優先されます。
Git が必要なのは、このオプションを使う場合だけです。
`SourceLocale` と `MissingDocuments` で未翻訳文書の扱いを設定できます。
除外時は不足 ID を inspect に記録し、エラー方針では公開前に `LSDOC001` で停止します。
本文を自動コピーして翻訳済みとして扱うことはありません。

`TranslationCatalog` は C# と React の UI 文言を共有し、原文、除外、エラーの
未翻訳方針を指定できます。翻訳本文と画像は言語別の入力ディレクトリに置きます。
alternate link は公開された対応文書だけを指します。ローカル検索は集合、バージョン、言語で
分割します。MDX 文書の本文には worker が抽出した静的テキストを使い、利用できない場合は
描画後の記事テキストを使います。Markdown の本文とすべての検索用の節にも、描画後の記事テキストを使います。
Core のサイト索引では MDX の本文にも
描画後の記事テキストを使います。外部検索には明示設定した Algolia の公開検索専用キーを使えます。

`MdxBlogSite` は複数 Blog、著者、タグ、月別一覧、ページ分割、RSS、Atom、JSON Feed
に対応します。抜粋と feed は明示的なテキストを使い、React の木を途中で切りません。
独立した React ページも MDX wrapper から import して同じ描画経路へ登録できます。

混在サイトでは `DocumentationSite.AddBlog` と `AddPages` を使い、拡張を一度だけ
登録してください。Docs、Blog、独立ページで compiler graph と React chunk を共有します。
別々の `MdxSite` は別 graph なので、同じ出力の資産領域へ重ねて登録しないでください。

`ISiteBuildExtension.PrepareAsync` は型付きコレクションと資産を共通のビルドへ
登録します。依存と所有者を宣言し、公開先へ直接書き込まないでください。
レイアウトは `IPageLayout<PageLayoutContent>`、部品の差し替えは
`ComponentsModule`、Remark/Rehype 拡張は `MdxPlugin` を使います。
プラグインのインポートは依存関係のフィンガープリントに参加します。
動的に読むファイルは `DeclaredInputFiles` に追加します。

`AfterBuildAsync` は公開完了後に登録順で呼ばれ、no-op build も通知します。
所有する出力を変更するフックではありません。公開後の通知なので、このフックの例外は
確定済みの公開結果を巻き戻しません。

`inspect` は worker のバージョン、依存、chunk、公開 props、hydration の選択と実行数を表示します。
CLI はバージョンの snapshot、翻訳キーの抽出、依存復元、Docusaurus 移行診断も扱います。
正確な引数は `lithosharp --help` を参照してください。移行診断は JavaScript 設定を
実行せず、不明な構文をレポートします。

`ApiReferenceSite` は XML コメントと OpenAPI 3 JSON を共通ページへ変換し、
正確な member ID による参照と選択したバージョンの差分を生成します。
外部 OpenAPI 参照を自動取得することはありません。

信頼するプロジェクトを復元した後、`DocumentationVerification.BuildApiAsync` を明示的に呼び、
必要な XML を選んで `AddXml` に渡せます。コード例は既存の Microsoft.Testing.Platform の
テストプロジェクトを `DocumentationVerification.VerifyExamplesAsync` で検証し、
`CodeBlock` で同じコード領域を取り込めます。どちらもプロジェクトのコードを実行します。
暗黙の制限時間はなく、呼び出し元からのキャンセルに従います。
依存の復元や MDX ビルドからの自動実行は行いません。

## ブラウザーの追加機能

`DocumentationBrowserOptions` でナビゲーション、テーマの保存、検索、告知と
高速遷移を設定します。通常のリンクと本文は JavaScript がなくても使えます。
遷移時は React root を破棄し、head、履歴、フォーカスを更新します。
必要なリソースを読み込めない場合は、通常のページ移動に戻ります。

`Offline = true` は service worker と webmanifest を有効にします。service worker は新しいバージョンの
HTML と資産をそろえてから有効化します。出力を一括配置し、古いページを開いている
利用者のために古い hashed asset を一定期間保持してください。HTTPS または localhost
が必要です。

ライブコードは sandbox 付き iframe で実行し、静的コードを fallback として残します。
React と `render` を提供しますが、ホスト文書で任意の MDX import や JSX 変換を
実行する機能ではありません。アクセス解析は既定で無効です。`lithosharp:consent` の
`detail: { granted: true }` を受けるまで送信せず、撤回後は停止します。
通常の移動と拡張した移動は、重複を除いた同じページビューの経路を使います。

この同意制御は`DocumentationBrowserOptions.Analytics`の機能です。
`SiteSettings.GoogleAnalyticsMeasurementId`または環境変数`GA_MEASUREMENT_ID`・
`GOOGLE_ANALYTICS_MEASUREMENT_ID`が有効にする従来のGoogle Analytics snippetは制御しません。
文書サイトの同意制御を使う場合は、これらを未設定にしてください。

## 信頼境界と再現性

MDX、React module、compiler plugin はビルド権限で動くコードです。worker 自体は
sandbox ではありません。信頼できないリポジトリは制限した別環境で実行してください。
環境変数は明示した値と実行に必要なものに絞り、`NODE_OPTIONS` や実行パスの上書きを
拒否します。

`Cacheable` は動的入力を宣言できるコードでだけ有効にしてください。比較時はビルド
時刻を固定します。キャッシュは入力 hash を検証し、変更がなければ compile、render、
bundle を省きます。部品変更後のブラウザーバンドルは全 entry を処理する場合があります。
.NET の割当量に Node のメモリは含まれません。公開する Node heap 値は処理後の値であり、
最大 RSS ではありません。

`MdxOptions.Timeout` は worker への 1 要求あたり既定で 2 分です。大量の文書を扱う場合は
明示的に延長してください。

`LSMDX003` は worker 応答や依存の不整合、`LSMDX005` は処理中の入力変更、
`LSMDX006` は禁止した文書 import を示します。診断は front matter を含む元の行へ
戻します。timeout、cancel、worker 異常終了時には部分出力を公開しません。
依存更新後は明示的な復元と再検証が必要です。バンドルした依存の第三者通知も生成します。
