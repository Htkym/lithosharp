# Markdown parserの抽出部品

MD-01では、現行Litho parserのblock/inline解析、node、UTF-16位置、directive名とparser上限を `Portable/` に集めます。同じsourceを使う独立projectの対象はnet10.0/netstandard2.0、言語版はC# 13です。I/O、HTML描画、site診断、Node、SharpDeps graphを参照しません。

内部nodeと位置型のnamespaceは既存の `LithoSharp.Content.Compilation` を維持します。製品はこのsourceをCompile linkで使用し、既存の型所属、assembly identity、描画経路を保ちます。独立assemblyの内部型を製品から参照する変更ではありません。SourceSpanのSiteSourceLocation変換と、LithoLimitsのsite診断は製品側のpartial定義に残します。

## portable境界

netstandard2.0にないIndex/Range、char検索、Stack.TryPopとrecord/initのcompiler markerは、同target限定の内部補助型で補います。block parserのGeneratedRegex 5箇所とslugの2箇所は、同じpatternのcached Regexを同targetで使い、net10では既存のsource-generated Regexを保ちます。Spanによる2箇所の文字列処理は、Ordinal比較とUTF-16文字のindex走査に置き換えます。ASCII文字判定の4箇所は、全targetで同じASCII範囲を使う補助関数へ接続します。parserの構文、位置の単位、render policy、既存compiler fingerprintを変更する抽出ではありません。

MD-02でYamlDotNetのexact 18.1.0依存、optional frontmatter、新しい公開parse facade・immutable facts・SourceSegmentsを加えました。入力はowned stringで、`MarkdownParser.Parse`は描画やsite解決を行いません。内部factsはPortable tree、公開DTOはPublic treeに分けています。既存runtime/inspection/site adapterはMD-03、compiler host/cache/世代はMD-04、固定版のruntime/source artifact pairはMD-05です。MD-01のsource linkを最終的なpackage供給の代替にはしません。

本projectは現在IsPackable=falseで、公開packageの版は変更しません。別repo移動も行いません。MD-06の移管はIN-01の両consumer確認後です。

## 小さいcanary

[LithoSharp.Markdown.Portability](../../tests/LithoSharp.Markdown.Portability/LithoSharp.Markdown.Portability.csproj)は、製品net10 assemblyと独立netstandard2.0 assemblyを使います。後者のTargetFrameworkAttributeを確認し、誤ってnet10同士を比較した場合は失敗します。

入力は既存C01のbody-basicsと、CRLF/entity/escape/emoji/reference/fence metadataを含む124 UTF-16 unitsの小さい例です。内部node、span、referenceをreflectionで読み、構造を比較します。公開APIをテストのために増やしません。既存の小さいh2/id/body描画と再実行一致、通常経路のMarkdig非ロードも確認します。

この比較の対象は、同じnet10 CLRで行う二つの解析経路です。別Roslyn版やGenerator/Analyzerでの実source compileを実証するものではなく、その確認はMD-04/IN-01に残します。MD-02では新strict entry、immutable facts、SourceSegmentsの小さい確認も同canaryへ追加しました。初回SHAのUnknown修正後の確認は成功しました。実diffレビューで求められたopaque判定・anchor探索・sentinelの修正後も、対象build/canaryは成功しました。結果を基準SHAと修正source hashに分けてMD-02のtask証拠へ記録します。

repo rootから、親が調整した検証枠で次を実行します。全solutionのbuild/testやbenchmarkは完了条件にしません。

~~~powershell
dotnet build src/LithoSharp.Markdown/LithoSharp.Markdown.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet restore tests/LithoSharp.Markdown.Portability/LithoSharp.Markdown.Portability.csproj --disable-parallel -nr:false
dotnet build tests/LithoSharp.Markdown.Portability/LithoSharp.Markdown.Portability.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet run --project tests/LithoSharp.Markdown.Portability/LithoSharp.Markdown.Portability.csproj --no-build --no-restore
~~~

MD-01のtask証拠には入力hash、command、実行結果、所要時間、未実行理由、所有プロセスの終了確認を残します。測定と競合する間は編集・差分確認を進め、次のローカルbuild/testを待ちます。MD-01はBocchiの実diffレビューに合格しました。現在のMD-02も、対象の確認とcommit/push後にBocchiのレビューを待ち、MD-03へは進みません。

## MD-01の確認結果

二TFM compileは3.27秒、製品接続canaryのrestoreは2.05秒、buildは2.29秒、runは0.91秒で成功しました。compileは警告・エラー0です。入力2件の構造・span・reference、fence metadata、既存描画の再実行一致、Markdig非ロードを確認し、所有build/test PID 0を確認しました。

[task証拠](../../docs/development/md01-verification.json)には実command、結果、入力/snapshot hash、初回失敗と修正、確認していない後続境界を記録します。実Roslyn host、固定版pack、別repo移管、公開は今回の確認に含めません。

## MD-02の新entry

[実装と確認記録](../../docs/development/md02-verification.ja.md)に、原文UTF-16のspanとmapping、optional YAML、headings/sections、links、fences、Partial/Failed、hash/optionsの境界を記録しています。folded/literal YAMLや未投影のtextはUnknownとして残し、LMD006とPartialを返します。旧entryのLSM診断やfrontmatter fallbackを新strict entryの規則へ置き換えることはMD-03の対象です。

新entryは取消を戻し、入力上限/invalid Unicodeをhash前に検査します。scan/depth/outputの途中上限はLMD003とPartialです。ParserVersionの[canonical source metadata](../../docs/development/md02-canonical-source.json)は開発中のsource hashで、固定版packageや実Roslyn hostの確認は後続に残ります。性能測定は行っていません。

## MD-02の確認結果

初回MD-02のSHA `41f80208cd7256421f84dcb927a7d34308704a91`の両TFM buildとcanary buildは警告・エラー0、runは成功しました。レビュー修正後の現revisionも両TFM buildとcanary build/runに成功し、buildは警告・エラー0です。BOM/CRLF、YAMLのUTF-16 mark、entity/escape/emoji、prefix gap、outline/reference/fence、Partial/Failed、未解析frontmatterのUnknown、identity/options、resource上限と取消を小さい入力で確認しました。既存2入力のsnapshotはMD-01から変わっていません。[task証拠](../../docs/development/md02-verification.json)に最終commandと失敗・修正の履歴を残しています。

レビュー修正ではcode/escape/entity/link destinationをopaque誤判定から保護し、heading/tableの実HTMLとlink labelにもUnknown mappingを適用します。opaqueの外側のlinkを保持し、通常のimport/export英文をESM候補から除きます。anchorは既存suffix順序を保ちつつ次の候補を記録し、各候補のbudgetと取消を確認します。新しい確認は同じ小さいcanaryに含め、全solution/testや性能測定は追加していません。
