# LithoSharp プロジェクトテンプレート

[English](https://github.com/Htkym/lithosharp/blob/main/templates/LithoSharp.ProjectTemplates/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/templates/LithoSharp.ProjectTemplates/README.ja.md)

C# の型とビルドで管理するドキュメントを作成できます。3.0.0 の `docs` テンプレートはカスタムスキーマ、生成バインダーとカタログ、型付き参照を、Docs のナビゲーションと明示的な出力品質検査につなぎます。

対応するテンプレートパッケージを `dotnet new install LithoSharp.ProjectTemplates::3.0.0` でインストールし、`dotnet new lithosharp-docs`、`dotnet new lithosharp-blog`、`dotnet new lithosharp-empty`、`dotnet new lithosharp-mdx` のいずれかでサイトを作成してください。

```sh
dotnet new lithosharp-docs -n MyDocs -o MyDocs
dotnet tool install LithoSharp.Tool --version 3.0.0 --tool-path .tools
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

CLI パッケージはテンプレートとは別です。`docs` はドキュメントサイト、`blog` は Blog テンプレートを使うサイトを作成し、`empty` はカスタム C# レイアウトの例を示します。`mdx` はオプションの MDX ワーカー設定を追加します。生成されたプロジェクトは `ISiteFactory` を公開し、CLI がこれをコンパイルして、信頼するサイトコードとして実行します。

テンプレートは .NET 10 を対象とし、既定で LithoSharp 3.0.0 を使用します。MDX には追加で Node.js 24.13.0 と、ワーカーの明示的な復元が必要です。ビルド、配信、対話型コンポーネントの例は[クイックスタート](https://github.com/Htkym/lithosharp/blob/main/docs/quickstart.md)を参照してください。

コンパイラー診断と生成出力の検査を含む、型付きコンテンツの作成から検証までの例は、[Docs サンプル](https://github.com/Htkym/lithosharp/blob/main/samples/LithoSharp.DocsSample/README.md)を参照してください。

Docs プロジェクトには `GuideFrontMatter.cs`、`Guides.Binder`、生成ルート、2 つの有効な Markdown ページが含まれます。`GuideFrontMatter.cs` では title が必須で、description と order は省略できます。`AdditionalFiles` と実行時の読み込みは `Guides.Catalog` を通じて、`content` ルート、拡張子を含む入れ子の相対 ID、アンダースコアで始まるファイルやディレクトリの除外を共有します。同じ宣言からカタログを再ビルドするまでは、ローダーが入力の追加、不足、移動を報告します。`RenderDocs` は共通の Docs ページ構成を提供し、`index.html` はテンプレートだけが所有します。CLI と生成された Program はどちらも外部 HTTP を無効にしており、品質検査で Error があると失敗します。既存プロジェクトにはファイルを再生成して上書きせず、[移行ガイド](https://github.com/Htkym/lithosharp/blob/main/docs/migration-3.0.md)に従って明示的に導入してください。
