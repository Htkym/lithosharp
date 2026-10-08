# Markdown parserの抽出部品

MD-01では、現行Litho parserのblock/inline解析、node、UTF-16位置、directive名とparser上限を `Portable/` に集めます。同じsourceを使う独立projectの対象はnet10.0/netstandard2.0、言語版はC# 13です。I/O、HTML描画、site診断、Node、SharpDeps graphを参照しません。

内部nodeと位置型のnamespaceは既存の `LithoSharp.Content.Compilation` を維持します。製品はこのsourceをCompile linkで使用し、既存の型所属、assembly identity、描画経路を保ちます。独立assemblyの内部型を製品から参照する変更ではありません。SourceSpanのSiteSourceLocation変換と、LithoLimitsのsite診断は製品側のpartial定義に残します。

## portable境界

netstandard2.0にないIndex/Range、char検索、Stack.TryPopとrecord/initのcompiler markerは、同target限定の内部補助型で補います。GeneratedRegexの5箇所は同じpatternのcached Regexを同targetで使い、net10では既存のsource-generated Regexを保ちます。Spanによる2箇所の文字列処理は、Ordinal比較とUTF-16文字のindex走査に置き換えます。ASCII文字判定の4箇所は、全targetで同じASCII範囲を使う補助関数へ接続します。parserの構文、位置の単位、render policy、既存compiler fingerprintを変更する抽出ではありません。

独立projectの依存はnetstandard2.0のBCL参照だけです。YamlDotNet/frontmatterと公開parse facade・immutable facts・SourceSegmentsはMD-02で実装します。既存runtime/inspection/site adapterはMD-03、compiler host/cache/世代はMD-04、固定版のruntime/source artifact pairはMD-05です。MD-01のsource linkを最終的なpackage供給の代替にはしません。

本projectは現在IsPackable=falseで、公開packageの版は変更しません。別repo移動も行いません。MD-06の移管はIN-01の両consumer確認後です。

## 小さいcanary

[LithoSharp.Markdown.Portability](../../tests/LithoSharp.Markdown.Portability/LithoSharp.Markdown.Portability.csproj)は、製品net10 assemblyと独立netstandard2.0 assemblyを使います。後者のTargetFrameworkAttributeを確認し、誤ってnet10同士を比較した場合は失敗します。

入力は既存C01のbody-basicsと、CRLF/entity/escape/emoji/reference/fence metadataを含む124 UTF-16 unitsの小さい例です。内部node、span、referenceをreflectionで読み、構造を比較します。公開APIをテストのために増やしません。既存の小さいh2/id/body描画と再実行一致、通常経路のMarkdig非ロードも確認します。

この比較の対象は、同じnet10 CLRで行う二つの解析経路です。別Roslyn版やGenerator/Analyzerでの実source compileを実証するものではなく、その確認はMD-04/IN-01に残します。immutable DTO、新strict entryのUnicode/frontmatter方針、SourceSegmentsの実装完了も意味しません。

repo rootから、親が調整した検証枠で次を実行します。全solutionのbuild/testやbenchmarkは完了条件にしません。

~~~powershell
dotnet build src/LithoSharp.Markdown/LithoSharp.Markdown.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet restore tests/LithoSharp.Markdown.Portability/LithoSharp.Markdown.Portability.csproj --disable-parallel -nr:false
dotnet build tests/LithoSharp.Markdown.Portability/LithoSharp.Markdown.Portability.csproj --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet run --project tests/LithoSharp.Markdown.Portability/LithoSharp.Markdown.Portability.csproj --no-build --no-restore
~~~

MD-01のtask証拠には入力hash、command、実行結果、所要時間、未実行理由、所有プロセスの終了確認を残します。測定と競合する間は編集・差分確認を進め、次のローカルbuild/testを待ちます。commit/push後はBocchiのレビューを待ち、MD-02へ進みません。

## MD-01の確認結果

二TFM compileは3.27秒、製品接続canaryのrestoreは2.05秒、buildは2.29秒、runは0.91秒で成功しました。compileは警告・エラー0です。入力2件の構造・span・reference、fence metadata、既存描画の再実行一致、Markdig非ロードを確認し、所有build/test PID 0を確認しました。

[task証拠](../../docs/development/md01-verification.json)には実command、結果、入力/snapshot hash、初回失敗と修正、確認していない後続境界を記録します。実Roslyn host、固定版pack、別repo移管、公開は今回の確認に含めません。
