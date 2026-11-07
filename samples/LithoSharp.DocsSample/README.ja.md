# LithoSharp Docs サンプル

![生成したサンプルサイト](../../docs/images/docs-light.png)

テーマ切り替えは既定で有効です。`SiteThemeOptions.EnableThemeSwitching = false` で無効にできます。
[固定ダーク配色を含む設定例](../../docs/layout-css-contract.md#theme-switching--テーマ切り替え)を参照してください。

標準のDocsテンプレートは石版をイメージした見出しと配色を採用しています。
ライト・ダークの切り替えはヘッダーから行えます。選択はブラウザーに保存され、
未選択の場合はOSの配色設定に従います。

## C#の型定義から生成物の検査まで

.NET 10 SDKを使い、リポジトリのルートで実行します。同じ`ArticleFrontMatter`モデルを、生成したbinder、`typed-content/`のコンパイル時入力、実行時の読み込み、記事レイアウトで使います。`GeneratedArticles`はコレクションのカタログと型付き参照を提供します。レイアウトは生成した参照で2件目の記事へリンクし、トピックページは読み込んだエントリーを使います。GeneratorとAnalyzerはコンパイル時だけの依存です。

[`AdditionalFiles`の宣言](LithoSharp.DocsSample.csproj)はcollection、entry ID、routeをコンパイルへ渡します。[`SiteFactory.cs`](SiteFactory.cs)は読み込みに`GeneratedArticles.Binder`、関連記事のリンクに`GeneratedArticles.Entries.Typed_Content_Second.GetUrl(context.Site.BaseUrl)`を使います。IDとrouteはコンパイラーの検査と同じ宣言に基づきます。自分のサイトへ適用する場合は[Generatorの設定](../../docs/source-generators.md)を参照してください。

1. 正常なサイトを生成し、出力を検査します。

   ```powershell
   dotnet run --project samples/LithoSharp.DocsSample -- --check --output .local/docs-walkthrough
   dotnet run --project src/LithoSharp.Tool -- check-output --output .local/docs-walkthrough --base-url https://example.com/ --format json
   ```

   `--check`は生成中のstagingを検査し、`check-output`はサイトを生成せずに現在のファイルを読みます。サイトルートからのリンクをたどるには、開発サーバーを起動してください。この操作は信頼するサンプルのfactoryをビルドします。

   ```powershell
   dotnet run --project src/LithoSharp.Tool -- serve samples/LithoSharp.DocsSample --output .local/docs-walkthrough --port 4328
   ```

   <http://127.0.0.1:4328/articles/first/>を開き、型付き参照で作った記事リンクをたどります。HTMLファイルを`file://`で直接開くと、サイトルートからのリンクを正しく解決できません。次の手順で意図的にコンパイルエラーを起こす前に、Ctrl+Cでサーバーを停止してください。

2. `samples/LithoSharp.DocsSample/typed-content/first.md`から`title:`の行だけを削除し、サンプルをビルドします。

   ```powershell
   dotnet build samples/LithoSharp.DocsSample
   ```

   C#の必須メンバーに対応する値がないため、サイト生成前に`LSG006`が出ます。行を戻してから`published_from`を`not-a-date`に変えると、型変換の診断も確認できます。[missing-title.md](examples/missing-title.md)と[wrong-type.md](examples/wrong-type.md)は、それぞれの不正な入力を示す例です。通常のコンテンツとコンパイル時入力からは除外しています。

3. front matterを元に戻し、`--check`を付けて再生成します。エディターで`samples/LithoSharp.DocsSample/typed-content/first.md`を開き、見出しのアウトラインや診断位置への移動も試してください。[VS Code拡張](../../extensions/lithosharp-vscode/README.ja.md)が編集を支援します。プロジェクトの文脈がない構文上の移動機能では、独自C#スキーマやサイトのルートは検証できません。プロジェクトのsnapshotがあっても、編集時のスキーマ検査は組み込み型だけです。独自C#スキーマはコンパイルとサイトビルドで確認します。Markdownのアウトラインは見出しをジャンプ先とし、symbolの範囲にはセクション全体を使います。MDXのアウトラインは別の見出し処理であり、Markdownのセクション範囲を引き継ぎません。

参照の矛盾を試すには、[conflicting-reference.cs.txt](examples/conflicting-reference.cs.txt)をサンプル内へ`ConflictingReferenceExample.cs`としてコピーし、ビルドします。完全で最新の同じカタログ内で、宣言済みの`first.md`に別のルートを組み合わせた矛盾を`LSA1402`が示します。通常のリンクには生成された参照を使ってください。確認後は一時的な`.cs`ファイルを削除します。[dynamic-reference.cs.txt](examples/dynamic-reference.cs.txt)はルートを実行時に受け取るため、コンパイラーでは値を確定できません。矛盾の診断がないことは、ルートが正しいという証明にはなりません。

生成後の問題を試すには、生成した`articles/first/index.html`の`</body>`の前へ[broken-output.html](examples/broken-output.html)を挿入し、`check-output`を再実行します。内部の参照先がないため`LSQ001`が出ます。サイトを再生成するとファイルを元に戻せます。この検査は実際の出力バイト列を読みますが、現在の原稿との一致は証明しません。[生成済み成果物の検査](../../docs/generated-output-checks.ja.md)を参照してください。

| 問題や情報 | 段階 | 判断できる範囲 |
| --- | --- | --- |
| 必須titleの不足や不正な日付 | Generator: `LSG006` | 明示したコンパイル時入力の必須項目や型変換のエラーです。動的に見つかったファイルは検証しません。 |
| 既知のエントリーIDと異なる宣言済みルートの組み合わせ | Analyzer: `LSA1402` | 完全で最新のカタログ（`Closed`）内の矛盾です。動的な値は未検証です。 |
| 見出しと原文位置の診断 | Editor/LSP | 現在の文書の構文情報です。`NoContext`ではプロジェクト、スキーマ、ルートの検査はできません。 |
| 不完全な解析（`Partial`） | 解析・編集中 | 取得できた情報だけを使います。情報がないことから、コンテンツの不存在や妥当性は判断できません。 |
| 入力や解析資源の上限による情報の取得不能 | Editor: `LSM013` | 解析が停止しており、取得できない情報は未検証です。入力の上限は8 Mi UTF-16コード単位です。 |
| 解析対象のpost本文の情報を取得不能 | 生成: `LSM013` | 本文入力の上限は1 Mi UTF-16コード単位です。失敗時は公開を停止し、以前の出力を保ちます。描画だけの断片はこの解析経路を使いません。 |
| headerの区切りを取得不能 | Generator: `LSG002`、runtime: `LSM013` | header専用走査の上限は1 Mi UTF-16コード単位です。`Unknown`からheaderの不存在や未閉鎖は確定できません。閉じ区切り後の本文は上限に含めません。 |
| 生成物の内部参照先やanchorの不足 | 生成時の品質検査・`check-output`: `LSQ001`/`LSQ002` | 検査したローカル成果物とanchorの存在を確認します。外部HTTP検査は既定で無効です。 |

CIは正常なサンプルのビルドと`--check`付き実行に加え、公開している不正な入力例をGenerator、Analyzer、生成物検査で確認します。単なるプロセスの失敗ではなく、診断IDと検出段階を検査します。Markdown構文と原文範囲はSyntamarkが担当し、C#スキーマ、ルート、表示方針、製品の診断はLithoSharpが担当します。既定のMarkdown profileは`lithosharp-markdown/1`です。

`DocsSampleFactory` を使って、サイトをテストプロセス内で生成できます。
出力先とキャッシュは一時領域へ隔離され、外部リンクの検証は既定で無効です。
テストプロジェクトからこのサンプルと `LithoSharp.Testing` を参照し、
次の例をリポジトリのルートで実行します。

## ファクトリのテスト

```csharp
using LithoSharp;
using LithoSharp.Testing;

await using var host = await SiteTestHost.CreateAsync(
    new DocsSampleFactory { AssetDemo = true },
    new SiteFactoryContext(Path.GetFullPath("samples/LithoSharp.DocsSample")));
host.AssertSucceeded();
host.AssertRoute("/index.html", "index.html");
using var page = await host.OpenPageAsync("/index.html");
page.AssertElement("main");
```

`AssetDemo = true` では、画像変換と独自の記事レイアウトも検証できます。
検証失敗時は `Succeeded` がfalse、`Result` がnullになり、診断を保持します。
描画処理そのものの例外は呼び出し元へ伝播します。
各検証APIとエラー条件は[テストガイド](../../docs/testing.ja.md)を参照してください。
