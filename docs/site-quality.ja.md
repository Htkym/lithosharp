# サイトの品質検査

品質検査は成果物をステージング領域に書いた後、出力トランザクションを確定する前に実行します。
生成ごとに `SiteGenerationOptions.Quality` で有効にします。
`null` の場合は品質検査を行わず、品質検査器からのネットワーク通信もありません。
検証済みの完全な無変更ビルドでは、ステージング用のコピーを作らず、公開済みのツリーをその場で検査する場合があります。
その場合も、現在のバイト列とサイト全体の規則を検査します。

これは、コンパイルとコンテンツの読み込みの後に行う、生成サイトの検査です。
C# のスキーマと型付き参照が有効でも、レイアウトが作る壊れたリンクやアンカーを検出できます。
[Docs サンプル](../samples/LithoSharp.DocsSample/README.ja.md)は不正なフィクスチャを分離して各段階を示します。
プロジェクトのコンパイルや再生成をせず、既存ファイルを調べるには、[`check-output`](generated-output-checks.ja.md) を使います。

```csharp
using LithoSharp.Diagnostics;
using LithoSharp.Quality;

var options = new SiteGenerationOptions
{
    Quality = new SiteQualityOptions(
        failureThreshold: SiteDiagnosticSeverity.Error,
        checkOrphans: true)
};

try
{
    var result = await generator.GenerateWithOptionsAsync(
        site, posts, output, clean: true, customization, options, cancellationToken);
    Console.WriteLine(result.QualityReport.Format(SiteDiagnosticFormat.Text));
}
catch (SiteQualityValidationException exception)
{
    Console.Error.WriteLine(exception.Report.Format(SiteDiagnosticFormat.Text));
    return 1;
}
```

失敗する重大度の既定値は `Error` です。`Warning` にすると、警告でもビルドを失敗させます。
検査が失敗しても、以前に確定した出力は維持します。
`SiteQualityReport.Format` は、`Text`、`Json`、SARIF 2.1.0 に対応します。

最終 HTML には、宣言した `.html`・`.htm` アセットと、部分ビルドで保持するページも含めます。
パーサーの不完全な対応範囲、処理上限、キャンセル、検査後のバイト列の変更は公開を停止します。
以前に公開した出力は引き続き利用できます。

HTML の局所的な観測値は、内容のハッシュと、解析・投影処理のバージョンをキーに、出力の既存ビルドキャッシュの区画に保存します。
参照、アンカー、canonical URL、サイト全体のすべての規則は、毎回、現在のビルドに対して検査します。
キャッシュヒットは品質の判定結果を再利用したという意味ではありません。
この再利用を無効にするには、`SiteGenerationOptions.HtmlFactsCacheEnabled = false` を指定します。
`QualityReport` の `HtmlParseCount` と `HtmlFactsCacheHitCount` は、最終 HTML の解析と再利用の件数です。
品質検査が無効なら、どちらも 0 です。

キャッシュの上限は、出力ごとに 128 レコード・32 MiB、1 レコードあたり 1 MiB です。
保存待ちのレコードにも同じ上限を適用し、ビルド後に解放します。ツリーやキャンセルトークンは保持しません。
公開に成功すると局所的な観測値を保存し、古いレコードを削除します。
欠落、破損、異なるバージョン、上限超過、利用できないレコードは、再解析します。
必要な作業対象全体がキャッシュに残る無変更ビルドでは、最終 HTML の解析はありません。
出力のバイト列はそれでも読み込んで照合します。
`SiteGenerator.ClearCache` は、この区画を消去します。ほかのビルドキャッシュの保持方針は従来どおりです。

外部 HTTP 検査は既定で無効です。有効にする場合は、サイトの出力ディレクトリ外のキャッシュパスを指定します。

```csharp
Quality = new SiteQualityOptions(
    externalLinks: new ExternalLinkCheckOptions(
        cacheFilePath: Path.Combine(cacheDirectory, "external-links.json")))
```

バージョン付きキャッシュの既定の有効期間は 24 時間です。
同一プロセスの要求間隔は 500 ms 以上、タイムアウトは 10 秒で、三つとも設定できます。
リダイレクトを最大 10 回たどり、HEAD が HTTP 405 または 501 の場合だけ GET で再試行します。
プライベート・予約・移行用のアドレスを拒否し、IPv6 はグローバルユニキャストに限定します。
DNS の結果を検査してから、選んだアドレスへ直接接続します。キャンセルで検査を停止します。
キャッシュが破損していれば再検査し、書き込めなくても今回の診断を破棄しません。

検査器は、内部 URL、フラグメント、canonical URL、SEO 項目、重複タイトル、
ホームから到達できないページ、未使用の登録アセット、任意の外部 HTTP リンクを検査します。
HTML の URL 属性と、CSS のリテラルの `url(...)` を調べます。
登録した `.css` アセットには有効な UTF-8 が必要で、不正なバイト列は確定前に生成を中止します。
生成する URL など、動的な CSS の URL は推測できないため、検査しません。

ソーシャル画像のソースがない場合、組み込みレイアウトは画像のメタデータを省き、Twitter カードには `summary` を使います。
明示した独自の画像 URL は、引き続き利用できます。
これにより、以前の出力が生成されていない画像を参照していた問題を修正します。

| ID | 重大度 | 規則 |
|---|---|---|
| `LSQ001` | Error | 不正・未対応の URL、リソース必須箇所の空 URL、欠落した内部成果物への参照。 |
| `LSQ002` | Error | フラグメントが、存在しないアンカーを参照する。 |
| `LSQ003` | Error | HTML ページが、公開ルートに対応する canonical URL を正確に一つ持たない。 |
| `LSQ004` | Error | リダイレクトが循環する。 |
| `LSQ005` | Error | リダイレクトが別のリダイレクトを参照する。最終ルートを直接指定する。 |
| `LSQ006` | Error | リダイレクトの参照先が存在しない。 |
| `LSQ007` | Warning | ホームからページに到達できない。 |
| `LSQ008` | Warning | 登録したアセットに HTML・CSS の参照がない。 |
| `LSQ009` | Warning | 複数ページが同じ空でないタイトルを使う。 |
| `LSQ010` | Warning | 必須の SEO 項目が欠落、または空。 |
| `LSQ011` | Warning | 任意の外部 HTTP 検査が失敗した。 |

リダイレクトは、所有するビルド成果物であり、ルート競合検査に参加します。
元のルートは一意で、ほかの出力と競合できません。参照先は存在する必要があります。
品質検査が無効な場合も、リダイレクトの連鎖と循環を拒否します。

```csharp
using LithoSharp.Routing;

var options = new SiteGenerationOptions
{
    Redirects =
    [
        new SiteRedirect(
            SiteRoute.ForFile("old-home.html"),
            SiteRoute.ForDirectoryIndex(""))
    ]
};
```
