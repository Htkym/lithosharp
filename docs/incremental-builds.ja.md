# 差分ビルド

`SiteGenerationOptions.BuildCacheDirectory` はビルドキャッシュの場所を指定します。`null` の場合は出力ディレクトリの隣にある `.lithosharp` を使い、出力の識別子ごとに分けて保存します。キャッシュは出力ディレクトリの外に置いてください。

`MaxDegreeOfParallelism` の既定値は `1` です。拡張機能は既定では直列実行され、コンテンツコレクションが `IsThreadSafe` を設定した場合だけページを並列化できます。レンダラーをキャッシュ可能にするには、明示的な `RendererFingerprint` も必要です。LithoSharp は fingerprint にレンダラーの実際のアセンブリとメソッド識別子を加えます。出力に影響する捕捉設定、テンプレート、その他の動作は fingerprint またはビルド入力に含めてください。

ソースコレクションの `IsCacheable` と、レンダラーのフィンガープリントの両方が必要です。型付きレンダラーはビルド時刻を参照できるため、時刻が変わるとソースが同じでも再生成します。変更のないページの描画を省略するには、同じ `BuildTimestamp` または `SOURCE_DATE_EPOCH` を指定してください。`DateTime.UtcNow` などの未宣言の値を読むレンダラーはキャッシュ可能と宣言しないでください。`clean: true` は常に再実行します。

`clean: false` では、ノードのキー、成果物の宣言、ステージング内のファイル、必要な描画済み本文を検証してから再利用します。破損・欠落・不一致があれば再生成します。削除の判断には既存の所有情報だけを使います。先に変更不能なキャッシュ記録を保存し、そのハッシュを出力マニフェストに記載して、既存の出力トランザクションで確定します。失敗したビルドは以前の出力と参照先を保持します。キャッシュが使えても、安全な確定に必要なステージングへの複製とファイル検証は行います。

`SiteBuildReport.Nodes` の各ノードには `CacheHit` と `CacheMissReason` があり、レポートには `CacheHitCount` と `CacheMissCount` があります。生成・省略した成果物の一覧から、今回の実行と再利用を区別できます。互換性のため、`GeneratedFiles` は公開したすべての成果物を返します。依存関係が不明な旧テンプレートは、ページをキャッシュせず毎回描画します。OGP画像は選択したフォントとSkiaSharp実行環境も識別し、識別できない場合は再生成します。

資産変換は、ページの計画より前にハッシュ付きの出力ルートを確定します。変換キャッシュは従来どおり `AssetCacheDirectory` で指定し、`null` なら使いません。変換が実行された場合は、以前と同じバイト列でもミスとして報告します。変換キャッシュが使える場合も入力検証は実行します。キャッシュの書込みに失敗すると生成を中止し、以前の出力を保持します。キャッシュ記録は出力の外に蓄積されます。容量を回収する場合は、生成が動いていないときに対象出力のキャッシュ保存先を削除してください。

次の例では、`articles` は `isCacheable: true` で読み込んだ `ContentCollection<ArticleFrontMatter, string>`、`ArticleLayout` は `IPageLayout<ContentEntry<ArticleFrontMatter, string>>` の実装です。

```csharp
var collection = new SiteContentCollection<ArticleFrontMatter, string>(articles, new ArticleLayout())
{
    RendererFingerprint = "article-layout:v1", // 捕捉した設定値も含めます。
    IsThreadSafe = true
};
var options = new SiteGenerationOptions
{
    ContentCollections = [collection],
    BuildTimestamp = DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
    MaxDegreeOfParallelism = 4
};
var result = await new SiteGenerator().GenerateWithOptionsAsync(
    site, posts, output, clean: false, customization, options, cancellationToken);
```
