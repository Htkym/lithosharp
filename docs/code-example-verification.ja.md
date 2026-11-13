# 文書のコード例を検証する

フェンス付きブロックの固定した `snippet` ID を、明示的に信頼したテストプロジェクトに対応付けます。
検証 API はそのプロジェクトの既存テストを実行します。
Markdown フェンス内のコードのコンパイルや実行、OKF の計算の評価は行いません。
通常のサイト生成やエディターでの検査でも、テストを開始しません。

````markdown
```csharp snippet="addition" source="examples/Arithmetic.cs" region="Addition"
```
````

テストプロジェクトが実際にコンパイルするコードを表示するには、既存の `source` と `region` の取り込み規則を使います。
`source` のないフェンスにもスニペット ID を宣言できます。
この対応付けは、どの既存テストが検査するかを示し、コピーしたフェンスの本文が自動的にコンパイルされるとは主張しません。
ID は各文書内で一意にしてください。

```csharp
using LithoSharp.Content;
using LithoSharp.Documentation;

var snippets = new[]
{
    new DocumentSnippet("addition", "guide.md", "examples/Examples.csproj",
        testFilter: "/*/*/ArithmeticTests/AdditionWorks")
};
var inputs = new[]
{
    ContentDependency.FromFile("examples/Arithmetic.cs"),
    ContentDependency.FromFile("examples/ArithmeticTests.cs"),
    ContentDependency.FromFile("examples/packages.lock.json"),
    ContentDependency.FromFile("global.json"),
    ContentDependency.FromValue("assertion-contract", "addition/1")
};
var results = await DocumentationVerification.VerifySnippetsAsync(
    trustedRoot, snippets, inputs, "Release", cancellationToken);
```

関連するソース、参照先プロジェクト、パッケージロック、SDK 設定、ビルドプロパティのファイル、テスト設定を、
選択したプロジェクトのディレクトリ外の依存関係も含めて、すべて `inputs` に宣言します。
パスは `trustedRoot` からの相対パスです。そのルート内の通常ファイルだけを受け付けます。
検査のたびにファイルのバイト列を読み直します。値の依存関係は、表すルールが変わった場合に値も変えてください。
この明示的な入力一覧は、呼び出し側が満たす条件です。MSBuild のグラフから推測するものではありません。

ソース文書、選択したプロジェクト、取り込むソース、実効的な `dotnet --version`、構成、
選択したテストフィルター、パーサーの識別情報もフィンガープリントに含めます。
テストには `dotnet test --project ... --no-restore --configuration ...` を使います。
任意のフィルターには TUnit の `--treenode-filter` 構文を使います。検証前に依存関係を復元してください。
API は暗黙の時間上限を持たず、キャンセルに応じます。

各結果は元の文書の範囲を示します。取り込んだコードには、既知の元ソースの領域も関連位置として付けます。
コマンドが失敗した場合は、診断に使えるよう、上限付きの出力をメッセージに保持します。
プロジェクトとフィルターが同じテストは一度だけ実行し、明示的に対応付けた各スニペットに、その結果を付けます。
この対応付けは、フェンス内の各文をテストが網羅することを証明しません。

`Success` と `Failure` は明示的なテスト実行の結果です。
`NotRun` は、実行しない検査、または実行中の入力変更によって無効になった結果を示します。
`Skipped` には明示的な理由が必要です。`OutOfScope` は、利用できないか曖昧なフェンスや入力を示します。
実行日時を結果に含めるのは、コマンドを実行した場合だけです。

プロジェクトを実行せずに現在の入力を確認するには、任意の過去の結果とともに `InspectSnippetsAsync` を呼びます。
コードや設定が変わると、過去の根拠は無効になります。
検査対象にできる、スキップしていないスニペットでも、実行しない検査の結果は常に `NotRun` です。
過去の成功を、新しくテストを実行した結果には変えません。
どの結果も OKF の `verified` メタデータや公開済みの出力を更新しません。
