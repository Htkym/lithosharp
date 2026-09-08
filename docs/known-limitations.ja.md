# 既知の制約

[English](known-limitations.md)

0.3.0の制約です。測定条件は[性能](performance.ja.md)、設定と実行規則は[MDX](mdx.ja.md)を参照してください。

## 生成コストと検証環境

10,000ページのMDX corpusでは、coldが274.30秒、no-opが105.13秒、本文1件の変更が213.39秒でした。
1件変更ではcompileとrenderが各1件でも、interactive entryを2,000件bundleしています。
10ケースを通したprocess treeの同時working-set観測最大値は11.807 GiBでした。
最低限必要なRAMでも、単一cold buildのpeakでもありません。no-opでも入力と出力を検証します。
`MdxOptions.Timeout`は1要求あたり既定2分で、このcorpusでは15分に延長しました。

性能測定はWindowsのみです。ブラウザーの挙動はChromiumで検証しており、FirefoxとSafariで
同等の検証をしたとは主張しません。JavaScriptがなくても静的本文と通常のリンクは利用できますが、
対話機能には対応するbrowser APIが必要です。動的に読み込むCLI/site経路ではtrimmingと
Native AOTは非対応であり、その配布形態の動作保証はありません。

PNG、JPEG、WebPはSkiaで実際に変換しました。AVIFには明示設定した信頼できる`avifenc`が必要です。
この環境では実AVIF encodeは未検証です。

## MDXとブラウザー機能の範囲

MarkdownだけならNode.jsは不要です。MDXはNode.js 24.13.0と同梱lockfileを使います。
任意のNode版やnpm packageを保証するものではありません。browser importはブラウザーで動作し、
server importは生成時に実行できる必要があります。workerはTypeScriptをtranspileしますが、型検査はしません。

Selective Hydrationは明示的に有効化します。islandにはload、idle、visible、media、manualの
起動戦略があります。未知の対話component、共有context、安全に分離できない境界はpage hydrationへ
fallbackします。静的ページでMDX hydration entryがなくても、任意のDocs navigationやsearchのJSは
読み込まれる場合があります。静的fixtureの削減率を全islandやfallbackへ一般化しないでください。

Docusaurus aliasは文書に記載した範囲のみです。任意のpluginとJavaScript設定の実行は非対応です。
移行機能は非対応構文を報告するもので、サイト全体の自動変換を保証しません。

## 信頼と公開

MDX、React module、C#のsite code、compiler pluginはbuild accountの権限で実行します。
HTML safetyはコード実行のsandboxではありません。信頼できないMDXは、認証情報や非公開ファイルを
持たない隔離環境で扱ってください。import検証とworker環境変数の制限はOSのsandboxではなく、
信頼するコード自体はnetwork通信や外部processの起動ができます。

通常のMDX生成はnpm依存をrestoreしません。`restore-mdx`は
`npm ci --ignore-scripts --no-audit --no-fund`を明示的に実行し、cacheがなければnetworkを使います。
`dotnet restore`も設定したfeedへ接続します。Git metadata、外部リンク検査、API build、example test、
外部AVIF encodeは明示的なoptionまたはcommandです。OpenAPIの外部参照は取得せず診断します。

DTOのfieldとJSON設定は、公開するものだけを選んでください。`MdxPublicData`とisland propsには
明示schemaが必要で、秘密情報を含めてはいけません。Algoliaには公開可能なsearch-only keyを使います。
`unlisted`と`noindex`はaccess controlではなく、URLを直接開けば閲覧できます。

live codeはReactとrenderを提供する限定的なsandbox iframeで動き、汎用MDX/npm実行環境ではありません。
offline機能はopt-inで、HTTPSまたはlocalhostが必要です。revision全体を一緒に配置し、利用者が参照する
可能性がある古いhash付きassetを保持してください。`DocumentationBrowserOptions.Analytics`は既定で
無効で、明示的なconsent eventが必要です。この同意制御は従来のGoogle Analytics snippetには適用しません。
`SiteSettings.GoogleAnalyticsMeasurementId`、`GA_MEASUREMENT_ID`、`GOOGLE_ANALYTICS_MEASUREMENT_ID`は
独立してそのsnippetを有効にします。文書サイトの同意制御を使う場合は、これらを未設定にしてください。

出力のstagingとrollbackは協調する同一userのbuildを保護するもので、特権userや攻撃的なfilesystem
writerへの防御ではありません。atomic renameが必要です。Unix ACL、拡張属性、ownershipの保持は
portableな保証に含みません。after-build observerはcommit後に実行するため、失敗しても配置を戻せません。
