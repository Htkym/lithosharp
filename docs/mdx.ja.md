# MDX とドキュメントサイト

`LithoSharp.Mdx` は .NET 10 向けの追加パッケージです。従来の Markdown
サイトには Node.js や React は不要です。MDX のビルドには Node.js 24.13.0、
MDX 3.1.1、React 19.2.4、esbuild 0.25.12 を使います。依存関係は worker の
`package-lock.json` で固定しています。公開先は静的 HTTP ホストだけで構いません。

## サイトを作る

```sh
dotnet new install LithoSharp.ProjectTemplates::0.3.1
dotnet tool install LithoSharp.Tool --version 0.3.1 --tool-path .tools
.tools/lithosharp new mdx MyDocs -o MyDocs
dotnet build MyDocs -c Release
.tools/lithosharp restore-mdx MyDocs/bin/Release/net10.0/worker
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release
```

Windows では `.tools/lithosharp.exe` を使います。`restore-mdx` はネットワークを
使う明示的な復元操作です。通常のビルドは npm パッケージを取得しません。
サイト固有の npm 依存は、サイト側でも復元してください。TypeScript は変換のみを
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

Tabs、TabItem、Admonition、Details、CodeBlock、TOCInline、Link、BrowserOnly、
Translate などの互換部品を用意しています。GFM、見出しアンカー、Prism、KaTeX、
Mermaid も利用できます。正確な alias と実行例は [英語版](mdx.md) と
`samples/LithoSharp.MdxSample/content` を参照してください。任意の Docusaurus
plugin や設定コードの実行には対応しません。不明な alias はエラーにします。

## Hydration と公開データ

既定値はページ単位の hydration です。React がビルド時に本文を描画し、ブラウザーで
同じ props と ID prefix を使って引き継ぎます。C# は外側のレイアウトを担当します。
React が管理する DOM を別のスクリプトで書き換えないでください。
`BrowserOnly` は静的 fallback と遅延 import を使えるため、読み込み時に `window` を
参照する部品も分離できます。

`MdxPublicData` は JSON と明示的な schema を受け取ります。オブジェクトには
`additionalProperties: false` が必要です。サービス、関数、内部設定、認証情報を
渡さず、ブラウザーで必要な値だけを選んでください。

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

文書は集合、版、言語、安定 ID で識別します。variant を明示的に登録し、URL prefix、
表示名、バナー、noindex、RTL、切替先がない場合の文書 ID を設定します。
自動、手動、混在サイドバーは、パンくずと前後リンクの情報を共有します。
`_category_.json` または YAML でカテゴリの順序、表示名、初期展開、導入ページを
指定できます。unlisted は直接アクセスできますが、一覧や検索、feed には出しません。

`GitMetadata = true` は Git から最終 commit の日時と著者を取得します。
未 commit のファイルには Git の更新情報を付けません。front matter の
`last_update` にある `date` と `author` が優先されます。
`SourceLocale` と `MissingDocuments` で未翻訳文書の扱いを設定できます。
除外時は不足 ID を inspect に記録し、エラー方針では公開前に `LSDOC001` で停止します。
本文を自動コピーして翻訳済みとして扱うことはありません。

`TranslationCatalog` は C# と React の UI 文言を共有し、原文、除外、エラーの
未翻訳方針を指定できます。翻訳本文と画像は言語別の入力ディレクトリに置きます。
alternate link は公開された対応文書だけを指します。ローカル検索は集合、版、言語で
分割し、描画後の本文と見出しを索引にします。外部検索には明示設定した Algolia の
公開検索専用キーを使えます。

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
動的に読むファイルは `DeclaredInputFiles` に追加します。

`AfterBuildAsync` は公開完了後に登録順で呼ばれ、no-op build も通知します。
所有する出力を変更するフックではありません。公開後の通知なので、このフックの例外は
確定済みの公開結果を巻き戻しません。

`inspect` は worker 版、依存、chunk、公開 props、hydration の選択と実行数を表示します。
CLI は版の snapshot、翻訳キーの抽出、依存復元、Docusaurus 移行診断も扱います。
正確な引数は `lithosharp --help` を参照してください。移行診断は JavaScript 設定を
実行せず、不明な構文をレポートします。

`ApiReferenceSite` は XML コメントと OpenAPI 3 JSON を共通ページへ変換し、
正確な member ID による参照と選択した版の差分を生成します。
外部 OpenAPI 参照を自動取得することはありません。

信頼するプロジェクトを復元した後、`DocumentationVerification.BuildApiAsync` を明示的に呼び、
必要な XML を選んで `AddXml` に渡せます。コード例は既存の Microsoft.Testing.Platform の
テストプロジェクトを `DocumentationVerification.VerifyExamplesAsync` で検証し、
`CodeBlock` で同じコード領域を取り込めます。どちらもプロジェクトのコードを実行し、
10 分でタイムアウトします。依存の復元や MDX ビルドからの自動実行は行いません。

## ブラウザーの追加機能

`DocumentationBrowserOptions` でナビゲーション、テーマの保存、検索、告知と
高速遷移を設定します。通常のリンクと本文は JavaScript がなくても使えます。
遷移時は React root を破棄し、head、履歴、フォーカスを更新します。

`Offline = true` は service worker と webmanifest を有効にします。新しい版の
HTML と資産をそろえてから有効化します。出力を一括配置し、古いページを開いている
利用者のために古い hashed asset を一定期間保持してください。HTTPS または localhost
が必要です。

ライブコードは sandbox 付き iframe で実行し、静的コードを fallback として残します。
React と `render` を提供しますが、ホスト文書で任意の MDX import や JSX 変換を
実行する機能ではありません。解析は既定で無効です。`lithosharp:consent` の
`detail: { granted: true }` を受けるまで送信せず、撤回後は停止します。

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
明示的に延長してください。10,000 ページの性能試験では 15 分を指定しています。

`LSMDX003` は worker 応答や依存の不整合、`LSMDX005` は処理中の入力変更、
`LSMDX006` は禁止した文書 import を示します。診断は front matter を含む元の行へ
戻します。timeout、cancel、worker 異常終了時には部分出力を公開しません。
依存更新後は明示的な復元と再検証が必要です。バンドルした依存の第三者通知も生成します。

再検証には worker の `npm test`、.NET 統合試験、`eng/Test-Templates.ps1`、
MDX サンプルを 4317 番ポートで配信した状態のブラウザー試験を使います。
CI 設定を追加したことと、各 OS で実際に成功したことは区別してください。
