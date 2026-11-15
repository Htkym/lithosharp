# Agent Readyのメタデータと発見用リンク

[English](agent-ready-metadata.md)

Agent Readyは、利用者が明示的に有効化する公開機能です。外部エージェントによるページの利用、検索エンジンのリッチリザルト表示、スキャナーの得点を保証するものではありません。`llms.txt`は提案段階の仕組みであり、アクセス制御や索引の標準ではありません。

## メタデータの有効化

ライブラリの既定ではJSON-LDやrobots.txtを生成しません。機能全体を有効にしたうえで、メタデータごとの生成方針を選んでください。

```csharp
using LithoSharp;
using LithoSharp.AgentReady;

var customization = new SiteCustomization
{
    GenerateLlmsTxt = true,
    AgentReady = new AgentReadyOptions
    {
        Enabled = true,
        StructuredData = new AgentReadyStructuredDataOptions
        {
            Mode = AgentReadyStructuredDataMode.Automatic,
            Publisher = new AgentReadyIdentity
            {
                Kind = AgentReadyIdentityKind.Organization,
                Name = "Example documentation",
                Url = "https://example.com/"
            }
        },
        Robots = new AgentReadyRobotsOptions
        {
            Mode = AgentReadyRobotsMode.Generated
        }
    }
};
```

robotsの管理方式を`Generated`にするには、サイトのベースURLが`https://example.com/`のようなオリジンのルートである必要があります。`https://example.com/project/`のようなサイトで、別のシステムがオリジンのrobots.txtを管理する場合は`External`を選んでください。`Disabled`では管理対象のrobotsファイルを生成しません。この設定を変えても、ホスト側のルート設定は変わりません。

## 発見用リンクとllms.txt

完全なMarkdownを自動生成できたページ、または明示的な代替本文を提供するページには、head内に`rel="alternate" type="text/markdown"`のリンクを付けます。hrefには登録済みのMarkdownルートを使います。未対応や除外したページの`.md` URLを推測して追加しません。

生成したllms.txtに含まれるページは、`rel="describedby"`でその索引を参照できます。`GenerateLlmsTxt = false`にすると、Agent Readyを有効にしていても索引とその発見用リンクを生成しません。llms.txtはサイトのベースパス配下に配置できます。robots.txtのようにオリジンのルートへ置く必要はありません。

既存のllms.txt生成器は、掲載対象のページに完全なMarkdownがある場合はそのURLを優先し、なければHTMLのURLを使います。索引への掲載とMarkdownの提供は別々に判断します。Markdownを生成しても、UnlistedやNoIndexのページを自動的に索引へ追加したり、派生面からの除外指定を取り消したりしません。タイトルと説明には公開メタデータを使い、複数行の値を1行にまとめ、Markdownの構文に関わる文字をエスケープします。本文全体を索引へ連結することはありません。

正規URLにはHTMLのURLを維持します。代替MarkdownのURLや、その本文に含めた元ページへのリンクは、HTTPのcanonical宣言にはなりません。`Vary: Accept`、HTTPのLinkヘッダー、コンテンツネゴシエーションを実装したことにもなりません。

## 構造化データの方針

サイト全体の方針には`Disabled`、`Automatic`、`UserManaged`を指定できます。ページの`AgentReadyPageStructuredDataOptions.Mode`を設定すると、そのページではサイト全体の方針より優先します。

自動生成ではWebSiteとページの構造化データを出力します。実際の内容に応じて`WebPage`、`TechArticle`、`BlogPosting`を選んでください。既定はWebPageです。技術的なタイトルという理由だけでTechArticleを選ぶものではありません。

```csharp
using LithoSharp.Pages;

var metadata = new PageMetadata("How to configure the parser", "Parser setup")
{
    Language = "en",
    AgentReady = new AgentReadyPageOptions
    {
        StructuredData = new AgentReadyPageStructuredDataOptions
        {
            Kind = AgentReadyPageKind.TechArticle,
            Author = new AgentReadyIdentity { Name = "Alex Example" },
            Published = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)
        }
    }
};
```

著者、発行主体、sameAsのURL、日付、画像は、明示された公開情報がある場合だけ出力します。ビルド時刻を公開日や更新日に使わず、トップページだからという理由でPersonを作りません。JSONのシリアライズにはHTMLで安全なエスケープを使うため、値に`</script>`が含まれても生成したscript要素を閉じません。

独自のheadでJSON-LDを管理する場合は`UserManaged`を選んでください。Automaticでは、渡したheadに既存のJSON-LDがあると自動挿入を抑え、管理方式の競合を診断します。同じページを重複して説明しないよう、管理する側を一つに決めてください。利用者が管理するJSONも、解析エラー、検証可能なページ情報の欠落、ページURLの矛盾、同じページ情報の重複を検査します。Schema.orgの完全な検証や、検索エンジンのリッチリザルト条件の確認ではありません。

## 独自レイアウト

独自レイアウトでも、標準レイアウトと同じ型付きヘルパーでheadの追加要素を作れます。

```csharp
var additions = AgentReadyMetadata.RenderHead(
    new AgentReadyHeadData
    {
        SiteTitle = "Example documentation",
        SiteUrl = "https://example.com/project/",
        SiteLanguage = "en",
        CanonicalUrl = "https://example.com/project/guide.html",
        Title = "Guide",
        Description = "An introduction to the public API",
        Language = "en",
        Page = new AgentReadyPageStructuredDataOptions
        {
            Kind = AgentReadyPageKind.TechArticle
        },
        MarkdownUrl = "https://example.com/project/guide.md",
        LlmsUrl = "https://example.com/project/llms.txt"
    },
    customization.AgentReady.StructuredData);
```

返された`IHtmlContent`は実際のHTMLのhead内に挿入してください。MarkdownUrlには完全な登録済み成果物のURLだけを、LlmsUrlにはそのページを含む生成済み索引のURLだけを渡します。生成器のページ計画に従う必要があります。最終的なレイアウトでリンクが欠落したり、誤ったリンクになったりすると検査で検出します。追加要素を計算するだけで、HTMLへ出力しなければ条件を満たしません。

`ExistingHead`には管理方式の競合を検査するために既存のhead追加要素を渡せます。この内容は戻り値に含めないため、レイアウト側で既存のheadを1回だけ出力してください。公開ヘルパーへ渡すURLは絶対HTTP(S) URLとし、認証情報、フラグメント、制御文字を含めないでください。

## robots.txtとsitemap

生成するrobots.txtに、特定のクローラーやAI学習に関する方針を暗黙に加えることはありません。既存の正規HTML用sitemapを絶対URLで参照し、Markdownの複製をsitemapへ追加しません。規則は型付きのグループで明示できます。

```csharp
Robots = new AgentReadyRobotsOptions
{
    Mode = AgentReadyRobotsMode.Generated,
    Groups =
    [
        new AgentReadyRobotsGroup
        {
            UserAgents = ["*"],
            Disallow = ["/private/"]
        }
    ]
}
```

利用者が管理する既存のrobots.txtを、確認せずに上書きしたり結合したりしません。外部管理を続けるか、管理方式の競合を解消してください。Disallowの規則は認可の代わりにはならず、NoIndexやUnlistedもアクセス制御ではありません。

## 検証する範囲

生成物の検査では実際のHTMLに含まれる発見用リンク、成果物の存在、正規URLと言語、実際のJSON-LDの内容を確認します。配信時や本番環境での確認とは区別します。

- `LSQ013`: JSON-LDの不正、競合、公開情報との不一致。
- `LSQ014`: 発見用リンク、正規URL、言語、成果物計画の不整合。
- `LSQ015`: robotsの配置やsitemap参照の不整合。

Requiredで求めるMarkdownの提供、ルートの衝突、誤った発見用リンクの検査は、任意の品質検査を無効にしても回避できません。既存のSEO項目の欠落は通常の品質レポートで扱い、説明の欠落を重複して診断しません。

通常のビルドは本番HTTPの確認を行いません。公開後は[`check-output --http`](generated-output-checks.ja.md#公開後の明示的なhttp検査)を使うか、実際の公開HTMLとMarkdownのURL、リダイレクト、Content-Type、未知URLのHTTP 404応答、オリジンの`/robots.txt`を確認してください。ローカル配信ではMarkdownを`text/markdown; charset=utf-8`で返します。本番のMIMEやヘッダーはホスト側で設定する必要があり、ファイルを生成しただけでは実際の配信動作を確認したことになりません。
