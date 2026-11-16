# 文書の意味制約

通常の文書には個々の CLR 型を超える制約を宣言できます。
同じルール評価器が、取得済みのビルドメタデータと、エディターのメタデータを受け付けます。
エディターでコンポーネント、プロジェクト、独自の検証処理を実行しません。
OKF バンドルには独立した適合性と公開のポリシーを維持します。

独自のコンテンツモデルでは条件付きの入力要件を宣言できます。

```csharp
public sealed class GuideFrontMatter
{
    public string? Status { get; set; }

    [ContentRequiredWhen("status", "stable")]
    public string? SupportedVersion { get; set; }
}
```

条件には YAML の名前を使い、デコードしたスカラーの文字列を ordinal で比較します。
複数の属性は選択条件として扱い、いずれかの条件に一致すると、そのフィールドが必須になります。
依存するフィールドは、入力に存在し、null でも空でもない必要があります。
空白だけの文字列、空のシーケンスやマッピングは失敗します。
C# の初期化子は欠落した入力を補いません。
YAML の通常の null はリテラルの `"null"` には一致しません。引用符で囲んだ `null` の文字列は一致します。
生成するコンパイル時検査、生成バインダー、リフレクションバインダー、生成するエディター用スキーマは、同じ規則を共有します。
公開の判断と独自の検証には、引き続きビルドが必要です。

文書拡張では標準の制約を明示的に有効にできます。

```csharp
docs.AddCollection(new DocumentationCollection("guide", variants)
{
    SemanticRules = DocumentSemanticRuleSet.Documentation
});
```

有効にすると、公開する文書には `supported_version` が必要になり、`prerequisites` は同じコレクション、バージョン、言語の文書 ID を含みます。

```yaml
title: Deployment
supported_version: '3.0'
prerequisites: [installation, configuration]
```

文書を公開するかどうかは、実際のビルドの公開ポリシーが決めます。
公開する文書は取得済みで未公開と分かる前提文書を参照できません。
一覧に載せない文書も公開済みの参照先です。
独自のルールではバージョンや言語をまたぐ参照を明示的に許可できます。
対象の識別情報を明示するには、`DocumentKey`、または `collection`、`version`、`locale`、`id` を持つマッピングを使います。
ルートは常に既存の `DocumentCatalog` から取得します。

`DocumentSemanticRuleSet.Evaluate` は、各ルールに `Passed`、`Failed`、`Deferred`、`NotApplicable` を返します。
カタログや公開判断が利用できない場合は `Deferred` であり、検証に成功した状態ではありません。
文書の欠落をエラーにするのは、スナップショットが参照対象の全範囲を明示的に網羅する場合だけです。
部分的なスナップショットでも、既知の参照先の存在は示せます。
`SemanticResults` と `SemanticSnapshot` は最新の文書の取得結果を公開します。
再取得に失敗すると、以前の成功した評価は無効になります。

診断には、宣言した条件付き要件の `LSC105`、実行時の必須フィールドの `LSDOC210`、
不正または欠落した参照の `LSDOC211`、公開文書から未公開文書への参照の `LSDOC212`、
バージョンや言語の不一致の `LSDOC213`、保留した検証を示す情報の `LSDOC214` を使います。
原因の位置は、解析した元の入力から取得し、既知の参照先には関連位置を付けます。
