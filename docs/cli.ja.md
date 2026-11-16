# CLIとサイトファクトリ

`LithoSharp.Tool`は.NET 10のツールです。サイトのビルドには10.0.300以降の.NET 10 SDKが必要で、
配信時にはASP.NET Coreの共有フレームワークも使います。
次のコマンドは3.0.0のパッケージ契約を対象にし、そのリリースがNuGetで入手可能になった後に適用します。
公開状態は[バージョン一覧](../eng/version-matrix.json)で確認してください。

```powershell
dotnet tool install LithoSharp.Tool --version 3.0.0 --tool-path .tools
dotnet new install LithoSharp.ProjectTemplates::3.0.0
.tools/lithosharp new docs MyDocs -o MyDocs
.tools/lithosharp build MyDocs
.tools/lithosharp serve MyDocs
```

テンプレートはツールとは別のパッケージです。
短い名前は`lithosharp-docs`、`lithosharp-blog`、`lithosharp-empty`、`lithosharp-mdx`で、`dotnet new`でも直接使えます。
空サイトのテンプレートは、独自のC#レイアウトを示します。

## 検査と移行

Toolと、選択したCore・MDXのパッケージは3.0.0にそろえてください。
`preflight`と`migrate testing --dry-run`には、その機能を通知するToolが必要です。
[3.0への移行案内](migration-3.0.ja.md)はネイティブYAML、任意の画像処理、取得したスキーマ、検証の範囲を説明します。
以前のTestingの移行は[2.xの履歴資料](migration-2.0.ja.md)に残しています。

## CLIとライブラリで同じ定義を使う

サイトアセンブリは、publicで非抽象・非ジェネリックの`ISiteFactory`クラスを1つだけ公開します。
publicな引数なしコンストラクターが必要です。ツールはプロジェクトをコンパイルし、新しい子プロセスでそのファクトリを読み込みます。
これによりC#コードを読み直し、各ビルドの終了時にDLLのロックを解放します。
サイトのコードは実行した利用者の権限で動作します。

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

ソースパスには`context.ProjectDirectory`を使ってください。`AppContext.BaseDirectory`はホストプロセス側のパスです。
相対指定の`SiteDefinition.OutputDirectory`はサイトプロジェクトを基準にし、CLIの`-o`はシェルの作業ディレクトリを基準にします。
`Customization`と`Options`は通常のライブラリオブジェクトで、コレクション、レイアウト、アセット、品質の閾値、
タイムスタンプ、キャッシュポリシーを含みます。生成したバイト列を比較する場合は、再現可能なタイムスタンプを設定してください。
詳しくは[増分ビルド](incremental-builds.ja.md)を参照してください。

## コマンド

| コマンド | 動作 |
| --- | --- |
| `new docs\|blog\|empty\|mdx` | インストール済みのプロジェクトテンプレートを呼び出します。 |
| `capabilities --format json` | プロジェクトを評価せずに、このToolのCore識別情報、契約、機能を報告します。 |
| `markdown-compat <file> --advisory on\|off` | 編集時の検査と同じ構文解析で、明示したMarkdownファイルを検査します。任意の`--project <snapshot.json>`は、プロジェクトをコンパイルせずに文脈のデータを渡します。 |
| `preflight --mode static --input <file>` | MSBuildやファクトリを実行せずに、明示したソース入力を検査します。MDXには復元済みの解析用workerが必要です。 |
| `preflight <project> --mode trusted` | 描画や公開をせずに、信頼して実行するfactory/catalogコードをコンパイル・実行します。任意の`--input`で明示したソースを検査します。網羅性は`Partial`のままです。 |
| `build [project]` | コンパイルして増分生成します。`--clean`は全体を置き換えるビルドを要求します。 |
| `serve [project]` | ビルドし、変更を監視してループバックで配信します。ポートは`--port`で選びます。 |
| `check [project]` | 一時出力へ生成して品質を検証し、設定済みの出力先へは公開しません。 |
| `check-output --output <directory> --base-url <url>` | コンパイルや再生成をせずに、既存の出力を読み取ります。[出力の検査](generated-output-checks.ja.md)を参照してください。 |
| `clean [project]` | ライブラリの所有権検証を使って、未変更の所有成果物を削除します。`clean`がキャッシュを削除することはありません。 |
| `cache info\|clean -o <directory>` | 1つの出力の増分キャッシュパーティションを明示的に報告・削除します。独自のキャッシュルートには`--cache-dir`を使います。ビルドの一部として実行されることはなく、公開出力や別の出力のパーティションを削除することもありません。 |
| `inspect [project]` | サイトを更新し、ビルドグラフ、出力パス、所有者、実際のキャッシュヒットとミスの理由を表示します。 |
| `restore-mdx <worker>` | MDX workerのlockfileに従って、npm依存関係を明示的に復元します。 |
| `snapshot <source> <destination> <version>` | 別の出力先に、バージョン付きの文書スナップショットを作成します。 |
| `extract-translations <source>` | 対応するソースファイルの翻訳キーを、ファイルを変更せずにJSONで報告します。 |
| `migrate testing --dry-run --source <file.cs> --reference <legacy.dll>` | 明示したソースとPEメタデータから、旧Testing APIの読み取りを報告します。ファイルを編集したり、渡されたアセンブリを実行したりすることはありません。[移行の対応表](Testing-v2-api.ja.md#移行)を参照してください。 |
| `migrate docusaurus <source>` | JavaScript設定を実行せずにDocusaurusサイトを解析します。`--output <directory>`を指定すると別のディレクトリへ変換し、入力は上書きしません。`--expected-routes <file>`はルートを正確に比較します。分類済みのoracleと`--compare-normalized-pages`で、正規化した文書ページの別の比較を明示的に有効にします。`--base-url`と`--default-locale`は移行の前提を設定します。[Docusaurusの移行案内](docusaurus-migration.ja.md)を参照してください。 |

プロジェクトファイル、またはプロジェクトが1つだけあるディレクトリを指定してください。
`-c Release`でビルド構成を変更します。`check --format text|json|sarif`はライブラリの診断フォーマッターを使い、
`inspect --format json`は構造化したグラフとキャッシュの情報を返します。
コンパイルエラーとプロジェクトの曖昧さはコマンドを失敗させます。
ファクトリの読み込みとサイト検証の失敗は、不完全な出力を公開する代わりに報告します。

静的検査、信頼して実行するpreflight、生成、出力検査は、それぞれ検証する範囲が異なります。
ソースだけの結果は、プロジェクトに結び付いたルートやスキーマを証明しません。
信頼して実行するpreflightは最終HTMLを描画せず、`check`は一時サイトを再生成し、
`check-output`は既存のバイト列を読み取るためソースの最新性を証明しません。
独自のC#スキーマの検証はコンパイルやサイトのビルドが担い、言語サーバーの組み込みスキーマのスナップショットには含まれません。
詳しくは[preflightの契約全体](../src/LithoSharp.Tool/README.ja.md#preflight)を参照してください。

`serve`はプロジェクトファイルを監視し、連続する変更をまとめ、ビルド出力とキャッシュを監視対象から除外します。
コンテンツの変更は増分生成し、C#とプロジェクトの変更では再コンパイルも行います。
ブラウザーのリロードスクリプトはHTTP応答に挿入し、ディスク上のファイルは変更しません。
`/_lithosharp/diagnostics`は現在のビルド状態を公開し、ブラウザーのオーバーレイはビルドの失敗を示します。
再ビルドが失敗しても直前にコミットした出力は利用できます。監視対象のプロジェクトディレクトリの外での変更には再起動が必要です。

JSONモードの`POST /_lithosharp/watch-barrier`は、すでに受け付けた監視入力とそのビルドの完了を最大60秒待ちます。
固定した世代、所有者、ホストの識別情報を返しますが、その後のネイティブなイベントが発生しないことを保証するものではありません。
再ビルド完了時の`watch`の記録には、処理した再起動理由、入力リビジョン、前後のホストを含めます。
復旧イベントは通常のホストの再利用と区別します。連携では構造化した項目を使い、ログや再起動メッセージがないことから再利用の成功を推測しないでください。

`SiteGenerator.CleanAsync(outputDirectory, cancellationToken)`は削除した相対パスを返します。
変更されたファイルと所有していないファイルは残します。所有権のメタデータが必要で、出力マニフェストだけでは削除を許可できません。
ファイルシステムのルートと安全でないリパースパスは拒否します。コミット前のキャンセルでは以前の出力を保持します。
`build --clean`は従来の全体置換の動作を維持し、この選択的な`clean`コマンドとは異なります。
キャッシュは削除の権限にはならず、`clean`でも削除しません。

<a id="構造化出力と-1x-の互換性"></a>

## 構造化出力と1.xの互換性

`build`、`check`、`inspect`、`migrate`、開発サーバーは機械可読な結果を返します。
成功と失敗のJSONは`schemaVersion`（現在は`"1.0"`）を持つ安定した形式です。
未知のフィールドを無視するため、加算的な拡張後も動作を保ちます。
終了コードは成功が`0`、失敗が`1`、使い方の誤りが`2`、Docusaurus移行に未対応の内容が含まれる場合が`3`、キャンセルが`130`です。
通常のプロジェクトのコンパイル失敗も`2`を返します。
信頼して実行するpreflightはコンパイル失敗をその段階のレポートに取り込み、`1`を返します。
0以外の結果をすべて同じ種類の失敗とせず、コマンドのレポートを確認してください。
手動対応の所見はレポートに残り、それ自体で終了コード`0`が変わることはありません。
開発サーバーは起動、再ビルド、停止を1行1件のJSONオブジェクトで出力します。
診断コード、検査情報の構造、移行レポートは、文書に記載した加算的な1.xの契約を維持します。
2.xの挙動とTesting APIの移行は[過去の移行案内](migration-2.0.ja.md)を参照してください。
パッケージのメジャーバージョンと構造化出力のスキーマバージョンは同じものではありません。

## 困ったときは

- ポート競合: `serve`は構造化した起動失敗を報告し、別のポートを推測せずに終了します。空いている`--port`を選んでください。
- Node.jsがない: MarkdownだけのサイトはNodeなしでビルドできます。MDXのビルドは`LSMDX002`で設定した実行ファイル名を示して失敗します。Node.js 24.13.0をインストールし、workerの依存関係を復元してください。
- 依存関係が未復元: ビルド前に、.NETは`dotnet restore --locked-mode`、MDXは`restore-mdx`かworkerディレクトリでの`npm ci --ignore-scripts --no-audit --no-fund`を実行してください。
- workerのツールチェーンのずれ: ビルドは固定した組み合わせ以外のNode、MDX、React、esbuildを使ってバンドルせずに拒否します。
- front matterの厳格な検証で問題が見つかった場合や、ルートが衝突した場合は、位置付きの診断でビルドを失敗させます。出力ではなくコンテンツを修正してください。

## 配布方式の制限

対応する配布方式は、共有フレームワークを使う.NETツールです。
単一ファイル形式の公開は、ツールのパッケージ化とは別に評価します。
ホストは外部のサイトアセンブリを読み込みます。ツールのバンドルに、将来の任意のサイトを同梱することはできません。

trimmingとNative AOTは、この動的なサイトホストではサポートしません。
実行時のファクトリ検出とサイトの任意の依存関係は、ツールの公開時には解析できません。
Native AOTは任意のマネージドアセンブリの読み込みに対応しません。
これらは明示的な制限であり、未検証のフラグによる互換性を約束するものではありません。

公式資料: [カスタムテンプレート](https://learn.microsoft.com/dotnet/core/tools/custom-templates)、
[単一ファイル配布](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)、
[trimmingの制限](https://learn.microsoft.com/dotnet/core/deploying/trimming/incompatibilities)、
[Native AOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot)。

独自の連携には[機能情報のスキーマ](tooling-capabilities.ja.md)と[言語サーバーのプロトコル](language-server.ja.md)を参照してください。
