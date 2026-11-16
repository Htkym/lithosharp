# Agent Readyの公開Markdown

[English](agent-ready.md)

Agent Readyは対象のHTMLページに対応する公開Markdownを追加する機能です。正規URLにはHTMLのURLを維持し、発見用リンクと既存のllms.txtは生成するファイルと同じ公開計画を使います。利用者が明示的に有効化する機能であり、外部エージェントによる利用、検索での表示、スキャナーの得点を保証しません。

## 機能の有効化

既存のサイト設定でAgent Readyを有効にしてください。DocsやBlogのテンプレートは従来どおり選びます。Markdownを有効にしても、テンプレートの選択やMDX workerの起動は行いません。

```csharp
using LithoSharp;
using LithoSharp.AgentReady;

var customization = new SiteCustomization
{
    AgentReady = new AgentReadyOptions { Enabled = true },
    GenerateLlmsTxt = true
};
```

`Enabled`の既定はfalseです。無効な場合は、既存のHTMLの公開判定と従来のllms.txtの掲載対象を維持します。JSON-LDとrobotsの生成は別々に明示設定してください。設定方法と独自レイアウト用のheadヘルパーは、[メタデータと発見用リンク](agent-ready-metadata.ja.md)で説明しています。

`GenerateLlmsTxt = false`にすると、Agent Readyが有効でもllms.txtとそのdescribedbyリンクを生成しません。Markdownの提供は無効になりません。

## Markdownの提供と索引への掲載

`MarkdownPost.AgentReady`、`SiteExtraPage.AgentReady`、または型付きページの`PageMetadata.AgentReady`に`AgentReadyPageOptions`を指定します。

- `Auto`は完全な公開本文を提供できる場合だけMarkdownを生成します。変換できない場合はHTMLを維持し、Markdownとalternateリンクを生成せず、理由を付けて`LSQ012`を報告します。
- `Required`は完全な自動変換または明示的な代替本文を要求します。提供できなければ、任意のQuality検査が無効でも公開前にビルドが失敗します。
- `Disabled`はMarkdownを意図的に生成しません。代替本文より優先しますが、この指定だけではllms.txtからページを除外しません。

最初にHTMLとして公開できるかを判定します。下書きや、公開期間・環境の対象外のページが、MarkdownをRequiredにしただけで公開されることはありません。NoIndexとUnlistedのページは、Markdownの自動提供と索引への自動掲載から除外します。Requiredまたは明示的な代替本文でMarkdownを提供できますが、索引への掲載は別に指定してください。これらの設定はアクセス制御ではありません。

```csharp
using LithoSharp.Pages;

var metadata = new PageMetadata("Internal-facing guide")
{
    NoIndex = true,
    AgentReady = new AgentReadyPageOptions
    {
        MarkdownMode = PublicMarkdownMode.Required,
        Indexing = AgentIndexMode.Exclude,
        MarkdownReplacement = "# Guide\n\nThe complete public counterpart."
    }
};
```

`AgentIndexMode.Auto`は新しい自動掲載の判定を使います。`Include`ではNoIndexやUnlistedのページを明示的に含められ、`Exclude`では除外します。どちらも適用される`GeneratedPageDerivedSurfaces.LlmsTxt`の除外指定を取り消しません。Markdownを生成しても、ほかの派生面フラグは変わりません。掲載対象のページにMarkdownがなければ、llms.txtにはHTMLのURLを残します。

代替本文には要約や追加の段落ではなく公開する本文全体を指定します。nullは通常の提供経路を使う指定で、空文字列は空の本文を明示する指定です。重要な説明、警告、コンポーネントの内容を省略せず、代替本文に含めてください。

## ルートと原文と公開メタデータ

既定では最終的なHTML出力の拡張子を`.md`に変えます。`guide.html`は`guide.md`、`guide/index.html`は`guide/index.md`になります。ディレクトリのindexとして公開するHTMLはそのURLを正規URLとして維持します。出力が`.html`以外の場合はMarkdownのルートを明示する必要があります。明示するルートの末尾は`.md`にしてください。

```csharp
using LithoSharp.Routing;

var provision = new AgentReadyPageOptions
{
    MarkdownMode = PublicMarkdownMode.Required,
    MarkdownRoute = SiteRoute.ForFile("readable/guide.md")
};
```

変換には最終的な本文と、それに対応するSyntamarkの解析結果を使います。生成済みHTMLからの逆変換ではありません。対応する見出し、コード、表、引用、参照、profile固有の内容を保持します。コード外のHTMLコメントは除去し、コード例の内容はそのまま残します。未知のHTML、未対応のコンポーネント、不完全な解析、安全に編集できない原文範囲がある場合は、部分的なMarkdownを成功扱いにしません。

リンクは登録済みの公開ルートとassetから解決します。元文書へのリンクには実際の文書とHTMLの対応表を使い、宣言されたincludeの出典も考慮します。フラグメントは現在のHTMLページを参照します。Markdownのファイル名だけから公開ルートを推測しません。複数の原文をまとめる生成ページで一つの出典を決められない場合、providerには公開先の絶対URLまたはルート相対URLを使ってください。URLの解決とMarkdown宛先のエスケープは別々に行います。

providerがinclude展開後の本文を変更すると、以前のsource mapは本文全体に対応しなくなります。この場合、原文に対する相対宛先をページの原文へ解決し直さず、拒否します。絶対URL、ルート相対URL、クエリだけ、フラグメントだけの宛先は通常のresolverで処理できます。

公開するfront matterの項目は、タイトル、正規HTML URL、設定されている説明と言語に限定します。元のfront matter、任意の独自メタデータ、ローカルパス、source map、キャッシュの記録、providerの例外の詳細をこのヘッダーへコピーしません。本文が`---`で始まっていても、生成したヘッダーの後では本文として保持します。

## 型付きレンダラーと完全なprovider

独自のHTMLレンダラーは元の本文にない情報を追加できます。原文だけで生成後のページを説明できるとは限らないため、対応する完全なMarkdownをproviderで提供してください。既存の`ContentCollection<DocumentFrontMatter, string> collection`で、本文だけを描画するDocsレンダラーは次のように登録できます。

```csharp
using LithoSharp.Content;
using LithoSharp.Documentation;

var pages = collection.RenderDocsWithMarkdown(
    entry => new DocsPagePresentation { Title = entry.FrontMatter.Title },
    (entry, context) => context.RenderMarkdown(entry.Body),
    (entry, context) => entry.Body,
    rendererFingerprint: "docs-public:v1",
    isThreadSafe: true);
```

両方のデリゲートは準備済みのentryとcontextを受け取ります。ほかの型付きコレクションと同様に、`pages`をサイトの`ContentCollections`へ追加してください。HTMLレンダラーでダウンロードのリンク、画像、警告、説明文を追加した場合は、Markdownのproviderにも対応する内容を含めます。providerのない通常の`RenderDocs`登録は、任意のHTMLを変換できることを保証しません。

ほかの型付きレンダラーでは`SiteContentCollection<TFrontMatter, TBody>.MarkdownProvider`を設定します。C#でグループ別のページを作る場合は`GeneratePagesWithMarkdown`を使ってください。providerはHTMLレンダラーと同じfactory生成の`SitePage<TPageContent>`とcontextを受け取ります。nullを返すと未対応、空文字列を返すと明示的な空の本文になります。providerを呼ぶのは公開対象とMarkdown提供対象を選んだ後だけで、読取専用の検査では呼びません。

明示的に登録した静的Markdownコンポーネントは`IPublicMarkdownComponent<TProps>`を実装できます。HTMLとMarkdownのメソッドには同じbind済みpropsを渡します。完全な代替内容がなければ未対応です。providerの失敗はMarkdownの提供失敗として扱い、成功したHTMLレンダラーをHTMLエラーへ変更しません。Requiredの場合は、完全なMarkdownがなければビルドが失敗します。

[Docsサンプル](../samples/LithoSharp.DocsSample/SiteFactory.cs)にはHTMLで追加するリンクや画像も含めた型付きDocsとグループ別ページのproviderがあります。[登録済みコンポーネント](../samples/LithoSharp.DocsSample/MarkdownComponents.cs)もMarkdownを提供します。[Blogサンプル](../samples/LithoSharp.Sample/SiteFactory.cs)は既存のBlog生成経路でAgent Readyを有効にしています。

## 静的MDXと明示的provider

Agent Readyが有効な場合、`MdxSite`の既定のHTMLレンダラーではproviderを明示しなくても限定範囲の静的Markdownを生成できます。HTMLで使うものと同じworkerのASTを使い、通常の原文を保持し、既知のwrapperの範囲だけを置き換えます。生成候補は単一の原文に対応する本文snapshot、source hash、workerの実装契約と一致する必要があります。合成した本文や検証できない本文を完全と仮定しません。独自のC# HTMLレンダラーではこの候補を自動的に使わず、完全なproviderを用意してください。

対応するflow wrapperと属性は次のとおりです。

- `Admonition`: リテラルの`type`と`title`。種類、タイトル、本文を保持します。
- `Details`: リテラルの`summary`と`open`。要約と本文全体を含め、HTMLで初期表示時に折りたたまれる内容も省略しません。
- `Tabs`: リテラルの`defaultValue`、`groupId`、`queryString`。選択中のタブだけでなく、すべての`TabItem`の本文を含めます。`TabItem`ではリテラルの`value`、`label`、`default`に対応し、labelまたはvalueを見出しとして保持します。
- 既知の`note`、`tip`、`info`、`warning`、`danger`、`caution`のdirective。種類、指定されているラベル、本文を保持します。

これらのコンポーネントは固定された`@theme/Admonition`、`@theme/Details`、`@theme/Tabs`、`@theme/TabItem`のdefault import、または`@lithosharp/runtime`の名前付きexportを使えます。安全な大文字始まりの別名にも対応します。属性には対応するリテラル文字列や属性の有無で表す真偽値を使い、式は使わないでください。

```mdx
import Details from '@theme/Details';

<Details summary="Important background">

This explanation remains in public Markdown even when initially collapsed.

</Details>
```

通常のMarkdownのリンク、画像、参照定義、コード、数式は、通常の公開URL変換の前まで原文の形で保持します。コード外のMDXコメントは除去し、コード例の内容はそのまま残します。未知のJSX、実行する式、利用者のimportやexport、コンパイラープラグイン、`ComponentsModule`による上書き、原文を変える`mdx-code-block`の前処理、リストや引用内のwrapper、`Tabs values={...}`は未対応です。`StaticComponents`を指定しても、任意のコンポーネントに完全なMarkdown表現があるとは判断しません。AutoではHTMLを維持し、Markdownを提供できない理由を報告します。Requiredでは完全な代替内容がなければ失敗します。

`MdxSite.AddCollectionWithMarkdown`と`MdxBlogSite.AddCollectionWithMarkdown`には完全なproviderを明示的に登録でき、静的な生成候補より優先します。providerはHTMLレンダラーが使う準備済みのMDX entryを受け取ります。抜粋、feed用テキスト、抽出したプレーンテキスト、hydrationの無効化だけでは、完全なMarkdownを提供できると判断しません。限定された静的変換で公開内容全体を確定できない場合は、明示的な代替本文またはproviderを用意してください。既存のビルドコードの信頼条件とworkerの範囲は[MDXサイト](mdx.ja.md)で確認してください。静的変換によってMDXの実行がsandboxになることはありません。

## 提供結果の確認

生成後は`SiteGenerationResult.AgentReadyPages`でページごとの結果を確認できます。`PublicMarkdownPageResult`には`HtmlPath`、提供時の`MarkdownPath`、`Status`、提供できなかった場合の公開用の`Reason`が含まれます。パスは原文のファイル名ではなく、出力先のパスです。Agent Readyが無効な場合、この一覧は空になります。

```csharp
foreach (var page in result.AgentReadyPages)
{
    Console.WriteLine($"{page.HtmlPath}: {page.Status} -> {page.MarkdownPath}");
    if (page.Reason is not null)
        Console.WriteLine(page.Reason);
}
```

`Automatic`と`ExplicitReplacement`は完全な本文を提供した結果です。`IntentionallyExcluded`は方針による除外です。`Unsupported`、`Unavailable`、`Failed`は、提供できなかった状態を理由付きで区別します。列挙型には依頼されていない処理を表す`NotRequested`もあります。これらは生成の結果であり、HTTP配信の検査結果ではありません。Requiredで提供できない場合は例外となり、公開に成功した結果は返しません。

## キャッシュと依存入力

公開Markdownは提供状態を持つ正式なビルド成果物です。検証済みのキャッシュから完全な本文と未対応・失敗の状態を再利用します。キャッシュの記録が破損している場合、本文が不正なUTF-8の場合、ハッシュが一致しない場合は再利用しません。キャンセルを成功扱いにすることもありません。入力が変わらない再ビルドでは互換性のある結果を再利用し、公開用変換のために元の本文を再解析しません。

キーには本文と原文の識別情報、解析・コンパイラーのprofile、公開ルートとassetの対応表、ページのメタデータ、取得したレンダラーの依存入力を含めます。登録済みコンポーネントのcontextには、文言、テーマ、ビルド時刻も含めます。型付きproviderはコレクションの既存の入力情報を使います。

独自レンダラーでキャッシュを再利用する場合は、安定した`rendererFingerprint`を設定し、変換規則を変えたらtransformation識別子のバージョンを更新してください。どちらかのレンダラーが使う外部ファイル、asset、キャプチャした値はすべて依存入力として宣言します。fingerprintは利用者による宣言であり、任意のクロージャーの状態を自動検出するものではありません。キャッシュを使う条件を満たさなければ再実行します。[増分ビルド](incremental-builds.ja.md)も参照してください。

## 部分ビルドと削除と無効化

`OutputScope`は成果物のパスを選びます。`guide.html`だけを指定しても`guide.md`は対象になりません。対になる内容を変更する場合は、関係する変更前と変更後のHTML・Markdownのパスを両方含めるか、それらを含むディレクトリを指定してください。以前に生成したページを削除したり非公開にしたりする場合も同じです。索引が変わる場合は`llms.txt`を含めるか、全体をビルドします。必要なパスが不足する場合は、指定範囲を自動的に広げず、出力への書込み前に拒否します。

```csharp
var options = new SiteGenerationOptions
{
    OutputScope = ["guide.html", "guide.md", "llms.txt"]
};
```

対象範囲外で変更されていない成果物は、既存の部分ビルドの動作に従って維持します。Agent Ready全体、robots、索引方針の変更には全体ビルドが必要です。Agent Readyを無効にした後は全体をビルドし、所有済みの不要なMarkdownと発見用リンクを削除してください。利用者が所有するファイルは既存の所有権・衝突検査で保護します。失敗やキャンセルでは、通常の出力トランザクションにより、公開済みの出力を維持します。[ビルドグラフと所有権](build-graph.ja.md)も参照してください。

## Qualityと配信の検査

`SiteGenerationResult.QualityReport`と`BuildReport.Diagnostics`で結果を確認できます。`LSQ012`はAutoで提供できない場合と意図的な除外を区別し、既存のQualityの失敗閾値に従います。たとえばWarningを閾値にすると、未対応のMarkdownを完全な本文として扱うことなく、Autoのビルドを失敗させられます。

Requiredで求める提供、ルートの衝突、出力範囲違反、誤った発見用リンク、正規URL・言語の不一致、管理対象のrobots・sitemapの不整合は、`Quality = null`でも必須検査です。任意のJSON-LD診断である`LSQ013`は明示的に有効にしたQualityの検査で扱います。メタデータの生成方針は別に設定できます。詳しくは[メタデータの検査](agent-ready-metadata.ja.md)と[サイトの品質検査](site-quality.ja.md)を参照してください。

ビルドの成功は生成ファイル、実際のhead内のリンク、公開成果物と計画のバイト列の一致を確認した結果です。ローカルや本番のHTTP動作を確認した結果ではありません。ローカルserveでは`.md`を`text/markdown; charset=utf-8`で返します。本番のMIME、リダイレクト、未知URLのHTTP 404応答、オリジンのルートにあるrobotsは、HTTPで明示的に検査する必要があります。[生成済み出力の検査](generated-output-checks.ja.md)に従い、任意の`check-output --http`コマンドなどで確認してください。Agent Readyを有効にしただけでは、本番HTTPの検査やリリースは行いません。
