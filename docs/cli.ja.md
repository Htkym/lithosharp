# CLI とサイトファクトリ

`LithoSharp.Tool` は .NET 10 のツールです。サイトのビルドには .NET 10 SDK、
開発サーバーには ASP.NET Core の共有フレームワークを使います。
ローカルで作ったパッケージは、公開せずに次のように試せます。

```powershell
dotnet tool install LithoSharp.Tool --tool-path .tools --add-source artifacts/packages
dotnet new install artifacts/packages/LithoSharp.ProjectTemplates.1.0.0.nupkg
.tools/lithosharp new docs -n MyDocs -o MyDocs
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

テンプレートは別パッケージです。`dotnet new lithosharp-docs`、
`dotnet new lithosharp-blog`、`dotnet new lithosharp-empty`、`dotnet new lithosharp-mdx` でも作成できます。
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
| `new docs\|blog\|empty\|mdx` | インストール済みのテンプレートから作成する。 |
| `build [project]` | コンパイル後に差分生成する。`--clean` は出力全体を置き換える。 |
| `serve [project]` | ビルドし、ファイル変更を監視してループバックで配信する。ポートは `--port` で指定する。 |
| `check [project]` | 一時出力で品質を検証し、設定済みの公開先を変更しない。 |
| `clean [project]` | 所有情報を検証し、生成後に変更されていない成果物を削除する。 |
| `inspect [project]` | サイトを更新し、グラフ、出力パス、所有者、キャッシュのヒットとミスの理由を表示する。 |
| `restore-mdx <worker>` | MDX worker の固定依存を明示的に復元する。 |
| `migrate docusaurus <source>` | Docusaurus サイトを解析する。JavaScript 設定は実行しない。`--output <directory>` で別出力先へ変換し、入力は上書きしない。`--expected-routes <file>` で route を照合し、`--base-url` と `--default-locale` で前提を定める。[移行手順](docusaurus-migration.ja.md) を参照する。 |

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

## 構造化出力と 1.x の互換性

`build`、`check`、`inspect`、`migrate`、開発サーバーは機械可読な結果を出す。
成功と失敗の JSON は `schemaVersion`（現在は `"1.0"`）を持つ安定した形式であり、
未知 field は無視するため、加算的な拡張は動作を保つ。終了コードは成功が `0`、
失敗が `1`、使い方の誤りが `2`、移行に未対応の内容を含む場合が `3` である。
手動対応の所見は report に残り、それだけでは終了コード `0` を変更しない。
開発サーバーは起動、再ビルド、停止を1行1件の JSON で出す。診断 code、
inspection 形状、移行 report は 1.x の範囲で加算的に保ち、それ以外は 2.0 へ送る。

## 困ったときは

- ポート競合: `serve` は構造化された起動失敗を出し、別のポートを推測しない。空いた `--port` を指定する。
- Node 不足: Markdown のみのサイトは Node なしで build できる。MDX は `LSMDX002` で設定した実行ファイル名を示す。Node.js 24.13.0 を入れて worker 依存を復元する。
- 未復元の依存: .NET は `dotnet restore --locked-mode`、MDX は `restore-mdx`（または worker での `npm ci --ignore-scripts --no-audit --no-fund`）で復元してから build する。
- worker の版ずれ: 固定外の Node、MDX、React、esbuild は bundle せずに拒否する。
- 厳格な front matter と route 衝突は位置付き診断で落とす。出力を直さず内容を直す。

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
