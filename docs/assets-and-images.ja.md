# 資産と画像

既存の`SiteAsset`のURLは維持されます。`AssetUrl.Fingerprint`はSHA-256の16進文字列、
`Integrity`は`sha256-`にBase64形式のハッシュを続けた値です。
`SiteGenerationOptions.PublicDirectory`を指定すると、通常のファイルを相対パスのままコピーします。
存在しないディレクトリ、シンボリックリンク、不正なパス、ルートの衝突は出力確定前に拒否します。
公開ファイルの入力ディレクトリと出力先は重ねられません。資産の入力ファイルも出力先の外に置きます。
資産と外部リンクのキャッシュは公開ファイルの入力ディレクトリの外に置いてください。

## 変換とCSSの参照

```csharp
using System.Text;
using LithoSharp;

var logo = new SiteAsset("logo", "assets", "logo.png", "images/logo.png");
var css = new SiteAssetOutput("brand-css", "styles/brand.css");
var transform = new SiteAssetTransform("brand-css", "brand-css/v1", [logo], [css],
    (context, token) => context.WriteAsync(css,
        Encoding.UTF8.GetBytes($".brand {{ background-image: url('{context.GetUrl(logo).Value}'); }}"), token));
var options = new SiteGenerationOptions
{
    Assets = [logo],
    AssetTransforms = [transform],
    AssetCacheDirectory = ".lithosharp/assets"
};
```

`OpenRead(input)`は入力の読取り専用スナップショット、`GetUrl(input)`は公開URLを返します。
`WriteAsync`で各出力を一度だけ書き込みます。描画時は`context.Assets.GetUrl(css)`で参照できます。
登録・変換・描画には同じ宣言オブジェクトを使います。IDやパスの重複、未宣言の入出力、出力漏れはエラーです。
変換結果の`AssetUrl.Asset`は互換性のための宣言であり、入力パスに元ファイルはありません。
変換結果は`SiteAssetOutput`で参照してください。`BuildNodes`では入力、依存関係、所有成果物を確認できます。

ハンドラーは同じ入力から同じ結果を返す必要があります。未宣言のファイル、ネットワーク、時刻、
共有の可変状態を読まないでください。コンテキストのAPIは宣言を検証しますが、任意のC#コードを隔離する機能ではありません。
実装・設定・ツールの依存関係を`implementationFingerprint`に含めます。
任意の`validateInputs`コールバックはキャッシュ参照前に毎回実行され、外部ツールの変更などを検証できます。

`AssetCacheDirectory`は明示指定で有効になり、出力ディレクトリの外に置く必要があります。
入力の内容とURL、出力宣言、実装と設定の指紋が一致すると変換処理を省略します。
キャッシュの内容はハッシュを検証し、欠落・破損・不一致があれば再生成します。
キャッシュを根拠に既存成果物を削除することはありません。
変更のない資産は最後の書込みを省略します。失敗時に元の出力を保持するため、
トランザクション開始時のステージングへの複製は残ります。

## 画像

```csharp
using LithoSharp.Images;

var image = new ImageAsset(
    new SiteAsset("photo", "assets", "photo.jpg", "images/original.jpg"),
    [new ImageVariant("photo-jpeg", "images/photo.jpg", 640, ImageFormat.Jpeg),
     new ImageVariant("photo-webp", "images/photo.webp", 640, ImageFormat.WebP)]);
var options = new SiteGenerationOptions
{
    Assets = [image.Source],
    AssetTransforms = [image.Transform],
    AssetCacheDirectory = ".lithosharp/assets"
};
// レイアウト内: ResponsiveImage.Render(context.Assets, image, "夜明けの湖")
```

PNGかJPEGの代替画像が必要です。形式と幅の組は重複できず、幅は正、品質は0〜100で指定します。
高さは縦横比から計算します。`ResponsiveImage.Render`は属性をエスケープし、
代替画像の幅と高さ、形式別の`source`、`srcset`、`sizes`、`alt`、遅延読み込みを出力します。
`lazy: false`で即時読み込みにできます。読めない画像は生成に失敗します。
SVGをそのまま公開する場合は通常の`SiteAsset`を使います。

PNG／JPEG／WebPはSkiaで生成し、外部実行ファイルは不要です。
キャッシュには変換設定、Skiaのマネージド版とネイティブ版、実行環境の指紋を含めます。
AVIFだけは、信頼する`avifenc`のパスと依存コーデックの版を明示します。

```csharp
var encoder = new ExternalAvifEncoder("/opt/libavif/bin/avifenc",
    dependencyFingerprint: "libavif-1.3.0+aom-3.12.1", timeout: TimeSpan.FromMinutes(2));
var avifImage = new ImageAsset(image.Source,
    [image.Variants[0], new ImageVariant("photo-avif", "images/photo.avif", 640, ImageFormat.Avif)], encoder);
```

依存関係の指紋には実際に導入した版を指定してください。PATH探索、ダウンロード、シェル経由の実行は行いません。
実行ファイルの内容と設定をハッシュに含め、キャッシュがある場合もファイルの置換を確認します。
実行時間を制限し、出力のAVIFブランドも検証します。ツールやコーデックを更新したら宣言を作り直してください。
詳細は[avifencの公式マニュアル](https://github.com/AOMediaCodec/libavif/blob/main/doc/avifenc.1.md)を参照してください。
Coreの生成にはこのツールは不要です。Docsサンプルの`--asset-demo --check`で公開ファイルとPNG／WebPを確認できます。
