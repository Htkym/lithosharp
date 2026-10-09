# Site quality checks / サイト品質検査

## English

Quality checks run after artifacts are written to staging and before the output
transaction is committed. Enable them per generation with
`SiteGenerationOptions.Quality`; leaving it `null` performs no quality checks and
no network requests.

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

The default failure threshold is `Error`. Set it to `Warning` to fail a build on
warnings as well. A failed check leaves the previously committed output intact.
Reports support `Text`, `Json`, and SARIF 2.1.0 through
`SiteQualityReport.Format`.

Final HTML includes declared `.html`/`.htm` assets and retained pages in subset
builds. Incomplete parser coverage, parser limits, cancellation, or bytes changing
after inspection stop publication. Previously published output remains available.

Local HTML observations are cached by content hash and parser/projection version
in the output's existing build-cache partition. References, anchors, canonical URLs,
and all site-wide rules are checked against the current build on every run. A cache
hit is not a cached quality verdict. Set `HtmlFactsCacheEnabled = false` to disable
this reuse. `HtmlParseCount` and `HtmlFactsCacheHitCount` on `QualityReport` count
final HTML parsing and reuse; both are zero when quality checks are disabled.

The cache admits at most 128 records and 32 MiB per output, with a 1 MiB record
limit. Pending records have the same bounds and are released after the build;
trees and cancellation tokens are not retained. Successful publication saves local
observations and evicts older records. Missing, corrupt, foreign-version, oversized,
or unavailable records cause a fresh parse. A no-op build parses no final HTML when
its complete working set remains in this cache; output bytes are still read and
verified. `SiteGenerator.ClearCache` clears this partition. Other build-cache data
keeps its existing retention policy.

External HTTP checks are off by default. Opt in with a cache path outside the
site output directory:

```csharp
Quality = new SiteQualityOptions(
    externalLinks: new ExternalLinkCheckOptions(
        cacheFilePath: Path.Combine(cacheDirectory, "external-links.json")))
```

The versioned cache lasts 24 hours by default. Requests are at least 500 ms apart
within the process and time out after 10 seconds; all three settings are configurable.
The checker follows at most ten redirects and retries HEAD with GET only for HTTP
405 or 501. Private, reserved, and transition addresses are rejected; IPv6 is limited
to global unicast. DNS results are checked before connecting directly to the selected
address. Cancellation stops the check. Cache corruption causes a fresh check, and an
unwritable cache does not discard the live diagnostics.

The checker validates internal URLs, fragments, canonical URLs, SEO fields,
duplicate titles, pages unreachable from the home page, unused registered
assets, and optional external HTTP links. It inspects HTML URL attributes and
literal `url(...)` values in CSS. Registered `.css` assets must be valid UTF-8;
invalid bytes abort generation before commit. Generated or otherwise dynamic
CSS URLs cannot be inferred and are not inspected.

Built-in layouts omit image metadata and use the `summary` Twitter card when no
social-image source is available. Explicit custom image URLs remain supported.
This corrects the earlier output's references to images that were never generated.

| ID | Severity | Rule |
| --- | --- | --- |
| `LSQ001` | Error | A URL is invalid, unsupported, empty where a resource is required, or points to a missing internal artifact. |
| `LSQ002` | Error | A fragment points to an anchor that does not exist. |
| `LSQ003` | Error | An HTML page does not have exactly one canonical URL for its public route. |
| `LSQ004` | Error | A redirect participates in a cycle. |
| `LSQ005` | Error | A redirect targets another redirect; target the final route directly. |
| `LSQ006` | Error | A redirect target does not exist. |
| `LSQ007` | Warning | A page is unreachable from the home page. |
| `LSQ008` | Warning | A registered asset has no HTML or CSS reference. |
| `LSQ009` | Warning | Multiple pages use the same non-empty title. |
| `LSQ010` | Warning | A required SEO field is missing or empty. |
| `LSQ011` | Warning | An optional external HTTP check failed. |

Redirects are owned build artifacts and participate in route collision checks.
Sources must be unique and must not collide with another output. Targets must
exist, and redirect chains and cycles are rejected even when quality checks are
disabled.

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

## 日本語

品質検査は、成果物をステージング領域へ書き込んだ後、出力を確定する前に実行します。
生成ごとに `SiteGenerationOptions.Quality` で有効にします。`null` の場合は品質検査も
ネットワーク通信も行いません。

```csharp
using LithoSharp.Diagnostics;
using LithoSharp.Quality;

var options = new SiteGenerationOptions
{
    Quality = new SiteQualityOptions(
        failureThreshold: SiteDiagnosticSeverity.Warning,
        checkOrphans: true)
};
```

既定では `Error` 以上の診断で生成に失敗します。`Warning` を指定すると警告も失敗として
扱います。検査が失敗しても、前回確定した出力は維持されます。レポートは
`SiteQualityReport.Format` により `Text`、`Json`、SARIF 2.1.0 の各形式で出力できます。

最終 HTML の検査には、宣言済みの `.html`／`.htm` 資産と部分ビルドで保持するページも
含まれます。解析器の未対応範囲や処理上限、キャンセル、検査後の bytes の変更を
検出した場合は公開を止め、前回公開した出力を維持します。

HTML の局所的な観測値は、内容ハッシュと解析・観測処理の実装版をキーにして、
出力別の既存 build cache に保存します。参照先、anchor、canonical URL、サイト全体の
規則は毎回現在の計画で検査します。再利用を無効にする場合は
`HtmlFactsCacheEnabled = false` を指定します。`QualityReport.HtmlParseCount` と
`HtmlFactsCacheHitCount` は最終 HTML の解析件数と再利用件数です。検査未実施時は
どちらも 0 です。

出力ごとに最大 128 レコード・32 MiB、1 レコードあたり最大 1 MiB を保存します。保存待ちの
レコードも同じ上限を持ち、生成後に解放します。tree やキャンセルトークンは保持しません。
公開が成功した後に保存し、古いレコードを削除します。欠落、破損、実装版の不一致、
上限超過、読み書きの失敗は再解析で回復します。必要な観測値が全て cache に残る
無変更ビルドでは、最終 HTML の解析は 0 件になります。出力 bytes の読み取りと
照合は省略しません。`SiteGenerator.ClearCache` で出力別の cache を削除できます。
その他の build cache の保持方針は従来どおりです。
失敗時は `SiteQualityValidationException.Report` から同じレポートを取得できます。

外部 HTTP リンク検査は既定で無効です。有効にする場合は、出力ディレクトリの外にある
キャッシュファイルを指定します。

```csharp
Quality = new SiteQualityOptions(
    externalLinks: new ExternalLinkCheckOptions(
        cacheFilePath: Path.Combine(cacheDirectory, "external-links.json")))
```

キャッシュの有効期間は既定で24時間、通信間隔は同一プロセス内で500ミリ秒以上、
タイムアウトは10秒です。これらは設定で変更できます。最大10回のリダイレクトをたどり、
HEADがHTTP 405または501を返した場合だけGETで再試行します。プライベートアドレスや
予約・移行用アドレスへの接続は拒否し、IPv6はグローバルユニキャストに制限します。
DNSの結果を検査してから、そのIPアドレスへ接続します。キャンセルで検査を中止できます。
キャッシュが壊れている場合は再検査し、保存できない場合も今回の診断は返します。

検査対象は、内部 URL、フラグメント、canonical URL、SEO 項目、重複タイトル、ホームから
到達できないページ、未使用の登録済みアセット、任意の外部 HTTP リンクです。HTML の URL
属性と CSS のリテラルな `url(...)` を調べます。登録済みの `.css` アセットは正しい UTF-8
である必要があり、不正なバイトがある場合は出力確定前に生成を中止します。生成時に組み立てる
など、動的な CSS URL は推測できないため、検査対象になりません。

組み込みレイアウトは、元になる画像がない場合に画像メタデータを省き、Twitterカードを
`summary` にします。明示した独自の画像URLは引き続き使用できます。これにより、従来の
出力が生成されていない画像を参照していた不具合を修正しました。

診断 ID と重大度は上の表のとおりです。`LSQ001` から `LSQ006` は URL、アンカー、
canonical、リダイレクトのエラーです。`LSQ007` から `LSQ011` は孤立ページ、未使用アセット、
重複タイトル、SEO 項目、外部リンクの警告です。

リダイレクトは所有権を持つ通常のビルド成果物であり、ルート競合検査の対象です。source は
一意で、ほかの出力と競合できません。target は存在する最終ルートを指定します。チェーンと
循環は、品質検査を無効にしている場合も拒否されます。設定例は英語節の
`SiteRedirect` コードを参照してください。
