# アセットと画像

[English](assets-and-images.md)

既存の `SiteAsset` 宣言は内容の識別情報を含む URL を維持します。
`AssetUrl.Fingerprint` は SHA-256 の小文字の16進表現、`Integrity` は `sha256-` と Base64 のダイジェストを組み合わせた値です。
`integrity` 属性では引用符で囲んで使います。
`SiteGenerationOptions.PublicDirectory` を設定すると、通常のファイルを元の相対パスで再帰的にコピーします。
ディレクトリの欠落、シンボリックリンク、不正なパス、ルートの衝突は、出力の確定前に失敗します。
公開ファイルの入力先と出力先は重ねられません。アセットのソースファイルも出力の外に置きます。
アセットと外部リンクのキャッシュは、公開ファイルのディレクトリの外に置いてください。

## 宣言した変換と CSS 参照

```csharp
using System.Text;
using LithoSharp;

var logo = new SiteAsset("logo", "assets", "logo.png", "images/logo.png");
var stylesheet = new SiteAssetOutput("brand-css", "styles/brand.css");
var transform = new SiteAssetTransform("brand-css", "brand-css/v1", [logo], [stylesheet],
    (context, token) => context.WriteAsync(stylesheet,
        Encoding.UTF8.GetBytes($".brand {{ background-image: url('{context.GetUrl(logo).Value}'); }}"), token));
var options = new SiteGenerationOptions
{
    Assets = [logo],
    AssetTransforms = [transform],
    AssetCacheDirectory = ".lithosharp/assets"
};
// In a rendering context:
// context.Assets.GetUrl(stylesheet).ToAttributeValue()
```

コンテキストは `OpenRead(input)` で読み取り専用スナップショット、`GetUrl(input)` で宣言した入力の URL を提供します。
`WriteAsync` では、宣言した出力を一度だけ書き込みます。
ハンドラーと描画処理が使うものと、同じ宣言オブジェクトを登録してください。
ID やパスの重複、宣言していない読み書き、出力の欠落は失敗します。
出力の `AssetUrl.Asset` は互換性のために合成した宣言です。その入力パスはソースファイルではありません。
変換した出力を指定するには `SiteAssetOutput` を使います。
`AssetRegistry.BuildNodes` は、コピーと変換の依存関係、識別情報、所有する成果物を公開します。

変換ハンドラーは決定的に動作する必要があります。
宣言していないファイル、ネットワーク、時刻、変更されるプロセス状態を読まないでください。
コンテキストは自身の読み書き API を制限しますが、任意のデリゲートのコードを隔離することはできません。
すべての設定、実装、ツールへの依存を `implementationFingerprint` に含めます。
任意の `validateInputs` コールバックはキャッシュヒット時も含め、キャッシュを調べる前に毎回実行します。
外部の依存関係が変わった場合に拒否するためにも使えます。
例は `validateInputs: token => { token.ThrowIfCancellationRequested(); ValidateTool(); return Task.CompletedTask; }` です。

明示的に有効にする `AssetCacheDirectory` は出力ディレクトリの外に置く必要があります。
キーには入力内容、入力 URL、出力宣言、実装・設定・ツールの識別情報を含めます。
キャッシュのバイト列はハッシュで確認し、欠落、破損、不一致があれば再生成します。
キャッシュを出力の所有権の根拠には使いません。ヒットした場合はハンドラーを省略します。
ステージング中のアセットとバイト列が一致すれば、最終的なアセットの書き込みを省略します。
ただし、ロールバックできるよう、トランザクションの準備では既存の出力を隔離したステージングへコピーします。

## 既存のレスポンシブ画像

Core はJPEG、WebP、AVIF、SVG などの既存画像ファイルをデコードせずにコピーできます。
レスポンシブ表示には `ImageDescriptor` と、バリアントから明示的に選んだフォールバックを使います。
Images パッケージや変換は不要です。

```csharp
using LithoSharp;

var small = new SiteAsset("photo-small", "assets", "photo-small.jpg", "images/photo-small.jpg",
    new ImageMetadata(320, 160, "image/jpeg", ImageMetadataProvenance.Declared));
var large = new SiteAsset("photo-large", "assets", "photo-large.jpg", "images/photo-large.jpg",
    new ImageMetadata(640, 320, "image/jpeg", ImageMetadataProvenance.Declared));
var smallSource = new ImageSource(small);
var largeSource = new ImageSource(large);
var picture = new ImageDescriptor([smallSource, largeSource], fallback: largeSource);
var imageOptions = new SiteGenerationOptions { Assets = [small, large] };
// In a layout: ResponsiveImage.Render(context.Assets, picture, "Lake at dawn")
```

`ImageSource` は、外部 HTTP(S) URL と登録済みの変換出力も受け付けます。
呼び出し元が渡す寸法の由来は `Declared` です。
対応するデコードと生成は、それぞれ `Decoded` と `Generated` の情報を確定します。
ファイルをコピーしただけではデコードできることを証明しません。
単一画像の寸法が不明なら、その寸法は出力しません。
複数のレスポンシブ画像では、既知の寸法とメディア型が必要で、メディア型と幅の組み合わせを重複させません。

`ResponsiveImage.Render` は代替テキストと属性をエスケープし、`picture`、形式別の source、`srcset`、`sizes`、
既知の固有の幅と高さ、`decoding="async"`、`loading="lazy"` を出力します。
遅延読み込みにしない画像には `lazy: false` を指定します。
描画時には寸法の取得のためにコーデックを開きません。
記述子が使うものと同じ宣言オブジェクトを登録してください。
メタデータを渡しても、未登録の別オブジェクトが有効になるわけではありません。

## 任意の PNG 処理

PNG 処理が必要な場合は、同じバージョンの Core と `LithoSharp.Images` を使います。
正の幅と任意の BCL `CompressionLevel` で PNG バリアントを宣言し、フォールバックを明示的に選びます。

```csharp
using LithoSharp.Images;

var fallback = new ImageVariant("photo-png", "images/photo.png", 640);
var image = new ImageAsset(
    new SiteAsset("photo", "assets", "photo.png", "images/original.png"),
    [fallback], fallback);
var transformed = new SiteGenerationOptions
{
    Assets = [image.Source],
    AssetTransforms = [image.Transform],
    AssetCacheDirectory = ".lithosharp/assets"
};
// ResponsiveImage.Render(context.Assets, image.Descriptor, "Lake at dawn")
```

限定した PNG デコーダーは非インターレースの8ビットのグレースケール、RGB、アルファ付きグレースケール、RGBA を受け付けます。
エンコードでは RGBA8 の PNG を作ります。
サイズ変更は乗算済みアルファを使う双線形補間で縦横比を保持します。
高さは四捨五入し、最小1画素にします。
`ImageLimits` は、入力、チャンク、寸法、画素数、デコード時の割り当てを制限します。
不正な入力、未対応の変換、リソース上限、キャンセルは、異なる失敗として扱います。

パレット形式・16ビット・Adam7 の PNG、アニメーション、EXIF の向き、sRGB 以外の色管理、
JPEG・WebP・AVIF の変換、SVG のラスタライズ、文字描画には対応しません。
既存アセットとして扱うか、事前に生成したバリアントを渡してください。
正確な PNG プロファイル、画素操作、明示的な背景とロゴの合成 API は、
[Images パッケージのガイド](../src/LithoSharp.Images/README.ja.md)にあります。

変換メタデータはバージョン付きのキャッシュで、出力の識別情報とバイト列のハッシュに結び付けます。
メタデータの欠落、不正、不一致があれば再生成します。
キャッシュヒットでも、同じ寸法、メディア型、由来を保持します。

## ソーシャル画像と移行

共通の既存画像には `SiteGenerationOptions.CommonSocialImage` を、ページごとの上書きには `SocialImages` を設定します。
上書きのキーはページ出力の相対パスです。
登録した PNG 合成結果にも同じ `ImageSource` の契約を使えます。
favicon からソーシャル画像を暗黙に生成することはありません。

従来の形式・品質のオーバーロードは、PNG の `CompressionLevel` の設定か、事前に生成したファイルに移行します。
`ExternalAvifEncoder` は削除されました。
`SiteThemeOptions.DefaultSocialSubtitle` と暗黙のソーシャル画像生成のフラグを、明示的な画像に置き換えてください。
文字を含む画像は事前に用意する必要があります。

[Docs サンプル](../samples/LithoSharp.DocsSample/README.ja.md)の `--asset-demo --check` で、公開ファイルと PNG 変換を確認できます。

```sh
dotnet run --project samples/LithoSharp.DocsSample -- --asset-demo --check --output .local/assets-site
```

このコマンドはリポジトリのルートから実行してください。
生成時の[品質検査](site-quality.ja.md)は登録したアセットへの参照を調べます。
[`check-output`](generated-output-checks.ja.md) は既存の配布物内のローカル参照先を調べますが、
登録済みアセットの所有者や未使用のアセットまでは確定できません。
