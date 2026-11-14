---
title: アセットと PNG 画像
supported_version: "3.0"
prerequisites: [quickstart]
---

Core は、ファイルのデコードやネイティブ画像ライブラリを必要とせず、画像参照を登録します。
`ImageSource`、`ImageMetadata`、レスポンシブ画像の記述子には、URL、寸法、バリアントを明示します。
既存の JPEG、WebP、AVIF ファイルをアセットとして登録できますが、Core は不足する画像形式を生成しません。

このサイトは `LithoSharp.Images` を明示的に追加します。
`website-social` 変換は `RgbaImage.Create`、`FillRectangle`、`PngCodec.Encode` を使い、
幅と高さを示す生成済みメタデータとともに、宣言した PNG 出力を書き出します。
結果は `CommonSocialImage` に登録します。生成したページの `og:image` メタデータと画像を確認してください。
変換には、設定と画素処理の識別情報があります。
宣言していないソースファイルの読み込みや、外部の描画処理の呼び出しは行いません。

初期の PNG コーデックは、明示した上限の範囲で、8 ビットかつ非インターレースのグレースケール、RGB、
グレースケールとアルファ、RGBA に対応します。
パレット形式、インターレース、ほかのビット深度の入力は、事前の変換が必要です。
`ImageAsset` は対応する PNG のサイズを変更し、`SocialImageAsset` は明示的に登録した PNG 入力を合成します。
フォント、JPEG・WebP・AVIF のエンコーダー、任意のネイティブコーデックは提供しません。

フォールバック用のバリアント、パスの範囲制限、アセットの依存関係、キャッシュの無効化は、
[アセットガイド](../../reference/ja/assets-and-images/)を参照してください。
キャッシュした画像記述子に互換性がない場合や、情報が不足する場合は、再生成が必要です。
