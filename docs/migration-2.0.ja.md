# 2.0への更新と1.xからの移行

[English](migration-2.0.md)

この案内は2.0.0と1.xからの変更を説明します。READMEと[Quick Start](quickstart.ja.md)の導入例は1.1.0向けです。CoreとVS Code拡張は別々に公開されるため、Coreの公開によって拡張0.2.0がMarketplaceへ公開されるわけではありません。

## バージョンの指定

正式版では、利用するLithoSharpパッケージをすべて2.0.0にそろえます。

候補feedと固定バージョンが提供されている場合は、そのバージョンのCore、MDX、Tool、Testing、Generatorsなどをそろえて利用します。追加するAnalyzersとCodeFixesも同じ候補バージョンを使います。`2.0.0-ci.…`や`2.0.0-rc.…`などの候補バージョンと、projectに記載した基準バージョン`2.0.0`を区別してください。

templateの既定参照は`2.0.0`です。候補feedで生成したprojectを使う場合は、既存の`LithoSharpVersion`プロパティに実際の候補版を指定してrestoreします。Toolのインストールにも同じ固定版と候補feedのNuGet設定を使ってください。公開済み1.1.0のToolに、以下のコマンドは含まれません。

Markdownのruntime依存には独立した[Syntamark](https://github.com/Htkym/syntamark)を使い、Generators／Analyzersは対応するsource由来のコードを組み込みます。候補feedには、その候補が指定したSyntamark runtimeも必要です。利用者のアプリへSyntamark.Sourceを直接追加する必要はありません。runtimeとsourceの固定pairを別々の版に入れ替えたり、LithoSharpの候補版へ付け替えたりしないでください。現在の固定pairはNuGetで公開済みのSyntamark 1.0.0です。Markdownの対応構文とMDXの必要環境は[既知の制約](known-limitations.ja.md)を参照してください。

## Testingの非互換変更

`SiteTestDocument.Document`と第三者DOMへの依存を、`Snapshot`と読み取り専用queryへ置き換えます。1.xのDOM getterを使ってコンパイルしたバイナリも更新が必要です。

```csharp
using LithoSharp.Testing;

using var document = SiteTestDocument.Parse("<h1>Home</h1>");
var heading = document.Snapshot.Query("h1")?.TextContent;
document.AssertText("h1", "Home");
```

`QuerySelector`は`Snapshot.Query`、`QuerySelectorAll`は`Snapshot.QueryAll`へ移します。DOMの変更、第三者型へのcast、独自の拡張メソッドは単純な置換では移行できません。元のHTMLや描画処理を変更し、必要に応じて解析し直します。対応selector、例外、所有期間は[Testing v2の仕様と移行表](Testing-v2-api.md#migration)を確認してください。ブラウザーの完全なDOM／CSS互換は保証しません。

Toolの`migrate testing --dry-run`は、明示したC# sourceと旧1.1.0 DLLのmetadata参照から利用箇所を報告します。旧DLLを実行せず、projectやsourceを変更せず、利用者の依存packageも削除しません。`ReadCandidate`のpreviewも確認が必要であり、`Manual`と`Unknown`は自動変換しません。[実行例と報告形式](Testing-v2-api.md#migration)を参照してください。

## HTML・資産・診断

HTMLのsnapshot、品質検査、対応する静的URL診断には、対応範囲と予算を持つ共通の解析コードを使います。未対応のHTMLや不完全な解析を、完全な検査結果として扱いません。[サイト品質の設定](site-quality.md)では、最終HTMLの検査を`SiteGenerationOptions.Quality`で有効にできます。

資産の登録・変換・描画では同じ宣言オブジェクトを使います。`Assets`、`AssetTransforms`、`PublicDirectory`の利用例とパスの制約は[資産と画像](assets-and-images.ja.md)を参照してください。生成の`MaxDegreeOfParallelism`は1以上で、既定値は1です。登録済みの生成呼び出しに対する確定した不正値は、Analyzerの`LSA1005`でも検査します。

`LithoSharp.Analyzers`は任意で導入するcompiler packageです。登録済みAPIの確定した誤用、対応するHTML補間contextとliteral URL、検証済みcatalogへの参照を検査します。不存在の確定にはfreshなClosed scopeが必要で、Open／Stale／Unknownや未対応の値を推測しません。診断がないことは、site全体のリンク・anchor・資産が検証済みであることを意味しません。coverage方針の`LSA9001`は明示opt-inです。

生成projectの`LithoSharpVersion`を`2.0.0`に設定し、診断を利用するprojectの`ItemGroup`へ次を追加します。候補feedを使う場合は、実際の候補版を指定してください。

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
