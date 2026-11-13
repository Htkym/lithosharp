# LithoSharp.CodeFixes

[English](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.CodeFixes/README.md) | [日本語](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.CodeFixes/README.ja.md)

C# スキーマと生成された参照を使って .NET のドキュメントを開発するチーム向けに、LithoSharp のコンパイラー診断を補う任意の IDE 修正機能を提供します。修正を提示するのは、文書に示した条件を満たすと証明できたパターンだけです。コンパイルと最終出力の検証は、修正とは別に扱います。

Roslyn 4.14 の IDE ホストで、対応する LithoSharp.Analyzers と Generators パッケージと一緒にインストールしてください。Workspaces への依存はこの IDE 用アセンブリが持ちます。コンパイラーアナライザーは Workspaces に依存せず、Core ランタイムを読み込みません。

3 パッケージともバージョン 3.0.0 を指定し、利用するプロジェクトだけで使う private な依存関係にしてください。CodeFixes の導入は任意です。インストールしても、エディターの Markdown 解析で C# スキーマを検査できるようになるわけではなく、すべての Analyzer 診断に修正が用意されるわけでもありません。

```xml
<PackageReference Include="LithoSharp.CodeFixes" Version="3.0.0" PrivateAssets="all" />
```

LSA1401 は検証済みで最新の入力に一致する Closed マニフェストを使う呼び出しで、直接記述されたリテラル引数だけを、一意の生成 PageRef の `Route.PublicPath` に置き換えます。元の `Manifest.GetUrl` のレシーバー、オーバーロード、実行時のガードは維持します。生成された Pages の初期化子はすべて検証済みの不変カタログへの正規形で副作用のない参照でなければなりません。Open、古い、曖昧、手書き、副作用のある参照や、証明できない参照には修正を提示しません。

修正アクションは現在のコンパイラー入力を再検証し、古いワークスペースのスナップショットを拒否します。標準の CodeAction のプレビューと元に戻す操作を利用できます。文書、プロジェクト、ソリューション単位の FixAll は、それぞれの対象を元のプロジェクトのコンパイル結果で証明し、独立したリテラルの置換を一括して不可分に適用します。証明できない対象がある場合や、途中でソリューションが編集された場合は、アクション全体を適用しません。プロジェクトとソリューション単位のアクションは、リンクファイルと、編集可能な元のソース文書がない診断も拒否します。未対応のコンパイル参照など、最新のマニフェストを証明するための既存の制限は引き続き適用されます。存在しないルート、生の HTML、見出し、任意の URL 式を推測で修復することはありません。

[Analyzer ガイド](https://github.com/Htkym/lithosharp/blob/main/src/LithoSharp.Analyzers/README.md)、[型付きコンテンツのジェネレーターガイド](https://github.com/Htkym/lithosharp/blob/main/docs/source-generators.md)、[アップグレード時の注意点](https://github.com/Htkym/lithosharp/blob/main/docs/migration-3.0.md)を参照してください。
