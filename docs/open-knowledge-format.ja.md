# Open Knowledge Format

LithoSharp は、固定した [OKF v0.2 仕様](https://github.com/GoogleCloudPlatform/open-knowledge-format/blob/ad30107c31c06aec8a7d5636e0d1058118604e6f/SPEC.md)に従って、ローカルの Open Knowledge Format バンドルを読みます。
バンドルには、Markdown の概念、任意の `index.md` と `log.md`、SQL、Python、画像などの補助ファイルがあります。
読み込みとエクスポートは、これらのファイルの実行、モデルの呼び出し、サービスへの接続を行いません。

各概念には、空でない文字列の `type` を持つフロントマターのマッピングが必要です。
未知の型や、入れ子の拡張メタデータは保持します。
任意フィールドやインデックスの欠落、未解決のリンクによって、概念が構造上不正になることはありません。

## 検査と検証

```sh
lithosharp okf inspect --input knowledge --format json --as-of 2026-11-13T00:00:00Z
lithosharp okf validate --input knowledge --strict-lint --format sarif
```

コマンドはバンドルを直接読み、C# プロジェクトのビルドやサイトファクトリの呼び出しは行いません。
`--as-of` は、オフセットを明示した日時を受け付けます。省略した場合は、操作ごとに現在時刻を一度だけ取得します。
`--max-files`、`--max-file-bytes`、`--max-bundle-bytes` は、取得する入力を制限します。
現在のファイル検出では取得ファイル数の上限を適用する前に、入力ツリーを走査します。

診断は、構造エラー、品質上の指摘、未対応の構文、リソース上限、I/O、キャンセル、公開ポリシーを分けます。
結果は、ファイルスナップショットの完全性、解析の完全性、適合性も区別します。
未対応の YAML 機能や未知の仕様バージョンの宣言は、適合性を検証したとは報告しません。
明示した `--strict-lint` では警告も失敗になり、構造上の失敗や実行の失敗は常に失敗です。
キャンセルの終了状態は 130、不正なコマンド引数は 2 です。

ルートの `index.md` は、`type` なしで `okf_version: "0.2"` を宣言できます。
ほかのインデックスファイルにはフロントマターを置きません。
ログの日付見出しには、有効な `YYYY-MM-DD` の日付を使います。ログのフロントマターは禁止していません。
`README.md`、`AGENTS.md`、アンダースコアで始まる Markdown ファイルも概念として扱います。

## メタデータを変更せずに読む

```csharp
using LithoSharp.OpenKnowledge;

var bundle = await OkfBundle.ReadAsync("knowledge", new OkfReadOptions
{
    AsOf = DateTimeOffset.Parse("2026-11-13T00:00:00Z"),
    BaseUrl = "https://example.com/docs/",
});

foreach (var document in bundle.Documents)
{
    Console.WriteLine($"{document.Path}: {document.Metadata?.Type}");
}
```

`OkfDocument.Path` と `ConceptId` は元のバンドル相対パスの表記を保持します。
概念 ID は末尾の `.md` を除き、公開ルートとは別に扱います。
大文字・小文字や NFC の衝突は、ファイル名を変えて解決せず、診断します。
`Catalog` は、共通のコンテンツカタログとルート検証を使います。

`RawText`、`File.GetOriginalBytes()`、`Yaml` は、それぞれ元の文字列、バイト列の防御的コピー、順序を持つネイティブ YAML ツリーを公開します。
既知のメタデータのプロパティはこのツリーに対する表示です。
数値、真偽値、null を文字列の `type` に変換しません。
`GetField` は、任意の入れ子の値、スカラーの種類とスタイルを保持します。
未対応の構文も取得済みのバイト列を保持し、表示を利用できない状態を明示します。

`Sources` は、パスや URL に加え、リソースの説明も受け付けます。
`UsageWindow`、`Generated`、`Runtime`、`Parameters`、`Computation`、`Executor`、`Attester` は、元のノードを保持します。
従来の `timestamp` は、`generated` がない場合だけ表示のフォールバックに使います。
従来の Citations 節は、`sources` がない場合に、そのままの節の文字列として利用できます。

`verified` のマッピングとシーケンスは、共通の `VerificationEvents` 表示を使います。

| 記録 | 導出する信頼性の段階 |
|---|---|
| `verified` キーなし | `Unverified` |
| 空のシーケンス | `NoRecords` |
| 不正な記録だけ | `Indeterminate` |
| `human:` 以外の主体による有効な記録 | `MachineConfirmed` |
| `human:` の主体による有効な記録あり | `HumanReviewed` |

有効なイベントと不正なイベントが混在する場合は、導出した段階を保持し、`VerificationIsComplete = false` を報告します。
生成者の名前は許可リストではありません。
段階は宣言された記録を要約するものであり、主体の本人確認や、内容の正しさを証明しません。
リーダーは、生成者、検証者、日時を追加しません。

`Freshness` は、期限なし、新しい状態、古い状態、不正な値を区別します。
`AsOf >= StaleAfter` で古い状態とし、オフセットをまたいで時点を比較します。
日付だけの値やオフセットのない値に、時刻やタイムゾーンを補いません。
`status` がない場合は `stable` として読みます。未知の状態値は、品質診断とともに利用できる状態を維持します。

## 既存の Docs パイプラインを使う

```csharp
using LithoSharp.Content;

var options = bundle.CreateSiteOptions(new ContentCollectionId("knowledge"));
```

`DocsSiteTemplate` とともに、通常の `SiteGenerator.GenerateWithOptionsAsync` にオプションを渡します。
コレクションは、既存の Docs ナビゲーション、検索、見出しのリンク、公開メタデータに参加します。
HTML 出力の接頭辞の既定値はコレクション ID なので、バンドルのルートインデックスは、
サイトのホームを置き換えずに `knowledge/index.html` になります。
別の接頭辞が必要な場合は、`outputPrefix` を明示します。
生の OKF のエクスポートはこの HTML 投影とは独立して元のパスを維持します。

リンクには、解決した参照リンクも含め、Markdown パーサーの情報を使います。
コードの範囲、フェンス付きコード、エスケープしたリンク風の文字列は、関係になりません。
バンドルルートのリンクは、サイトのオリジンではなくバンドルに対して解決します。
HTML の URL はサイトのベースパスを使います。
元の宛先は `Links` に残し、未解決の概念も関係として保持します。
未解決の概念は、`data-okf-target` を持つ、移動しない span として描画します。
通常のサイトコンテンツには既存のリンク検査を引き続き適用します。

脚注は、元のラベル、表示番号、繰り返し参照した箇所、戻るリンクを保持します。
`Citations` はラベルを `Sources[].Id` に対応付けるので、出典を並べ替えても別の出典への帰属に変わりません。
通常の説明用の脚注も描画します。

既定では、下書き、非推奨、古い概念、未検証の概念も閲覧できます。
公開方法を変えるには、`PageMetadata` のマッパーと、固定したポリシーのフィンガープリントを明示します。

```csharp
using LithoSharp.Pages;

var policyOptions = bundle.CreateSiteOptions(new ContentCollectionId("knowledge"),
    document => new PageMetadata(
        title: document.Metadata?.Title ?? document.ConceptId ?? document.Path,
        draft: document.Metadata?.Status == "draft"),
    policyFingerprint: "hide-declared-drafts/1");
```

この対応付けはサイトのポリシーであり、OKF の適合性ルールではありません。
選んだポリシーで除外した文書へのリンクは、生成する OKF ページでは移動しない表示になります。
バージョンと言語は、マッパーの通常の `PageMetadata.Document` プロパティで指定できます。
独自のマッパーにフィンガープリントがない場合は、コレクションのキャッシュを無効にします。

`CreateAuxiliaryAssets(outputPrefix)` は、取得済みの補助ファイルのバイト列を、準備済みのアセットとして公開します。
画像変換の実行や、実行目的で計算用ファイルを開き直す処理は行いません。
`CreateSiteOptions` は、これらのアセットを自動的に含めます。
入力集合、パーサーのポリシー、ルート、関係の集合、明示した時刻、取得済み入力のハッシュがビルド入力に参加します。
未解決の参照先は必須ファイルの依存関係にはなりません。

## 既存のバンドルを保持する

```sh
lithosharp okf export --input knowledge --output exported-knowledge --format json
```

```csharp
await bundle.ExportPreservedAsync("exported-knowledge");
```

保持処理は、取得した完全なファイル集合を、入力バンドルの外にある新しいディレクトリに書きます。
すべての文書と補助ファイルで、UTF-8 BOM、改行のスタイル、ソース ID、未知のメタデータも含め、元の相対パスとバイト列を維持します。
既存のインデックスや出力ディレクトリは置き換えません。
リンク、リパースポイント、安全でないパス、正規化後の衝突は拒否します。
完全にステージングしたディレクトリの移動によって公開します。
失敗やキャンセルの場合は、確認用に専用のステージングディレクトリが残ることがあります。

バイト列の保持と構文の検証は、別の結果です。
未対応の YAML を含むバンドルも、完全に取得できていれば保持できます。
CLI はエクスポートを報告するとともに、検証が成功していない状態を維持します。
ファイルの取得が不完全な場合は、保持バンドルとしてエクスポートできません。
この保証は、ローカルで取得したファイルを対象とし、外部サービスを通した往復処理は対象にしません。

## 新しい OKF を明示的に作る

```csharp
var prepared = OkfWriter.Prepare(
[
    new OkfNewConcept("metrics/revenue.md", "type: Metric\ntitle: Revenue",
        "# Revenue\n\nThe declared value is 42.\n"),
]);
await prepared.ExportAsync("new-knowledge");
```

新しい出力は指定したメタデータと Markdown を使います。
通常の記事の Date、Draft、Sources プロパティから、`generated.at`、`status`、OKF の出典を推測しません。
日時には明示的なオフセットが必要です。生成者、検証イベント、ビルド時刻は挿入しません。

コンポーネントの Markdown や MDX では、元の本文の種類を宣言し、移植可能な Markdown の代替を渡します。

```csharp
var concept = new OkfNewConcept("metric.md", "type: Metric", originalMdx,
    OkfSourceBodyKind.Mdx, portableMarkdown: "# Metric\n\nValue: 42.\n");
```

代替がない場合は、結果の `OriginalMarkdown` を保持したまま、変換に失敗します。
選んだ出力に構造化コンポーネントディレクティブがある場合は拒否します。
明示的に渡す代替は、生成側の宣言であり、すべての Markdown 実装で同じ動作になる保証ではありません。
`OkfNewAuxiliary` は、実行しないバイト列のスナップショットを追加します。
出力は新規作成専用で、変換に失敗した部分結果を、移植可能な OKF としてエクスポートすることはできません。
