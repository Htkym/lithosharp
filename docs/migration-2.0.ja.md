# 2.2.0への更新

[English](migration-2.0.md)

このガイドはLithoSharp 2.2.0への更新と、2.1.3のDocs readerから型付きDocsへの任意の移行を説明します。VS Code拡張のバージョンは0.4.0です。
導入方法は[Quick Start](quickstart.ja.md)を参照してください。

0.xのサイトでは[更新履歴の資料](history/README.md)で過去の挙動と依存関係の変更も確認してください。履歴には当時のバージョンに向けた手順を残しています。今回の更新先は2.2.0にそろえます。初回は新しいディレクトリへ生成し、出力を比較してから配置してください。

## バージョンの指定

利用するパッケージ、Tool、Templatesは2.2.0にそろえてください。生成プロジェクトの既定値も2.2.0です。既存プロジェクトを更新する場合は、`LithoSharpVersion`を2.2.0に変更し、ToolとTemplatesも更新します。旧1.1.0のToolに`preflight`と`migrate testing`は含まれません。

Markdownのruntimeは[Syntamark](https://github.com/Htkym/syntamark) 1.2.0を使い、GeneratorsとAnalyzersは対応するSourceを組み込みます。アプリケーションからSyntamark.Sourceを直接参照する必要はありません。既定のprofileは`lithosharp-markdown/1`です。CommonMarkの選択やMDX本文解析の置き換えではありません。対応する構文と必要な環境は[既知の制約](known-limitations.ja.md)を参照してください。

Generatorとsplitterのheader走査には、本文解析とは別の`frontmatter-scan/1`を使います。`ScanFrontMatter`は閉じ区切りの後の本文を解析せず、1 Mi UTF-16コード単位のheader上限にも本文を含めません。`Closed`が確定するのは区切りとheaderのUnicodeであり、YAMLとスキーマの検査は引き続きLithoSharpが行います。入力や解析資源の問題による`Unknown`は、Generatorで`LSG002`、runtimeで`LSM013`となり、headerの不存在や確定した未閉鎖として扱いません。headerの結果には本文の解析情報や文書ハッシュは含まれません。manifestのfingerprintは別途元の入力全体を対象とし、本文のUnicodeが不正な場合は取得できません。

postとcollectionのMarkdown本文は、最終解析時に1 Mi UTF-16コード単位まで扱えます。エディターでの検査は8 Miまでです。readerとcollection loaderが加工前の本文を調べる上限も8 Miです。1 Miを超える元の本文でも、includeや前後の空白除去によって最終解析の前に短くなる場合があります。

入力や解析資源の上限により解析を完了できない場合は、LSM013 Errorとなり、出力を公開する前に生成を停止します。加工前の警告は元の本文の位置を示します。加工後のcollection本文の警告は、元の本文との一致を確認できる場合だけ元の位置を示し、それ以外はファイル名までです。collectionの無変更ビルドでも、検証済みキャッシュから警告を復元します。

## 既存Docsを型付きコンテンツへ移す

2.2.0では新しく作成する`docs`プロジェクトを変更しました。
既存サイトは自動で変わりません。`MarkdownPostReader`のスキーマとルートが用途に合えば
維持できます。作業中のプロジェクトへテンプレートを上書きしないでください。
テンプレートのファイルを参考に、ブランチ上で必要な変更を選び、最初は別の出力先へ生成します。

| 項目 | 従来のreader | 新しい型付きDocsテンプレート |
| --- | --- | --- |
| 入力の契約 | `MarkdownPost`とreaderの規則。title・dateの必須条件と任意のsummary検証。 | 独自の`GuideFrontMatter`。titleは必須、description・orderは任意で、日付を必須にしません。 |
| コンパイル入力 | 自動では生成済みの型付きcollectionになりません。 | `content/**/*.md`を`AdditionalFiles`へ明示し、除外とcollection・id・routeのmetadataを指定します。 |
| 実行時入力 | 既存readerとcontent directory。 | `content`をrootとする`MarkdownContentCollectionLoader<GuideFrontMatter>`で生成Binderを使います。 |
| entry ID | 既存post・readerの規則。 | content rootからの相対pathに拡張子を含め、区切りは`/`にします。 |
| ルート | サイトの現在の出力規則を維持できます。 | 新規の既定値は`guide/<拡張子を除いた相対path>/`です。loaderは`Guides.Catalog.GetRoute(entry.Id)`から取得します。 |
| 表示と検証 | 既存テンプレートと選択済みの検査。 | `RenderDocs`と`DocsSiteTemplate`を使い、QualityのError閾値と外部HTTP検査の無効を明示します。 |

既存URLを維持する場合は、コンパイル入力のmetadataへ現在のルートを宣言し、実行時にも
生成Catalogから同じルートを取得してください。新しい`guide/`のprefixやdirectory-index形式を
採用する必要はありません。別のルート辞書を重ねて作らないでください。テンプレートのホームと
コンテンツページの出力先を分けます。既存コンテンツが`index.html`を生成する場合は、標準の
Docsホームを使う前に生成元を明示的に整理してください。URLを変える必要がある場合だけ
redirectを検討します。

コンパイルと実行時のroot、再帰glob、`_`で始まる入力の除外、IDをそろえてください。
`reference/setup.md`を`setup.md`と同じIDにしないようにします。ファイル名を変えると、宣言を
明示的に維持しない限り、生成IDと参照名も変わります。Core・Generators・Analyzersを
`LithoSharpVersion`でそろえ、コンパイル用パッケージには`PrivateAssets="all"`を指定します。
エディターで独自スキーマを実行せず、生成Binderとselectorを使ってください。
`RequireStaticCoverage`は対応範囲で選ぶ任意の規則であり、テンプレート全体の必須条件ではありません。

変更前後のルート、ナビゲーション、必須metadata、HTMLの見出しとリンク、資産、独自CSSを
比較してください。配布前に、新しい出力へ[DocsのCI手順](quickstart.ja.md#このdocsプロジェクトのci)を
適用します。変更を選ぶ場合を除き、既存のQuality閾値、抑制、外部リンク方針は維持してください。
新しいfactoryの明示設定はCore全体の既定値を変えるものではありません。MarkdownにNodeは
不要ですが、MDXを加える場合は明示的なworker設定が必要です。
adapterは[Docsの設定](docs-template.ja.md)、出力を比較する範囲は[互換性契約](compatibility-contract.ja.md)を参照してください。

## Docsの設定と生成済み成果物

Docsの追加設定は必要な項目だけを有効にします。標準テンプレートの検索、トピック、
トップページの設定と、追加の注意書きやリンクを指定する`SiteCustomization.Footer`は、
[Docsの標準設定](docs-template.ja.md)を参照してください。HTMLの後処理を置き換える場合も、
サイトに必要な注意書きとリンクは残します。AngleSharpの依存は、ほかの利用コードが
残っていないことを確認してから削除してください。

配布する成果物を再生成せずに調べるには、`check-output`を使います。毎回現在のファイルを
読みますが、現在の原稿との一致は保証しません。原稿や設定を変更した場合は再生成が必要です。
`check`との違い、出力形式、検査範囲は[生成済み成果物の検査](generated-output-checks.ja.md)を参照してください。

## Testingの非互換変更

`SiteTestDocument.Document`と第三者DOMへの依存を、`Snapshot`と読み取り専用queryへ置き換えます。1.xのDOM getterを使ってコンパイルしたバイナリも更新が必要です。

```csharp
using LithoSharp.Testing;

using var document = SiteTestDocument.Parse("<h1>Home</h1>");
var heading = document.Snapshot.Query("h1")?.TextContent;
document.AssertText("h1", "Home");
```

`QuerySelector`は`Snapshot.Query`、`QuerySelectorAll`は`Snapshot.QueryAll`へ移します。DOMの変更、第三者型へのcast、独自の拡張メソッドは単純な置換では移行できません。元のHTMLや描画処理を変更し、必要に応じて解析し直します。対応selector、例外、所有期間は[Testing v2の仕様と移行表](Testing-v2-api.md#migration)を確認してください。ブラウザーの完全なDOM／CSS互換は保証しません。

Toolの`migrate testing --dry-run`は明示したC# sourceと旧1.1.0 DLLのmetadata参照から利用箇所を報告します。旧DLLを実行せず、projectやsourceを変更せず、利用者の依存packageも削除しません。`ReadCandidate`のpreviewも確認が必要であり、`Manual`と`Unknown`は自動変換しません。[実行例と報告形式](Testing-v2-api.md#migration)を参照してください。

## HTML・資産・診断

HTMLのsnapshot、品質検査、対応する静的URL診断には、対応範囲と予算を持つ共通の解析コードを使います。未対応のHTMLや不完全な解析を、完全な検査結果として扱いません。[サイト品質の設定](site-quality.md)では最終HTMLの検査を`SiteGenerationOptions.Quality`で有効にできます。

資産の登録・変換・描画では同じ宣言オブジェクトを使います。`Assets`、`AssetTransforms`、`PublicDirectory`の利用例とパスの制約は[資産と画像](assets-and-images.ja.md)を参照してください。生成の`MaxDegreeOfParallelism`は1以上で、既定値は1です。登録済みの生成呼び出しに対する確定した不正値は、Analyzerの`LSA1005`でも検査します。

`LithoSharp.Analyzers`は任意で導入するcompiler packageです。登録済みAPIの確定した誤用、対応するHTML補間contextとliteral URL、検証済みcatalogへの参照を検査します。不存在の確定にはfreshなClosed scopeが必要で、Open／Stale／Unknownや未対応の値を推測しません。診断がないことは、site全体のリンク・anchor・資産が検証済みであることを意味しません。coverage方針の`LSA9001`は明示opt-inです。

プロジェクトの`LithoSharpVersion`を`2.2.0`に設定します。診断を利用するprojectの`ItemGroup`へ次を追加してください。

```xml
<PackageReference Include="LithoSharp.Analyzers" Version="$(LithoSharpVersion)" PrivateAssets="all" />
```

`LithoSharp.CodeFixes`は対応するIDE host用の任意packageです。`LSA1401`で検証された直接文字列リテラルを対象とし、文書化した条件でfixを提供します。すべての診断に自動修正があるわけではありません。severityとsuppression、各ruleの範囲は[Analyzerの案内](../src/LithoSharp.Analyzers/README.md)、fixの条件は[CodeFixesの案内](../src/LithoSharp.CodeFixes/README.md)を参照してください。

## 描画前のCLI検査

同じ固定版のToolをインストールした後、明示したMarkdown入力だけを調べるには次を使います。

```sh
.tools/lithosharp preflight --mode static --input page.md --format json
```

static modeはMSBuild、site factory、plugin、bundle、SSRを実行しません。MDX入力を含める場合は、既存の明示worker restoreと対応するNode／worker設定が必要です。

信頼するprojectをコンパイルし、factory/catalogも検査する場合は次を使います。

```sh
.tools/lithosharp preflight site.csproj --mode trusted --input page.md --format json
```

trusted modeはprojectとfactory/catalogのコードを実行しますが、描画や出力公開は行いません。coverageはPartialであり、最終HTML、動的な処理、site全体の存在証明を保証しません。成功は報告した段階にErrorがないという意味で、後のbuildを許可する証明ではありません。通常buildのruntime検証と最終品質検査は引き続き必要です。詳細な報告形式は[Toolの案内](../src/LithoSharp.Tool/README.md#preflight)を参照してください。
