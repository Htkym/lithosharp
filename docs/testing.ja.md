# サイトのテスト

このガイドは LithoSharp.Testing 3.0.0 を説明します。
LithoSharp の各パッケージを同じバージョンで使い、公開状態は[バージョン一覧](../eng/version-matrix.json)で確認してください。
3.0 の変更は[移行](migration-3.0.ja.md)を参照してください。
1.x の `Document` getter から更新する場合は、以前のソースとバイナリの破壊的変更を残した [2.x の履歴資料](migration-2.0.ja.md)も確認してください。

利用するLithoSharpパッケージと同じバージョンを参照してください。

```sh
dotnet add package LithoSharp.Testing --version 3.0.0
```

`LithoSharp.Testing` はテストフレームワークに依存しない検証 API と、読み取り専用の HTML スナップショットを提供します。ブラウザーを起動せず、スクリプトの実行やネットワークへの接続も行わずに HTML を解析します。対応するセレクターと移行例は [スナップショット API の仕様](Testing-v2-api.ja.md)を参照してください。

これらのテストはコンパイラーやスキーマの診断、エディターの検査を補います。
描画済みHTMLと生成ルートを検査するもので、`NoContext`、`Partial`な解析、`Unknown`なカタログの根拠を、それ以前の工程で検証済みの結果に変えるものではありません。
[Docsサンプル](../samples/LithoSharp.DocsSample/README.ja.md)は型付きコンテンツ、コンパイル、生成、出力検査を一つのプロジェクトでつないでいます。

```csharp
using LithoSharp;
using LithoSharp.Testing;

using var document = SiteTestDocument.Parse("<main><h1>Home</h1><p>&lt;safe&gt;</p><meta name='description' content='Description'></main>");
document.AssertElement("main");
document.AssertText("h1", "Home");
document.AssertText("p", "<safe>");
document.AssertMeta("description", "Description");
```

`AssertText` は最初に一致した要素の `TextContent` を期待値と完全一致で比較します。文字参照は解析時にデコードされます。属性、メタデータ、リンク、画像も、引用符や改行を含めた値の完全一致で検証します。要素が見つからない場合や値が異なる場合は `SiteTestException` が発生します。

null、空白のセレクター、不正な属性名には標準の引数例外が発生します。セレクターの構文エラーには `HtmlSelectorSyntaxException`、未対応の機能には `HtmlSelectorUnsupportedException` が発生します。空の HTML や空文字の期待値は使用できます。HTML ツリーの解析が不完全な場合は、スナップショットを公開する前に例外が発生します。文書を破棄すると、検証メソッド、`Snapshot`、保持していた要素を使用できなくなります。

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

`SiteTestHost` はサイト定義またはファクトリから一時ディレクトリへサイトを生成します。出力先と各キャッシュは隔離します。無効な資産キャッシュは無効のままです。指定済みのビルド日時を維持し、未指定時には `SOURCE_DATE_EPOCH` にかかわらずUnixエポックを使います。

既定ではローカルの品質検証を有効にします。明示した失敗閾値や孤立ページの設定は維持し、外部リンクの検証は設定した場合だけ実行します。ファクトリには `SiteFactoryContext` で実際のソースディレクトリを渡します。次の例はTestingパッケージとDocsサンプルを参照するテストプロジェクトから、リポジトリのルートを作業ディレクトリとして実行します。

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

ルート、ビルド計画、品質の検証で失敗した場合、`Succeeded` はfalse、`Result` はnullになり、診断を保持します。壊れた内部リンクを意図的に検証するテストでは、`AssertSucceeded` の代わりに `host.AssertDiagnostic("LSQ001", LithoSharp.Diagnostics.SiteDiagnosticSeverity.Error)` を使います。診断の検証だけでは生成成功を確認したことにはなりません。それ以外のファクトリ、描画、I/Oの例外とキャンセルは、後片付けの後に呼び出し元へ伝播します。

破棄時には一時領域内で編集・追加したファイルも削除します。ソースは元の場所から読み取り、サイト定義に書かれた出力先やキャッシュへは書き込みません。シンボリックリンクやジャンクションを経由する読み取りと削除は拒否します。削除に失敗した場合は、残った一時領域を調査できます。独自のファクトリ、レンダラー、変換処理は信頼するコードとして実行され、そのコード自身のI/Oは制限しません。ホストはサンドボックスではありません。メモリ上のファイルシステムやPlaywright支援は追加していません。

| 検証API | 用途 |
|---|---|
| `AssertRoute` / `AssertNoRoute` | ルート表とURLの形式を検証 |
| `AssertArtifact` / `AssertNoArtifact` | 宣言した通常ファイルの存在、または宣言と実ファイルの両方の不在を検証 |
| `AssertDiagnostic` / `AssertNoDiagnostics` | 生成診断の存在、または指定した重大度以上の診断がないことを検証 |
| `OpenPageAsync` | 生成ページを `SiteTestDocument` として開く |
