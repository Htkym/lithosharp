# LithoSharp.Analyzers

[English](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Analyzers/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Analyzers/README.ja.md)

.NET でドキュメントを開発するチーム向けのコンパイラー診断です。C# のコンテンツスキーマと生成された参照の整合性を確認します。バージョン 3.0.0 を、同じバージョンの LithoSharp パッケージと組み合わせてください。Analyzer は netstandard2.0 と Roslyn 4.14.0 を対象とし、コンパイラーホストでサイトファクトリー、ユーザー定義の getter、Core のランタイムコードを実行しません。

## インストールと使用

利用するプロジェクトの `ItemGroup` に次の参照を追加します。

```xml
<PackageReference Include="LithoSharp.Analyzers" Version="3.0.0" PrivateAssets="all" />
```

生成されたカタログを必要とする規則には [LithoSharp.Generators](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Generators/README.ja.md) を使用します。IDE の修正機能は、任意で追加する別パッケージの [LithoSharp.CodeFixes](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.CodeFixes/README.ja.md) が提供します。[型付きコンテンツの手順](https://github.com/Htkym/lithosharp/blob/main/samples/LithoSharp.DocsSample/README.md#from-a-c-model-to-checked-output)では、スキーマ、参照、出力検査を組み合わせて説明しています。

## 解析の範囲

`Known` の値は、制限された範囲でコンパイラーが確認した事実です。`Closed` が示す網羅範囲は、各規則が対象とする検索または所有元に限られます。`Deferred`、`Open`、`Stale`、`Unavailable` は妥当性の証明になりません。不明な値、未対応の式、同名のユーザー定義型やユーザー定義の変換、上限を超えた入力は未検証のままです。生成コードは対象外です。

定数、対応する変換、文字列の連結、書式指定のない文字列補間、ローカル変数への単一の代入、有限個の分岐からは、最大8個の既知の値を得られます。Error を報告するのは、すべての値が対象のガードに違反する場合だけです。有効な値と無効な値が混在する場合、その Error は報告しません。プロパティ、変更可能なフィールドや readonly フィールド、任意の呼び出し、書式指定、ループ、ref による参照の流出、例外を伴う制御フローによって、ローカル値を解析できない場合があります。null 許容警告を抑制する `!` は既知の事実を保ちますが、不明な値を証明するものではありません。

個別の診断は、通常の Roslyn 設定、`.editorconfig`、`NoWarn`、元のソースに記述する pragma で設定します。診断を抑制しても実行時のガードはなくなりません。ビルドが成功してもサイト全体の妥当性は確認できないため、生成と[出力品質検査](https://github.com/Htkym/lithosharp/blob/main/docs/generated-output-checks.ja.md)も実行してください。YAML／スキーマの診断は引き続き Generator が担当します。

### LSA1001

Error：`SiteUrl.FromAbsolute(string)` に渡す既知の引数がすべて無効です。空白、制御文字、バックスラッシュを含まない絶対 HTTP(S) URL を使用してください。

### LSA1002

Error：既知の引数が、登録されたルートまたは URL のガードに違反しています。`SiteUrl.ForFile`、`SiteUrl.ForDirectory`、`SiteRoute.ForFile`、`SiteRoute.ForDirectoryIndex` は相対パスと省略可能なベース URL を検査します。ディレクトリトラバーサル、予約済みのデバイス名、不正なパーセントエンコーディングや UTF-8 エンコーディング、無効なベース URL を避けてください。空のディレクトリルートは有効です。

`SiteUrl.FromRoute`、`StaticSiteManifest.GetUrl(string)`、`ContentRef<TEntry>` のルート引数は確実に null である引数を拒否します。`ContentRef<TEntry>.GetUrl(baseUrl)` は明示的なベース URL を検査します。null または省略時は既存のルートを維持します。空のベース URL、相対ベース URL、クエリやフラグメントを含むベース URL は無効です。これらの検査は、レシーバーが null でないこと、呼び出しに到達すること、ルートやカタログに含まれることを証明しません。

### LSA1003

Error：`SiteAsset` の既知の相対入力／出力パス、または `SiteAssetOutput` の既知の相対出力パスが無効です。これらはファイルシステム上の名前なので、パーセント記号を含む文字列はそのままの文字として扱います。相対パスを修正してください。入力ルートからの解決、ファイルの存在、レジストリーの完全性は実行時に確認する必要があります。

### LSA1004

Error：品質オプションの既知の引数がそれぞれのガードに違反しています。`SiteQualityOptions.failureThreshold` には対応する重大度を指定します。`ExternalLinkCheckOptions` は、空白だけではないキャッシュパス、負でないキャッシュ期間、制限内の要求間隔とタイムアウトを必要とします。間隔は0にできますが、タイムアウトは0にできません。タイマーの上限は 42949672940000 ticks です。期間が既知の null なら既定値を維持します。対応する `TimeSpan` の値は、定数、ticks を受け取るコンストラクター、整数値を受け取るファクトリーです。小数値、非有限値、オーバーフロー、丸めが不確実な値、ユーザー定義のファクトリーは Deferred のままです。

### LSA1005

Error：`SiteGenerator.GenerateWithOptionsAsync` に直接構築して渡した `SiteGenerationOptions` で、既知の `MaxDegreeOfParallelism` の値がすべて1未満です。1以上に設定してください。省略時の既定値は有効です。オプションの別名参照と `with` 式はこの解析範囲に含まれません。

### LSA1101

Info：`Html.UnsafeRaw(string)` に渡す内容を静的に確定できません。生の出力を確認してください。未検証の内容を報告する診断であり、脆弱性があると断定するものではありません。

### LSA1102

Error：生のフラグメントだと証明された値から明示的に呼び出した `ToHtmlString()` の結果が、既知の引用符付き HTML 属性内にあります。属性用に適切にエンコードした値を使用してください。sealed な `HtmlText` と `HtmlAttributeValue` は内容をエンコードするため、この位置で使用できます。任意の `IHtmlContent` 変数は生のフラグメントであることの証明になりません。生のオブジェクトを直接補間した場合は、レンダリングした HTML ではなく型名が出力されます。

### LSA1103

Error：既知のタグ名、属性名、引用符のない属性値に補間があります。適切な引用符付き属性を追加するか、テンプレートを手動で変更してください。引用符を自動で補う修正機能はありません。

### LSA1104

Warning：既知の script／style テキスト、または引用符付きのイベントハンドラー／style 属性に、`Html.Encode`、`HtmlText`、`HtmlAttributeValue` があります。HTML エンコーディングは JavaScript／CSS の安全性を証明しないため、適切なシリアライズ方法を手動で選んでください。

コンテキストの検査は `Html.UnsafeRaw(string)` に渡される直接の補間文字列と、有限個の制御フローから組み合わせた補間文字列に対応します。不明な補間箇所、書式指定や配置指定、コメント、不正な前置部分、SVG／MathML の外来コンテンツ、未対応の解析モードがあると、後続の確定的な診断を報告できない場合があります。直接のテンプレートでは、ソースとデコード後の長さはそれぞれ 65,536 UTF-16 コード単位、補間箇所は256個、タグ名／属性名は各128文字までです。組み合わせた形では、候補は8個、各候補の構成片は513個までです。上限を超えると解析は Deferred のままです。

### LSA1105

Error：静的に既知の HTML に、実行時の `LSQ001` 規則でベース URL に依存せず判断できる URL 違反があります。対象は空のリソース URL、不正な明示的絶対スキーム、未対応の絶対スキーム、HTTP(S) のユーザー情報です。報告された元の C# ソース位置で URL を修正してください。

対応する入力は、通常／逐語的／生の文字列リテラル、組み込みの文字列加算、ソース内の文字列定数、既知のローカル変数やキャプチャー、有限個の分岐、書式指定がなく文字列だけを補間する式です。すべての値と由来について、解析と元のソースへの対応付けが完全である必要があります。複数の構成片にまたがる位置や曖昧な位置、不完全な解析、上限を超える入力は Deferred のままです。直接のリテラル式には最大64個の葉を認めます。HTML 入力の上限は、デコード後が 65,536 UTF-16 コード単位、ソースが 655,360 コード単位、ノードが 8,192 個、深さが128です。

検査の対象は、対応する名前空間のないリソース属性、受理された `srcset` 候補の URL、有効な style 属性／要素内のインライン CSS URL です。スタイルシートの最上位にある `@import` の文字列も含みます。不活性な template の内容は対象外です。CSS の解析も完全である必要があります。相対 URL、プロトコル相対 URL、外部スタイルシート、base／canonical のポリシー、サイトへの所属、アンカー、画像の選択、ブラウザーへの一般的な適合性は別に検査する必要があります。ベース URL やサイトのグラフを推測することはありません。

### LSA1201

Error：`Guides.Manifest.GetUrl(path)` の既知のパスがすべて、同じ最新の Closed な生成済み検索対象にありません。`EmitStaticSiteManifest = true` でこの検索を有効にし、宣言済みの正確な公開パスを使用するか、不足する宣言を追加してください。

存在する値と存在しない値の混在、不明な引数、レシーバーの別名参照、カスタムファクトリー、メタデータだけのマニフェスト、通常のカタログは Deferred です。クエリ、フラグメント、URL の解決は、この順序比較によるパス検索の範囲に含まれません。コレクションのカタログは、サイトの公開、アンカー、アセットの網羅性を証明しません。

### LSA1202

Error：同じコンパイラー管理下の固定 HTML プラン内で、別ページのフラグメントが確実に存在しません。トップレベルの static partial クラスに `FixedHtmlSite` を指定し、引数のない static な `IHtmlContent` メソッドに `FixedHtmlPage` を指定します。

```csharp
using LithoSharp;
using LithoSharp.Content;

[FixedHtmlSite("https://example.com/")]
public static partial class Pages
{
    [FixedHtmlPage("source.html")]
    private static IHtmlContent Source() => Html.UnsafeRaw("<a href='/target.html#intro'>Intro</a>");

    [FixedHtmlPage("target.html")]
    private static IHtmlContent Target() => Html.UnsafeRaw("<h1 id='intro'>Intro</h1>");
}
// await Pages.FixedHtmlPlan.GenerateAsync("dist");
```

どちらのメソッド本体も最新のプラン内で元のソースへ完全に対応付けられたリテラルによる `Html.UnsafeRaw(string)` 呼び出しである必要があります。対応するリンクは、名前空間のない `a`／`area` のサイトルートから始まる href で、クエリがなく、空でないフラグメントを持つものです。デコード後の `id`／`a name` の値は順序比較します。プラス記号は空白として扱いません。リンク先のアンカーを追加するか、リンクを修正してください。

別のページ、欠けているリンク先、相対 URL／外部 URL／フラグメントだけの URL、static コンストラクター、古い入力や不完全な入力、script、iframe／srcdoc、カスタムのマークアップや外来コンテンツは Deferred のままです。対応するページ数は最大64です。プランは宣言された出力を所有し、`index.html` を含む組み込み出力名との衝突は認められません。一般の `StaticSiteManifest.AnchorCoverage` は Open のままです。CodeFix はありません。

### LSA1203

Error：既知の入力／出力インスタンスがすべて、そのインライン変換ハンドラーの宣言に含まれていません。`SiteAssetTransform` の入力と、そのコンテキストの `GetUrl`／`OpenRead` には同じ `SiteAsset` インスタンスを使用してください。出力と `WriteAsync` には同じ `SiteAssetOutput` インスタンスを使用します。別のインスタンスで ID やパスが同じでも、宣言に含まれることにはなりません。

コンストラクターにはインラインの配列／コレクション式とインラインのハンドラーを渡す必要があります。変更されていないローカル変数の別名参照に対応します。共有配列、再代入、ref による参照の流出、別のクロージャー、曖昧な制御フロー、不明な所属や有効／無効が混在する所属、内容を解析できないハンドラーは Deferred のままです。入力と出力の範囲は独立しています。上限は、候補8個、同一性をたどる深さ64、所有元の構文ノード 4,096 個です。検査するのは宣言への所属であり、ファイルの存在、重複した書き込み、未出力のファイル、サイト全体のアセットではありません。

### LSA1205

Info：生成済みマニフェストのフィンガープリントが、現在のコンパイラー入力と異なります。宣言や入力を変更した後は、生成カタログを再ビルドしてください。検索対象が古い間、ルートの不存在は Deferred のままです。別の partial 宣言内にあるものも含め、明示的な static コンストラクターがあるとコレクションは Unavailable になります。読めない入力、未対応の参照、上限を超えた入力でも、不存在を確定できません。フィンガープリントの上限は、ソース／追加入力 8,192 個、長さ情報を付けたテキスト 8,388,608 コード単位です。

### LSA1401

Info：検証済みで最新の Closed なマニフェスト内の直接の文字列リテラルに、同等な生成済み `PageRef` が1つあります。任意で導入する CodeFix はその引数だけを完全修飾された `Route.PublicPath` に置き換えます。レシーバー、オーバーロード、実行時のガードを維持し、現在の入力を再検証します。曖昧な参照、古い参照、手書きの参照、副作用のある参照、未証明の参照には修正を提示しません。FixAll の制限は [CodeFix ガイド](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.CodeFixes/README.ja.md)を参照してください。

### LSA1402

Error：一意に選ばれた同じ最新の Closed な生成カタログ内で、既知のエントリー ID と、その ID に対応しない別の有効なルートが組み合わされています。`Catalog.CollectionId` でカタログを選択し、宣言済みのルートまたは生成済みの参照を使用してください。主位置はルートで、エントリー ID は関連位置です。

存在しない ID や不明な ID、曖昧な範囲、手書き／Open／Stale／Unavailable のカタログ、セレクターの別名参照、null でないか不明なベース URL、動的なルート、ユーザー定義の変換では、この Error を報告しません。無効なルート構文は `LSA1002` です。この診断は、最終的な公開、アンカー、アセットの網羅性を証明しません。CodeFix はありません。

### LSA1403

Info：選択され、登録済みのドキュメントプロデューサーの出力には実行時の準備が必要で、静的には列挙できません。`SiteGenerationOptions.StaticRouteGuaranteeProducers` は `Extensions` に登録済みのインスタンスを選択するもので、登録や準備を行うものではありません。

両方のリストは直接構築した `SiteDefinition.Options` 内の明示的な配列／コレクション式である必要があります。変更されていないローカル変数の同一性と別名参照に対応し、重複は1回として数え、上限は64項目です。認識する Deferred プロデューサーは `LithoSharp.Mdx.DocumentationSite` で、同じインスタンスに先行する `AddCollection(new DocumentationCollection(...))` 呼び出しがあるものです。ソース内のプロデューサーで、正確な `PrepareAsync` が、`ContentCollections` が空だと証明できる `Task.FromResult(new SiteBuildContribution(...))` を返す場合は Deferred 項目を必要としません。未登録の選択、getter／ファクトリー、曖昧な値、その他のプロデューサー本体、上限を超えた入力は Unavailable のままです。この数はページ数やルート数ではなく、Info がないことも Closed の証明にはなりません。CodeFix はありません。

### LSA1501

Warning：戻り値が `AngleSharp.Dom.IDocument` の、旧 `LithoSharp.Testing.SiteTestDocument.Document` getter に結び付いた使用箇所は移行が必要です。[Testing の移行対応表](https://github.com/Htkym/lithosharp/blob/main/docs/Testing-v2-api.ja.md#移行)を使い、`Snapshot.Query/QueryAll` またはアサーション用ヘルパーを選んでください。変更操作、サードパーティーのキャスト／拡張、未対応のセレクターは手動で変更する必要があります。v2 のスナップショット、同名の別 API、未解決の構文は対象外です。自動 CodeFix はありません。

### LSA9001

RequireStaticCoverage は既定で無効です。`.editorconfig` に `dotnet_diagnostic.LSA9001.severity = error` を設定するか、診断 ID ごとのコンパイルオプションで有効にします。要求された `StaticSiteManifest.GetUrl(string)` の検索、または明示的な `StaticRouteGuaranteeProducers` の選択が Open、Stale、Unavailable、不明である場合に、その範囲と理由を報告します。最新の Closed な検索対象で文字列が既知の場合と、選択したプロデューサー群が空だと証明できる場合は、このポリシー診断を必要としません。

参照の欠落を断定する診断ではなく、任意の HTML、アンカー、アセット、動的なプロデューサー、サイト全体を保証するものでもありません。ほかの診断と実行時のガードはそれぞれ別の範囲を維持します。CodeFix はありません。
