# HTML portable bridge と compiler host

HT-07 は `eng/html/HtmlSourceHost.projitems` で Core の HTML tokenizer、compact tree、文字参照テーブルと `HtmlLiteralFacts` を Analyzer に取り込みます。parser の正本は `src/LithoSharp/HtmlParsing` です。Search/SiteGenerator に依存する `HtmlFacts.cs` は host に取り込みません。netstandard2.0 で使えない API と index/range 表記だけを同じ意味の表記へ置き換えています。

`HtmlLiteralFacts.Parse` は compiler 診断用の内部入口です。Core と packed Analyzer が同じ入口を持ち、Complete の tree から `href`、`src`、`srcset`、`poster`、`action`、`formaction` と `object` の `data` を観測します。属性名だけで採用する観測であり、URL sink の判定は呼び出し側の責務です。metadata、CSS、srcset の候補分割、base/URI 解決、scheme policy とサイト内照合は扱いません。既存の Quality/Search 呼び出し先は変えていません。

観測には調整後の要素名・属性名、要素/属性 namespace、prefix、要素 span、属性名の原文 mapping、値の原文と decode 値、値の source segments、行・列を含めます。HTML/foreign tree の名前調整と tokenizer の一度だけの文字参照 decode を使います。値の空白、URI の大文字小文字、percent escape、Unicode の正規化形式を変更しません。位置は入力文字列の UTF-16 offset で、行・列は 1 始まりです。CRLF は一つの改行です。

template の内容は通常 tree の走査対象から外れます。script の文字列を HTML として再解釈しません。noscript は同じ `HtmlTreeOptions.Scripting` に従います。重複属性は tokenizer の扱いと診断を引き継ぎます。

未対応構文や budget 超過で tree が Partial/Failed になったときは、URL 観測を返さず、状態と tree/tokenizer の理由を返します。呼び出し側は Complete 以外を「URL がない」と認定できません。cancel は例外として伝播します。これらは新しい HTML 診断 rule や CodeFix の実装ではなく、後続 rule が使う入力契約です。

## 実 consumer 検証

`tests/LithoSharp.Html.Portability` は Core/Analyzer の ProjectReference を持ちません。実 nupkg を展開し、`AnalyzerFileReference` で packed Analyzer を発見した後、内部 bridge を呼びます。runtime の実結果と全 observation を比較します。共通テスト driver は入力と reflection 呼び出しだけを共有し、parser を含みません。

固定 oracle の 12 ケースと、entity/原文/位置、foreign namespace、template/script/noscript、URL 属性、未対応 shadow/foreign fragment、input/node budget の 9 ケースを比較します。runtime テストは原文、decode、namespace、位置と失敗状態を独立に検査します。

consumer は Roslyn 4.14.0 を固定し、実 Analyzer の TFM と assembly references、runtime assembly の実ロードを検査します。Core は canary の metadata reference としてだけ読みます。実 SiteUrl API に bind した LSA1001 Error と元 argument の span を確認します。Core、Syntamark runtime、AngleSharp、SkiaSharp、Node、MSBuild、Workspaces の実ロードは拒否します。現在の consumer process は .NET 10 です。全 SDK/IDE や旧 CLR での実行認定は別途必要です。

## 再実行

repository root で、未使用の絶対出力ディレクトリ `$out` と新しい検証用 `$version` を選びます。固定 Syntamark runtime/source pair は再 pack しません。Analyzer の宣言 version は変更せず、pack 時だけ候補版を指定します。同じ候補を変更して再 pack する場合は別の version と出力先を使います。

```powershell
dotnet restore src/LithoSharp.Analyzers/LithoSharp.Analyzers.csproj --locked-mode
dotnet restore tests/LithoSharp.Tests/LithoSharp.Tests.csproj --locked-mode
dotnet restore tests/LithoSharp.Html.Portability/LithoSharp.Html.Portability.csproj --locked-mode
dotnet build tests/LithoSharp.Tests/LithoSharp.Tests.csproj --no-restore -c Release -m:1
dotnet build tests/LithoSharp.Html.Portability/LithoSharp.Html.Portability.csproj --no-restore -c Release -m:1
New-Item -ItemType Directory -Path "$out/feed","$out/results" -ErrorAction Stop
$env:HT07_RUNTIME_PROOF = "$out/results/runtime-observations.json"
try {
    dotnet test tests/LithoSharp.Tests/LithoSharp.Tests.csproj --no-restore -c Release --no-build -- --treenode-filter '/*/*/HtmlPortableBridgeTests/*' --report-trx --results-directory "$out/results" --report-trx-filename bridge-runtime.trx
} finally { Remove-Item Env:HT07_RUNTIME_PROOF }
dotnet pack src/LithoSharp.Analyzers/LithoSharp.Analyzers.csproj --no-restore -c Release -o "$out/feed" "-p:Version=$version"
dotnet tests/LithoSharp.Html.Portability/bin/Release/net10.0/LithoSharp.Html.Portability.dll "$out/feed/LithoSharp.Analyzers.$version.nupkg" $version tests/LithoSharp.Tests/Fixtures/HtmlObservation/html-observation-corpus-v1.json "$out/results/runtime-observations.json" src/LithoSharp/bin/Release/net10.0/LithoSharp.dll "$out/results/compiler-host"
dotnet test tests/LithoSharp.Tests/LithoSharp.Tests.csproj --no-restore -c Release --no-build -- --treenode-filter '/*/*/(HtmlTokenizerTests|HtmlTreeBuilderTests|HtmlFactsTests)/*'
```

各 native command の exit code を確認し、失敗したら後続を止めます。個人運用で RTK がある場合、build/test は `rtk-dotnet-verify` の wrapper を使います。runtime observations、host observations/proof と TRX を証拠に残します。consumer output は新規ディレクトリを使い、既存 DLL を上書きしません。
