# CLI とサイトファクトリ

`LithoSharp.Tool` は .NET 10 のツールです。サイトのビルドには .NET 10 SDK、
開発サーバーには ASP.NET Core の共有フレームワークを使います。
ローカルで作ったパッケージは、公開せずに次のように試せます。

```powershell
dotnet tool install LithoSharp.Tool --tool-path .tools --add-source artifacts/packages
dotnet new install artifacts/packages/LithoSharp.ProjectTemplates.0.2.0.nupkg
.tools/lithosharp new docs -n MyDocs -o MyDocs
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

テンプレートは別パッケージです。`dotnet new lithosharp-docs`、
`dotnet new lithosharp-blog`、`dotnet new lithosharp-empty` でも作成できます。
空サイトのテンプレートには、独自の C# レイアウトを含めています。

## CLI とライブラリで同じ定義を使う

サイトには、公開された引数なしコンストラクターを持つ `ISiteFactory` の具象クラスを
1つ用意します。CLI はプロジェクトをコンパイルし、ビルドごとに別プロセスで
ファクトリを呼び出します。終了時に DLL のロックが解放され、次回は C# の変更も
反映されます。サイトのコードは、実行した利用者の権限で動作します。

```csharp
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;

public sealed class SiteFactory : ISiteFactory
{
    public async Task<SiteDefinition> CreateAsync(
        SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var posts = await new MarkdownPostReader().ReadAllAsync(
            Path.Combine(context.ProjectDirectory, "content"));
        return new SiteDefinition(new SiteSettings
        {
            Title = "My docs", BaseUrl = "https://example.com/", TimeZone = "UTC"
        }, posts) { OutputDirectory = "dist" };
    }
}
```

通常のコンソールアプリからも、同じファクトリを呼び出せます。

```csharp
var root = Environment.CurrentDirectory;
var definition = await new SiteFactory().CreateAsync(new SiteFactoryContext(root));
var result = await new SiteGenerator().GenerateWithOptionsAsync(
    definition.Site, definition.Posts, Path.GetFullPath(definition.OutputDirectory, root),
    false, definition.Customization, definition.Options, CancellationToken.None);
```

入力のパスは `context.ProjectDirectory` を基準にします。`AppContext.BaseDirectory` は
ホストの実行ファイルの場所です。定義の相対出力先はサイトプロジェクトを基準にし、
CLI の `-o` はシェルの作業ディレクトリを基準にします。
`Customization` と `Options` は通常のライブラリ API と同じ型です。
出力のバイト列を比較する場合は、[差分ビルド](incremental-builds.ja.md)の説明に従って
ビルド日時も固定してください。

## コマンド

| コマンド | 動作 |
| --- | --- |
| `new docs\|blog\|empty` | インストール済みのテンプレートから作成する。 |
| `build [project]` | コンパイル後に差分生成する。`--clean` は出力全体を置き換える。 |
| `serve [project]` | ビルドし、ファイル変更を監視してループバックで配信する。ポートは `--port` で指定する。 |
| `check [project]` | 一時出力で品質を検証し、設定済みの公開先を変更しない。 |
| `clean [project]` | 所有情報を検証し、生成後に変更されていない成果物を削除する。 |
| `inspect [project]` | サイトを更新し、グラフ、出力パス、所有者、キャッシュのヒットとミスの理由を表示する。 |

プロジェクトファイル、またはプロジェクトが1つだけあるディレクトリを指定します。
`-c Release` で構成を変更できます。`check --format text|json|sarif` はライブラリと
同じ診断形式を使います。`inspect --format json` はグラフとキャッシュの情報を返します。
コンパイル失敗、プロジェクトの曖昧さ、ファクトリの読み込み失敗、サイトの検証失敗は
コマンドの失敗として報告します。

`serve` は変更をまとめて処理し、ビルド出力とキャッシュを監視対象から除外します。
本文の変更は差分生成し、C# とプロジェクトの変更では再コンパイルします。
リロード用のスクリプトは HTTP 応答に加えるため、生成ファイルは変わりません。
`/_lithosharp/diagnostics` とブラウザー上の表示で診断を確認できます。
再ビルドに失敗しても、直前に確定した出力を配信します。
プロジェクト外の入力が変わった場合は、再起動が必要です。

`SiteGenerator.CleanAsync(outputDirectory, cancellationToken)` は削除した相対パスを
返します。利用者が編集したファイルと未所有のファイルは残します。
所有情報が必要で、出力マニフェストだけでは削除できません。
ファイルシステムのルートや安全でない再解析ポイントは拒否します。
確定前にキャンセルした場合は、既存の出力を維持します。
`build --clean` は従来どおりの全体置換で、この選択的な `clean` とは動作が異なります。
キャッシュは削除権限の根拠にはせず、`clean` でも削除しません。

## 配布方式の制限

対応する配布方式は、共有フレームワークを使う .NET ツールです。
単一ファイル形式はツールのパッケージ化とは別に評価します。
任意のサイトアセンブリは外部から読み込むため、ツールの単一ファイル化だけでは
サイトまで同梱できません。

trimming と Native AOT は、この動的ホストでは非対応です。
実行時に発見するファクトリと任意のサイト依存関係は、ツールの公開時には解析できません。
Native AOT では任意のマネージドアセンブリの動的読み込みを使えません。

公式資料: [カスタムテンプレート](https://learn.microsoft.com/dotnet/core/tools/custom-templates)、
[単一ファイル配布](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)、
[trimming の制限](https://learn.microsoft.com/dotnet/core/deploying/trimming/incompatibilities)、
[Native AOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot)。
