# CLI とサイトファクトリ

`LithoSharp.Tool` は .NET 10 のツールです。サイトのビルドには10.0.300以降の.NET 10 SDK、
開発サーバーには ASP.NET Core の共有フレームワークを使います。
以下の導入例は2.1.3を対象にしています。候補feedを使う場合は、提供された実際の固定候補版を指定してください。

```powershell
dotnet tool install LithoSharp.Tool --version 2.1.3 --tool-path .tools
dotnet new install LithoSharp.ProjectTemplates::2.1.3
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

テンプレートは別パッケージです。`dotnet new lithosharp-docs`、
`dotnet new lithosharp-blog`、`dotnet new lithosharp-empty`、`dotnet new lithosharp-mdx` でも作成できます。
空サイトのテンプレートには独自の C# レイアウトを含めています。

## 検査と移行

`preflight`と`migrate testing --dry-run`には対応するToolを使ってください。旧1.1.0のToolには含まれません。[2.1.3への移行案内](migration-2.0.ja.md)に、固定候補版の依存関係、静的検査と信頼するproject検査の違い、Testingの更新手順をまとめています。

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

通常のコンソールアプリからも同じファクトリを呼び出せます。

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
| `check-output --output <directory> --base-url <url>` | コンパイルや再生成をせずに生成済みの出力を検査する。[検査対象と制約](generated-output-checks.ja.md)を参照する。 |
| `clean [project]` | 所有情報を検証し、生成後に変更されていない成果物を削除する。 |
| `inspect [project]` | サイトを更新し、グラフ、出力パス、所有者、キャッシュのヒットとミスの理由を表示する。 |
| `capabilities --format json` | 選択したToolの機能情報を取得する。プロジェクトは評価しない。 |
| `preflight --mode static --input <file>` | 明示したソースだけを解析する。MSBuild、factory、描画、公開は実行しない。 |
| `preflight <project> --mode trusted` | 明示的にコンパイルし、信頼するfactory/catalogを検査する。`--input`は任意で、明示した入力を検査する。描画や公開は実行しない。 |
| `migrate testing --dry-run` | 明示したC#ソースと旧DLLのmetadata参照からTesting APIの移行箇所を報告する。ソース変更や旧DLLの実行はしない。 |
| `cache info\|clean -o <directory>` | 出力ごとのlockを取得し、キャッシュの使用量を確認するか削除する。 |
| `markdown-compat <file> --advisory on\|off` | 明示したMarkdownを編集時と同じ構文解析で検査する。`--project <snapshot.json>`はコンパイルせずに文脈のデータを渡す。 |
| `snapshot <source> <destination> <version>` | 文書のバージョン別snapshotを別の出力先に作る。 |
| `extract-translations <source>` | 対応ソースの翻訳キーをJSONで報告し、ソースを変更しない。 |
| `restore-mdx <worker>` | MDX worker の固定依存を明示的に復元する。 |
| `migrate docusaurus <source>` | Docusaurus サイトを解析する。JavaScript 設定は実行しない。`--output <directory>` で別出力先へ変換し、入力は上書きしない。`--expected-routes <file>` はrouteをexact比較する。分類oracleと`--compare-normalized-pages`で文書page set比較を追加できる。`--base-url` と`--default-locale`で前提を定める。[移行手順](docusaurus-migration.ja.md)を参照する。 |

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

JSON modeの`POST /_lithosharp/watch-barrier`は既に受け付けたwatch入力とそのビルドの完了を最大60秒待ちます。固定した世代、owner、hostの識別情報を返しますが、その後のファイルイベントがないことは保証しません。再ビルド完了の`watch`項目には、処理した再起動理由、入力revision、前後のhostが含まれます。復旧イベントは通常のhost再利用と区別されます。連携ではログの文言や再起動通知の有無から推測せず、構造化した項目を使ってください。

`SiteGenerator.CleanAsync(outputDirectory, cancellationToken)` は削除した相対パスを
返します。利用者が編集したファイルと未所有のファイルは残します。
所有情報が必要で、出力マニフェストだけでは削除できません。
ファイルシステムのルートや安全でない再解析ポイントは拒否します。
確定前にキャンセルした場合は、既存の出力を維持します。
`build --clean` は従来どおりの全体置換で、この選択的な `clean` とは動作が異なります。
キャッシュは削除権限の根拠にはせず、`clean` でも削除しません。

## 描画前の検査

```powershell
.tools/lithosharp preflight --mode static --input page.md --format json
.tools/lithosharp preflight site.csproj --mode trusted --input page.md --format sarif
```

static modeでは明示したUTF-8入力だけを検査します。MDXは復元済みの解析用workerを使い、必要なら`--worker-directory`と`--node-executable`を指定します。preflight自身は依存関係をインストールせず、Markdownへの代替処理もしません。

trusted modeはプロジェクトとfactory/catalogのコードを実行します。ソースにErrorがあればコンパイル前に停止し、コンパイル後は指定したソースを読み直してcatalogを検査します。未知の拡張prepare hookは実行せず`Deferred`として残します。いずれのmodeもcoverageは`Partial`です。成功は報告した段階でErrorがないという意味で、最終HTML、動的な処理、サイト全体、後続のビルドを検証した証明ではありません。

JSONとSARIFには段階、入力の観測、診断が含まれます。`sourceCoverage`の`Inspected`だけで妥当性を判断せず、`success`、診断、終了コードも確認してください。入力のバイト列ハッシュはその時点の観測であり、その後の変更を保証しません。[Toolの詳細](../src/LithoSharp.Tool/README.md#preflight)と[機能情報](tooling-capabilities.ja.md)を参照してください。

<a id="構造化出力と-1x-の互換性"></a>

## 構造化出力と互換性

`build`、`check`、`inspect`、`migrate`、開発サーバーは機械可読な結果を出します。
成功と失敗の JSON は `schemaVersion`（現在は `"1.0"`）を持つ安定した形式であり、
未知 field は無視するため、加算的な拡張は動作を保ちます。終了コードは成功が `0`、
失敗が `1`、使い方の誤りが `2`、移行に未対応の内容を含む場合が `3` です。
手動対応の所見は report に残り、それだけでは終了コード `0` を変更しません。
開発サーバーは起動、再ビルド、停止を1行1件の JSON で出します。診断 code、
inspectionの形状、移行reportは各schemaの互換性規則に従います。未知のフィールドは無視し、互換性のないmajor schemaは拒否します。キャンセル時の終了コードは`130`です。

## 困ったときは

- ポート競合: `serve` は構造化された起動失敗を出し、別のポートを推測しない。空いた `--port` を指定する。
- Node 不足: Markdown のみのサイトは Node なしで build できる。MDX は `LSMDX002` で設定した実行ファイル名を示す。Node.js 24.13.0 を入れて worker 依存を復元する。
- 未復元の依存: .NET は `dotnet restore --locked-mode`、MDX は `restore-mdx`（または worker での `npm ci --ignore-scripts --no-audit --no-fund`）で復元してから build する。
- worker のバージョンのずれ: 固定外の Node、MDX、React、esbuild は bundle せずに拒否する。
- 厳格な front matter と route 衝突は位置付き診断で落とす。出力を直さず内容を直す。

## 配布方式の制限

対応する配布方式は共有フレームワークを使う .NET ツールです。
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

独自の連携には[機能情報のschema](tooling-capabilities.ja.md)と[言語サーバーのプロトコル](language-server.md)を参照してください。
