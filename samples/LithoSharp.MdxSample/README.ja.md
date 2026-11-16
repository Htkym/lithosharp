# MDX と React のサンプル

[English](README.md)

このサンプルはリポジトリ内のプロジェクトを参照します。公開パッケージを使う場合は
[MDX Quick Start](../../docs/quickstart.ja.md)から始めてください。
.NET 10 SDK（10.0.300 以降の対応する feature band）と Node.js 24.13.0 を使います。

このリポジトリは LithoSharp 3.0.0 を対象にしています。選定した Syntamark 2 の組み合わせと、
候補が未公開であることは[バージョン一覧](../../eng/version-matrix.json)に記録しています。

リポジトリのルートで実行します。

```sh
npm ci --prefix src/LithoSharp.Mdx/worker --ignore-scripts --no-audit --no-fund
dotnet run --project samples/LithoSharp.MdxSample -c Release
dotnet run --project src/LithoSharp.Tool -- check-output --output samples/LithoSharp.MdxSample/.artifacts/site --base-url https://example.com/ --format json
```

出力は `samples/LithoSharp.MdxSample/.artifacts/site` です。HTTP で配信してください。
`file:` URL ではモジュールの読み込みとサービスワーカーを利用できません。

静的な MDX、ページ全体の hydration へのフォールバック、load・idle・visible・media・manual の
明示的な island、隔離した環境内のライブコードを示します。
ファクトリではローカル検索、プログレッシブナビゲーション、オフライン対応を有効にしています。
`--page-hydration` は比較用のモードを選択し、`--offline-disabled` はオフライン対応を終了するリビジョン、
`--next-revision` は更新用のフィクスチャーを生成します。

`check-output` は現在のローカル HTML/CSS と参照先・アンカーの関係を調べます。
ブラウザーの hydration、オフライン動作、現在の原稿との一致を検証するものではありません。
対話機能はブラウザーで確認してください。MDX とコンポーネントのコードはビルド実行者の権限で動きます。信頼できるコードだけをビルドしてください。

共通のフロントマター処理は選定した Syntamark 2 の runtime と Source の組み合わせを使います。
MDX 本文は引き続き worker が解析します。エディターの MDX シンボルは見出し単位であり、
ネイティブ Markdown の節全体を使うアウトラインとは別です。
独自の C# スキーマと生成した参照は[Docs サンプル](../LithoSharp.DocsSample/README.ja.md)を参照してください。

`SiteFactory.cs` はリポジトリのソースから worker を解決します。NuGet の利用側では
`new MdxOptions(context.ProjectDirectory)` でライブラリの隣にある同梱 worker を解決できます。そのディレクトリを明示的に復元してください。
[MDX](../../docs/mdx.ja.md)、[性能](../../docs/performance.ja.md)、[既知の制約](../../docs/known-limitations.ja.md)も参照してください。
