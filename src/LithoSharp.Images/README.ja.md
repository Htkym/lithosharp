# LithoSharp.Images

[English](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Images/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Images/README.ja.md)

LithoSharp サイトに、明示的に指定しキャッシュする PNG 変換を追加します（.NET 10）。Core はこのパッケージやデコーダーを使わずに、任意の形式の既存画像を登録・コピーし、レスポンシブ画像のマークアップを生成できます。PNG の処理が必要な場合だけ Images の変換を登録してください。

Core と Images は同じバージョンを使用します。宣言した出力を解決できるように、生成とレンダリングには同じ `ImageAsset` 宣言のインスタンスを登録してください。

```csharp
using LithoSharp;
using LithoSharp.Images;
using System.IO.Compression;

var small = new ImageVariant("small", "images/photo-small.png", 320);
var large = new ImageVariant("large", "images/photo-large.png", 640, CompressionLevel.Optimal);
var image = new ImageAsset(
    new SiteAsset("photo", "assets", "photo.png", "images/original.png"),
    [small, large], fallback: large);
var options = new SiteGenerationOptions
{
    Assets = [image.Source],
    AssetTransforms = [image.Transform],
    AssetCacheDirectory = ".lithosharp/assets"
};
// In a layout: ResponsiveImage.Render(context.Assets, image.Descriptor, "Lake at dawn")
```

Core のレンダラーは alt テキストと属性をエスケープし、picture、形式別の source、srcset、sizes、画像固有の寸法、loading、decoding 属性を出力します。フォールバックは既存のバリアントから明示的に選択する必要があります。不明な寸法は不明のままです。既存の画像には Core だけを使用できます。

```csharp
var webp = new ImageSource("https://cdn.example.test/photo.webp",
    new ImageMetadata(640, 320, "image/webp", ImageMetadataProvenance.Declared));
var jpeg = new ImageSource("https://cdn.example.test/photo.jpg",
    new ImageMetadata(640, 320, "image/jpeg", ImageMetadataProvenance.Declared));
var existing = new ImageDescriptor([webp, jpeg], fallback: jpeg);
// ResponsiveImage.Render(context.Assets, existing, "Lake at dawn")
```

`ImageSource` は登録済みの `SiteAsset` または `SiteAssetOutput` も受け取ります。呼び出し元が与えるソースの寸法は `Declared` です。画像をコピーしても、その画像をデコードできることの証明にはなりません。Images の変換を使わずに、登録、フィンガープリント、ビルド依存を維持するには、ソースアセットで寸法を宣言します。

```csharp
var smallAsset = new SiteAsset("small", "assets", "photo-small.jpg", "images/photo-small.jpg",
    new ImageMetadata(320, 160, "image/jpeg", ImageMetadataProvenance.Declared));
var largeAsset = new SiteAsset("large", "assets", "photo-large.jpg", "images/photo-large.jpg",
    new ImageMetadata(640, 320, "image/jpeg", ImageMetadataProvenance.Declared));
var smallSource = new ImageSource(smallAsset);
var largeSource = new ImageSource(largeAsset);
var registered = new ImageDescriptor([smallSource, largeSource], fallback: largeSource);
var existingOptions = new SiteGenerationOptions { Assets = [smallAsset, largeAsset] };
// ResponsiveImage.Render(context.Assets, registered, "Lake at dawn")
```

寸法が宣言されていても、未登録のアセットへの参照は失敗します。対応するデコード処理で `Decoded` の情報が確定し、変換では出力された正確なバイト列に `Generated` のメタデータを付けます。キャッシュ形式2は、メタデータを出力 ID、コンテンツハッシュ、変換の識別情報に関連付けます。キャッシュのメタデータが古い、不足している、不正である、一致しない場合は再生成します。キャッシュは出力先の外に置いてください。フィンガープリントには、PNG とピクセルのプロファイル、圧縮設定、実装、ランタイムの識別情報を含めます。検証していない環境間でのバイト一致は保証しません。

入力 PNG は8ビットでインターレースのない、グレースケール、RGB、グレースケール＋アルファ、RGBA に限ります。フィルター0〜4、複数の IDAT チャンク、透過、制限付きの構造／チェックサム検証に対応します。出力はフィルター0と zlib 圧縮を使用する、インターレースのない RGBA8 PNG です。リサイズは指定幅を正確に守り、縦横比を維持し、高さの端数は四捨五入します。バイリニア補間と source-over 合成は乗算済みアルファを使用します。完全に透明な出力の RGB は0です。`ImageLimits` は、割り当て前に入力、チャンク、寸法、ピクセル数、デコード後のバイト数を制限します。

パレット画像、16ビット PNG、Adam7、APNG、JPEG／WebP／AVIF の変換、SVG のラスタライズ、EXIF の向き、ICC／cICP／HDR の色変換、汎用の文字／ベクター描画には対応しません。入力は sRGB またはタグなしである必要があり、互換性のある gAMA／cHRM の値を検査します。任意のメタデータは検証して読み飛ばし、変換後のメタデータ保持は保証しません。未対応の形式は `SiteAsset` でそのまま公開し、既存のバリアントを Core の descriptor で宣言してください。

ソーシャル画像を明示的に合成するには、`SocialImageAsset(background, logo, id, "social.png", logoWidth, logoX, logoY)` を作成します。背景とロゴの両方の PNG ソースアセットと、その `Transform` を登録します。その `Image` を `SiteGenerationOptions.CommonSocialImage` または `SocialImages` に指定してください。`SocialImages` のキーはページの正規化済み相対出力パスです。背景がキャンバスの寸法を決め、ロゴの配置はキャンバスの範囲で切り取ります。既存のサイト画像やページ画像には `ImageSource` を直接使用します。favicon があるだけではソーシャル画像を生成しません。タイトルなどの文字が必要な場合は、事前に生成した画像を用意してください。

移行時は、形式／品質を指定するバリアントを `ImageVariant(id, path, width, CompressionLevel)` に置き換え、`ImageAsset` でフォールバックを明示的に選び、その `Descriptor` を Core の `ResponsiveImage.Render` に渡します。以前の形式／品質のオーバーロードは `LSI001`／`LSI002` を報告します。PNG に非可逆の品質設定はありません。未対応の PNG 機能は `LSI003`、不正な PNG 入力は `LSI005`、フォールバックの選択漏れは `LSI004` を報告します。`ExternalAvifEncoder` は削除されています。AVIF ファイルは事前に生成し、既存のアセットとして参照してください。

ソースルート、出力パス、変換の登録、キャッシュの制限は[アセットと画像](https://github.com/Htkym/lithosharp/blob/main/docs/assets-and-images.ja.md)を参照してください。
