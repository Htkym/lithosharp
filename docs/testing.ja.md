# サイトのテスト

`LithoSharp.Testing` は AngleSharp を使った、テストフレームワークに依存しない DOM 検証 API です。ブラウザーを起動せず、スクリプトの実行やネットワークへの接続も行わずに HTML を解析します。

```csharp
using LithoSharp;
using LithoSharp.Testing;

using var document = SiteTestDocument.Parse("<main><h1>Home</h1><p>&lt;safe&gt;</p><meta name='description' content='Description'></main>");
document.AssertElement("main");
document.AssertText("h1", "Home");
document.AssertText("p", "<safe>");
document.AssertMeta("description", "Description");
```

`AssertText` は、最初に一致した要素の `TextContent` を期待値と完全一致で比較します。文字参照は解析時にデコードされます。属性、メタデータ、リンク、画像も、引用符や改行を含めた値の完全一致で検証します。要素が見つからない場合や値が異なる場合は `SiteTestException` が発生します。

null、空白のセレクター、不正な属性名には標準の引数例外が発生します。CSSセレクターの構文エラーはAngleSharpの例外になります。空のHTMLや空文字の期待値は使用できます。破棄した後は検証メソッドや `Document` を使用できません。

コンポーネントやレイアウトは、既存の描画コンテキストを指定して単独で検証できます。

```csharp
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Pages;
using LithoSharp.Routing;
using LithoSharp.Testing;

var site = new SiteSettings { BaseUrl = "https://example.test/" };
using var componentDocument = SiteTestDocument.RenderComponent(new HelloComponent(), "Home", ComponentRenderingContext.Create(site));
var page = new SitePage<string>(new PageId("home"), SiteRoute.ForFile("index.html"), "Home", new PageMetadata("Home"));
using var layoutDocument = SiteTestDocument.RenderLayout(new HelloLayout(), page, PageRenderingContext.Create(site));
componentDocument.AssertText("h1", "Home");
layoutDocument.AssertText("main", "Home");

sealed class HelloComponent : ISiteComponent<string>
{
    public IHtmlContent Render(string value, ComponentRenderingContext context) => Html.UnsafeRaw($"<main><h1>{Html.Encode(value)}</h1></main>");
}
sealed class HelloLayout : IPageLayout<string>
{
    public IHtmlContent Render(SitePage<string> page, PageRenderingContext context) => Html.UnsafeRaw($"<main>{Html.Encode(page.Content)}</main>");
}
```

`SiteTestHost` は、サイト定義またはファクトリから、一時ディレクトリへサイトを生成します。出力先と各キャッシュは隔離します。無効な資産キャッシュは無効のままです。指定済みのビルド日時を維持し、未指定時には `SOURCE_DATE_EPOCH` にかかわらずUnixエポックを使います。

既定ではローカルの品質検証を有効にします。明示した失敗閾値や孤立ページの設定は維持し、外部リンクの検証は設定した場合だけ実行します。ファクトリには、`SiteFactoryContext` で実際のソースディレクトリを渡します。次の例は、TestingパッケージとDocsサンプルを参照するテストプロジェクトから、リポジトリのルートを作業ディレクトリとして実行します。

```csharp
using LithoSharp;
using LithoSharp.Testing;

await using var host = await SiteTestHost.CreateAsync(
    new DocsSampleFactory(),
    new SiteFactoryContext(Path.GetFullPath("samples/LithoSharp.DocsSample")));
host.AssertSucceeded();
host.AssertNoDiagnostics();
host.AssertRoute("/index.html", "index.html");
host.AssertNoRoute("/index/");
host.AssertArtifact("index.html");
host.AssertNoArtifact("missing.html");
using var page = await host.OpenPageAsync("/index.html");
page.AssertElement("main");
```

ディレクトリ形式のルートには末尾の `/` を含めます。ファイル形式のルートにはファイル名を指定します。組み込みサンプルのホームは `/index.html` として登録されています。`AssertLink` はデコード後の `href` をそのまま比較し、参照先やアンカーの解決はサイト全体の品質検証が担当します。

ルート、ビルド計画、品質の検証で失敗した場合、`Succeeded` はfalse、`Result` はnullになり、診断を保持します。壊れた内部リンクを意図的に検証するテストでは、`AssertSucceeded` の代わりに `host.AssertDiagnostic("LSQ001", LithoSharp.Diagnostics.SiteDiagnosticSeverity.Error)` を使います。診断の検証だけでは、生成成功を確認したことにはなりません。それ以外のファクトリ、描画、I/Oの例外とキャンセルは、後片付けの後に呼び出し元へ伝播します。

破棄時には、一時領域内で編集・追加したファイルも削除します。ソースは元の場所から読み取り、サイト定義に書かれた出力先やキャッシュへは書き込みません。シンボリックリンクやジャンクションを経由する読み取りと削除は拒否します。削除に失敗した場合は、残った一時領域を調査できます。独自のファクトリや変換処理は信頼するコードとして実行され、そのコード自身のI/Oは制限しません。メモリ上のファイルシステムやPlaywright支援は追加していません。

| 検証API | 用途 |
|---|---|
| `AssertRoute` / `AssertNoRoute` | ルート表とURLの形式を検証 |
| `AssertArtifact` / `AssertNoArtifact` | 宣言した通常ファイルの存在、または宣言と実ファイルの両方の不在を検証 |
| `AssertDiagnostic` / `AssertNoDiagnostics` | 生成診断の存在、または指定した重大度以上の診断がないことを検証 |
| `OpenPageAsync` | 生成ページを `SiteTestDocument` として開く |
