# Markdown共有契約 v1の設計案

J-01は、LithoSharp 2.0.0とSharpDeps 0.2.0候補が使う解析結果、原文位置、版、供給方法を定めるタスクです。本書はBocchiの設計レビュー待ちです。パッケージ、DTO、cacheの実装やcompiler hostでの動作確認はまだ完了していません。J-01のcommit/push後はレビューを待ち、MD-01へ進みません。

公開契約は本書、SharpDeps側の採用文書、両repoの同一fixture JSONで管理します。原資料7件の一覧と詳細照合はローカルの記録に保持し、公開契約には転載しません。[J-00の基準](../development/2.0.0-baseline.ja.md#計画の参照基準)を引き継ぎます。

## 現行コードと変更の位置付け

LithoSharpの基準SHAは`e6419c202bb95bb0055650bc7f04e0dcd6fe0f29`、SharpDepsの基準SHAは`562aacf22822d19cf022a4fdcc67735d7049be85`です。J-00の追加入力は公開基準文書だけで、次の実装観察は同じ基準に基づきます。

| ソースパスとシンボル | 現行の動作 | J-01で定める変更・維持条件 |
| --- | --- | --- |
| `src/LithoSharp/Content/Compilation/SourceSpan.cs` / `SourceSpan` | UTF-16のzero-based半開区間。内部の`Empty`は位置0 | 公開のUnknownをnullable spanで区別する |
| `src/LithoSharp/Content/Compilation/SourceText.cs` / `SourceText` | CRLF、LF、lone CRを保持して1-based位置へ変換する | 原文全体のoffsetを維持する |
| `src/LithoSharp/Content/Compilation/FrontMatterSplitter.cs` / `FrontMatterSplitter` | 実際の先頭BOMと厳密な`---`行を認識する | genericのoptional frontmatterとsiteの必須条件を分ける |
| `src/LithoSharp/Content/Compilation/LithoNodes.cs` / `LithoLineMap` | body相対spanとprefix除去後の行対応がある | decoded→raw segmentsとfence各部の位置を追加する |
| `src/LithoSharp/Content/Compilation/LithoMarkdownCompiler.cs` / `AnalyzeForInspection`, `CollectHeadingTexts` | inspectionでもheading textをrenderer経由で作る。siteではh1をh2へ投影する | 共有のtext投影を描画から独立させ、output levelはsiteに残す |
| `src/LithoSharp/Content/MarkdownContentCollectionLoader.cs` / `ParseYaml`, `ParseYamlNode` | YamlDotNet event parser。anchor/alias、重複key等を拒否する | 同じ構文方針を使い、raw key/valueの終端と位置精度を追加する |
| `src/LithoSharp/Inspection/DocumentInspection.cs` / `DocumentInspection` | 必須frontmatterのLSM診断。missing/unclosed時は全文をbodyとして解析する | 新generic entryと旧site互換adapterを区別する |
| `src/LithoSharp/Content/Compilation/MarkdownDocumentFingerprints.cs` / `SourceHash`, `SemanticHash` | SourceHashはUTF-8の本文だけ。SemanticHashは位置・identityを含まない | 新TextHashを別に追加し、既存hashの意味を維持する |
| `src/LithoSharp/Content/Compilation/LithoLimits.cs` / `MaxNestingDepth`, `RemoveDiacritics` | 深さ上限200。slug用の正規化前にunpaired surrogateを置換する | 新strict entryのUnicode処理を旧entryへ機械転用しない |
| `src/LithoSharp/Inspection/DocumentWorkspace.cs` / `DocumentWorkspace` | owner、epoch、generation/version予約、取消、遅延publish拒否がある | この順序保証を維持し、共有parse cacheを後続で実装する |
| `src/LithoSharp.Generators/LithoSharp.Generators.csproj` | netstandard2.0、Roslyn 4.14.0、YamlDotNet 18.1.0 | net10 facade DLLをcompiler hostへ読み込ませない |
| SharpDeps `analyzer/src/SharpDeps.Analysis.Core/Identity/Identity.cs` / `DocumentId` | workspace/project variantに基づく既存graph ID | parserのopaque SourceIdと永続graph IDを区別する |
| SharpDeps `analyzer/src/SharpDeps.Analysis.Contracts/AnalysisSnapshot.cs` / `AnalysisSnapshot` | 既存report v2とcoverage/capabilities | 新Markdown coverageでreport v2を置換しない |
| SharpDeps `src/security/trust.ts` / `trustDecision` | untrusted workspaceでは解析・restoreを禁止し、保存結果の表示を許可する | Quick/Semanticを含む既存のtrust方針を維持する |

## 責務と入力

共有部品はfrontmatterの構文事実、Markdown構文、text投影、位置、診断、coverageを扱います。HTML描画、site route/member解決、型付きschemaやuser code、HTML bridgeはLithoSharp側です。Markdownからgraphへの変換、SQLite、Query、clientはSharpDeps側です。AngleSharp DOM、Roslyn型、site objectを共有DTOに含めません。

入力はcallerが渡した文字列または`ReadOnlyMemory<char>`です。parserはpathを解釈せず、ファイル、ネットワーク、Node、MSBuild、ユーザーコードを実行しません。バイト列のdecode、byte hash、decoder方針はcallerの責務です。BOMをdecoderが除去した場合は、その除去後の文字列が本契約の原文です。

mutable bufferは上限を確認してから一度だけowned stringへコピーします。callerはコピーを含む呼出し中にbufferを変更しません。返却DTO、各要素、collectionを深くimmutableにし、mutable backing array/listを外へ公開しません。

`ScopeId`と`SourceId`は空でないopaque文字列、`SourceVersion`はopaque文字列またはnullです。callerがworkspace instanceごとのScopeIdを割り当て、filesystem等の正規化もcallerが行います。比較はordinalで、大小文字やUnicode正規化をparserが変更しません。識別子は整形式のUnicodeに限定し、不正な識別子やoptionsはargument errorとします。SourceVersionの文字列をgenerationの大小比較に使いません。

Cancellationは`OperationCanceledException`としてcallerへ戻し、結果をpublish/cacheしません。プログラム上の不変条件違反を、成功や入力診断へ変換して隠しません。

## 原文位置とdecoded textの対応

`RawSpan`の設計形は`Start`と`Length`です。値は原文全体のUTF-16コード単位に基づくzero-based半開区間です。`End = checked(Start + Length)`を使い、`0 <= Start <= End <= raw.Length`を満たします。fixtureの配列は読みやすさのため`[start,end)`で記載します。

実際のBOMは1単位、CRLFは2単位、surrogate pairは2単位、lone CRは1単位です。既知の空位置はLength=0、不明な位置はnullです。不明位置をoffset 0やline 1で代用しません。body相対位置は公開前に原文全体へ変換します。

line/columnは原文offsetから派生する1-based UTF-16値です。CRLF、LF、lone CRはそれぞれ1回の改行として数えます。parserに渡していない元バイトのoffsetやBOM位置を復元しません。

decoded text自体が未投影ならnullとし、segmentsは空、decoded coverageはUnknownです。既知のraw regionは保持します。各decoded stringに`SourceSegments`を付けます。segmentのDecodedSpanはdecoded string内のUTF-16位置、RawSpanは入力原文全体の位置です。

| 種別 | 対応と照会結果 |
| --- | --- |
| `Linear` | 同数・同順のUTF-16単位が対応する。部分照会を同じ長さのrawへ移せる |
| `Atomic` | entity、backslash escape、CRLF正規化等のtoken全体に対応する。部分照会でもraw token全体を返し、`CoveringTokens`とする |
| `Unknown` | raw対応を確定できない。RawSpan=nullとし、Exactを返さない |

decoded spansは昇順、非重複でdecoded string全体を覆います。空decoded stringのsegmentsは空です。known raw spansは単調でgapを許します。prefixを除去した箇所や構文markerのgapを埋めません。

照会結果は`RawFragments`と精度を返す設計です。全Linearなら`Exact`、Atomicを含めば`CoveringTokens`、Unknownとknownの混在なら`Partial`、known対応がなければ`Unknown`です。接しているraw fragmentsだけを結合できます。空照会では一意のraw境界を確定できる場合だけ既知の空spanを返します。surrogate pairの途中のUTF-16照会も許しますが、Unicode文字単位の選択精度は保証しません。

例えば`> a\r\n> b`から`a\nb`へ投影した全文照会は`[2,5)`と`[7,8)`です。除去した`[5,7)`のprefixを含む`[2,8)`へ広げません。raw HTMLやMDXの未投影部分も、架空のExact位置にしません。

## DTOの意味と不変条件

型・プロパティ名は実装時の設計名です。公開ASTの機械的な移植を避け、次の意味を持つimmutable factsを提供します。serialization用の任意objectや実装依存のYamlDotNet Markは公開しません。

| facts | 必要な内容 |
| --- | --- |
| envelope | ContractVersion、ParserVersion、ProfileId、OptionsHash、TextHash、ScopeId、SourceId、SourceVersion、Status、Capabilities、Coverage |
| document | BodySpan、FrontMatter、Headings、Sections、Links、Fences、TextRegions、Diagnostics |
| frontmatter | 状態、opening/YAML/closing raw spans、mapping/sequence/scalarの構文関係、scalar key/valueのraw spansとdecoded scalar。未確定対応はUnknown |
| heading | RawLevel、decoded Textとsegments、RawSpan、nullable TextRawSpan、Anchor、LocalKey |
| section | LocalKey、ParentLocalKey、nullable HeadingLocalKey、DirectBodySpan、SubtreeSpan。preambleを必ず1つ持つ |
| link | inline/autolink/reference use・definitionの区別、label textとsegments、target/title literal、nullable target raw span、Markdown定義内の解決状態 |
| fence | opening marker/info/content/closing raw spans、Closed、info literal、content decoded textとsegments |
| text region | kind、nullable decoded text、segments、nullable raw envelope span。未投影領域を別に示す |
| diagnostic | Id、Severity、Message、nullable RawSpan、Origin、Reason。callerが必要に応じてline/columnを派生する |

Heading RawSpanは最後のsyntax行の終端改行を除きます。Setextの場合はunderline行までを含みます。TextRawSpanはraw labelの外接範囲で、decoded textのExact対応を意味しません。細かな選択はsegmentsを使います。既存のslugと重複suffix規則を共有のpure投影へ移し、既存Anchorを維持します。siteのh1→h2というOutputLevelは共有factsへ混ぜません。

Sectionsは、原文順の見出しとRawLevelから作るoutlineです。block ASTの親子関係とは区別します。preambleはBodySpanの開始から最初の見出し開始までです。見出しsectionのSubtreeSpanは、その見出し開始から次の同じlevel以上の見出し開始、またはbody末尾までです。ParentLocalKeyは直前の低いlevelのsectionを指します。

DirectBodySpanは見出しの最終syntax行と終端改行の後から、最初の子見出し開始またはsection末尾までです。子sectionの本文を親のdirect bodyへ二重計上しません。LocalKeyはdocument内の順序に基づくkeyであり、編集で変わる場合があります。永続graph IDには使いません。不明BodySpanの場合はpreambleの位置もUnknownにし、未生成のsectionsを完全な一覧と表示しません。

LinkのResolutionは`Inline`、`ResolvedReference`、`UnresolvedReference`です。参照先のMarkdown definition探索までを共有部品が扱い、site route、C# member、外部URLへの到達性はconsumerが解決します。fenceのinfoは文字列として扱い、実行しません。未閉じcode fenceはCommonMarkで有効なので、Closed=falseだけを理由にPartialへ変更しません。挿入した表示用separatorを含む全文plain textはconsumerが作り、存在しないraw位置を付けません。

## Profile、frontmatter、coverage、診断

初期ProfileIdは`lithosharp-markdown/1`です。CommonMark 0.31.2、GFM 0.29および現行Lithoのextension規則を起点とし、抽出時に既存の構文を変更しません。featureごとの静的なCapabilitiesと、今回の入力で分かったraw位置・decoded対応のCoverageを分けます。

Capabilitiesのfeature stateは`Supported`、`Unsupported`、`Partial`です。Coverageには`RawPositions`、`DecodedMapping`の各`Complete/Partial/Unknown`と、未投影regionおよび理由を持たせます。空の対応対象はCompleteです。raw HTML/MDXをopaque regionとして保持することと、そのtextを完全に投影できたことは区別します。このcoverageでsiteのcontext解決やSharpDeps report v2の解析完了を宣言しません。

| generic frontmatterの状態 | BodySpanと解析 | site互換adapterの条件 |
| --- | --- | --- |
| `Absent` | 先頭BOMを除いた全文がbody。frontmatter欠如のみではCompleteを妨げない | 必須条件のLSM001を既存severity/位置で返す |
| `Parsed` | delimiter後のbodyは既知 | 型付きschema/bindingはsite側で行う |
| `Empty` | 空YAMLと既知body。欠如のみではCompleteを妨げない | 既存LSM003を返す |
| `Invalid` | closingが分かればbodyを解析し、全体はPartial | reasonに対応する既存LSM004〜008へ投影する |
| `Unterminated` | BodySpanはUnknown、全体はPartial。frontmatterをMarkdownとして二重解釈しない | 旧entryだけは全文bodyのfallbackとLSM002を維持する |

delimiterは現行と同じ厳密な`---`行です。新generic entryは実際の先頭BOMを1つだけ構文解析から除外し、raw offsetとTextHashには残します。AbsentのBodySpan開始はBOMありなら1、なしなら0です。Parsed/Empty/Invalidではclosing delimiter後、UnterminatedではUnknownです。例えばBOM付き`# A\r\n`のBodySpanは[1,6)、Heading RawSpanは[1,4)、preambleは[1,1)です。旧site entryの全文fallbackは現行どおりoffset 0と元の文字列を使い、この新規則を機械適用しません。YAMLはevent parserを使い、1 document・mapping root・非null scalar keyという現行方針を維持します。anchor/alias、重複key、非scalar keyを拒否し、任意型のdeserializeやalias展開をしません。

YAMLのevent開始/終端をraw key/value spansへ変換します。Mark.Indexの単位とEndの意味はMD-02の小さいfixtureで実確認し、その後にUTF-16 offsetへ変換します。現時点ではライブラリのIndexを無条件にUTF-16と断言しません。変換できない位置やfolded scalarのdecoded対応はUnknownです。

`Status=Complete`は要求したprofile factsを完了した場合です。未投影の必要なfacts、invalid frontmatter、途中のresource上限等はPartialです。入力上限超過や新strict entryのinvalid Unicode等、意味のあるfactsを作れない入力はFailedです。Partial/Failedは理由を残し、collectionが途中までの場合はcompleteと扱いません。

| 新しい共有診断の予約ID | 条件 | severity |
| --- | --- | --- |
| LMD001 | new strict entryのunpaired surrogate | Error |
| LMD002 | new strict entryの入力上限超過 | Error |
| LMD003 | 途中のnode/depth/scan上限 | Warning |
| LMD004 | generic frontmatterが未閉じ | Warning |
| LMD005 | frontmatter構文/構造が不正。Reasonにsyntax、duplicateKey、alias、invalidKey、invalidRoot、multipleDocumentを保持 | Warning |
| LMD006 | 要求したdecoded対応が未完成で、説明を要する場合 | Information |

LMDは今回予約する新prefixです。既存LITのunsupported advisory、LSM、LSG、LSQのIDと意味は維持します。LSA番号は後続RAで定めます。同じcauseの共有診断とsite LSMを二重表示せず、site adapterでは既存ID・severity・位置を優先します。generator/analyzer側のblocking方針は既存のconsumer契約を維持します。

## TextHash、OptionsHash、版

TextHashは、渡された原文全体をstrict UTF-8へ変換したSHA256のlowercase hex64です。BOM、frontmatter、改行、空白を含み、正規化しません。encoderのpreambleを追加しません。unpaired surrogateはencode前に判定し、new strict entryではFailed/LMD001、該当raw span、TextHash=nullを返します。入力上限超過もTextHash=nullとし、大きなhash計算を始めません。

既存SourceHashはUTF-8の本文だけという意味を保ちます。既存SemanticHashはplain text、heading、link、assetのsite投影を対象とし、位置・identityを含みません。新しいpublic SemanticHashは追加せず、既存SemanticHashを位置付きDTOやparse cacheのvalidity判定に使いません。

ContractVersionは`1.0`、ParserVersionは`1/`とcanonicalSourceHashのlowercase hex64を連結した値です。componentVersionはNuGetの版で、これらと別です。DTOのoptionalな追加はcontract minor、既存shapeや意味の変更はmajorです。grammar、slug、依存、profile、optionsの意味を変えた場合はcanonicalSourceHashとParserVersionを更新します。profileの意味を変える場合はProfileIdの版も更新します。

OptionsHashは有効なparse optionsのcanonical JSONをstrict UTF-8でSHA256へ変換したlowercase hex64です。Contract 1.0のpublic optionsは次の表の6項目だけです。省略はdefaultへ展開してからhashし、未知の項目、非整数、不正値を拒否します。ProfileIdは上記の1種類、optionsSchemaVersionは1を受け入れ、4つの上限値は1〜Int32.MaxValueとします。

| canonical JSON key | default | 意味 |
| --- | --- | --- |
| profileId | lithosharp-markdown/1 | 選択profile |
| maxInputUtf16 | 1048576 | 入力UTF-16単位 |
| maxOutputItems | 131072 | emitted node、segment、diagnostic等の合計数 |
| maxNestingDepth | 200 | Markdown/YAMLの構造深さ |
| maxScanUnits | 16777216 | 自前scannerが調べるUTF-16単位の延べ数 |
| optionsSchemaVersion | 1 | optionsの意味とcanonical化の版 |

keyはASCIIのordinal昇順、whitespaceなし、integerは十進表記、文字列はquote/backslashとcontrolをescapeします。controlはlowercase hexの`\u00xx`、その他の整形式UnicodeはそのままUTF-8とし、slashをescapeしません。配列を将来追加する場合は順序を保持し、nullable項目はnullを明示します。SourceId、世代、route/context、時刻、Cancellationはparse optionsに含めません。

## 上限とcacheの設計条件

maxOutputItemsはfrontmatter構文node、heading、section、link use/definition、fence、text region、source segment、diagnosticを各1件として合計します。envelopeとspan値そのものは別件に数えません。

上記defaultは新entryの設計判断であり、現行実装の計測値ではありません。上限は正の整数としてcallerが明示設定でき、無限・disableを許しません。入力上限はcopy/hash/parse前、深さ上限は再帰前、出力上限は追加前に確認します。自前scannerは再走査も延べ数へ加算し、loop/event処理で取消を確認します。

opaqueなYamlDotNet内部処理の全命令数をmaxScanUnitsで計数できるとは主張しません。YAMLには入力、event出力、深さの上限とalias禁止を適用します。途中上限では検証済みのfactsだけをPartialとして返します。論理的な処理上限はCPU時間や実heapの固定SLAではありません。

新parse cacheはworkspace instanceに属し、global cacheにしません。keyは`(ScopeId, SourceId, SourceVersion, TextHash, ParserVersion, ContractVersion, ProfileId, OptionsHash)`です。null SourceVersionも区別します。同じ原文hashでも別文書・別workspaceのDTOを返しません。

同じScopeId/SourceIdでは最新1件だけを保持し、LRU、entry数、retained logical bytesの上限を設けます。初期cache設定は64 entries / 16 MiBです。論理bytesは保持rawとDTOの全string fieldのUTF-16単位×2に、各fact 64 bytes、各source segment 32 bytesを加えます。同じstringを複数fieldが参照してもfieldごとに数え、source segmentはfact側へ二重計上しません。これは実heap使用量の保証ではありません。1 entryが上限を超えればcacheしません。cache設定はparse結果を変えないためOptionsHashに含めません。

cacheするのはCompleteでTextHashがある結果だけです。Failed、Partial、取消結果はcacheしません。Remove/Clear/Disposeで保持を解放し、epochを進めます。以前返したimmutable DTOの内容は変わりません。content-only cacheを将来採用する場合は、返却ごとにidentityを新しいimmutable DTOへbindする別レビューが必要です。

workspaceは予約、解析、publishの順に処理し、publish時にepochと最新予約を再確認します。generationを先に、同generationではversionを比較する現行順序を保ちます。Remove後に古い結果を復活させません。現行DocumentWorkspaceのProject参照による保守的な再解析条件も維持します。

syntax cacheのhitはroute/member/site contextの解決結果を保証しません。consumerの解決cacheにはproject、route、symbol等の世代を含め、contextが変われば再解決します。新しいparse cacheはMD-04で実装し、現在すでに上記上限があるとは記述しません。

## runtimeとcompiler hostへの同版供給

正式なIDの設計案はruntime NuGet `Syntamark`とsource NuGet `Syntamark.Source`です。両方を同じimmutable version Vで作ります。初期Vは`2.0.0-preview.N`、stable候補は`2.0.0`です。ここではartifactをpack/publishしません。

net10.0のpublic facadeと、Generator/Analyzerのnetstandard2.0 hostは同じcanonical portable source treeを使います。portable sourceはnetstandard2.0 APIでコンパイルできる範囲に限定し、host/TFMによってgrammar・位置・hashの意味を分岐させません。net10 DLLをcompiler hostへ読み込ませません。

source payloadは`src/Portable/**/*.cs`と生成したversion stampです。namespaceとfactsはinternalとし、compiler hostへpublic facadeのDTOを二重に定義しません。host adapterが内部factsを既存のconsumer型へ投影します。必要な言語機能用helperもcanonical payloadで管理し、consumerが手作業で補修しません。

source packageには`build/Syntamark.Source.targets`を置き、project側の`SyntamarkIncludeSource=true`で明示的にCompileへ取り込みます。project propertyの設定前に評価されるpropsへ条件付きCompileを置きません。buildTransitive/contentFilesによる無条件のsource取り込みは使いません。

source PackageReferenceは`Version="[2.0.0-preview.N]"`というexact range、`PrivateAssets="all"`、`IncludeAssets="build"`を使う設計です。plain versionは最低版の指定なので固定版の証明には使いません。[公式PackageReference](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)と[MSBuild filesの配置](https://learn.microsoft.com/en-us/nuget/create-packages/creating-a-package#including-msbuild-props-and-targets-in-a-package)に基づきます。

portable sourceが必要とする依存はhostがcompile/runtime用に直接参照します。初期YamlDotNetは現行と同じexact 18.1.0、compiler hostではPrivateAssets=allとし、analyzerへの同梱は現行方針を引き継ぎます。runtime nupkgのYamlDotNet依存も`[18.1.0]`のexact rangeで宣言します。consumerは復元結果とロード済み依存のversion/identityをpair manifestと照合し、不一致または照合不能なら新entryの起動を拒否し、そのcacheを再利用しません。NuGetの直接依存優先やhostのbindingで違うDLLが選ばれても、同じParserVersionの結果として扱いません。ロード済みidentityの照合はconsumer startup側で行い、pure parserへファイルI/Oを追加しません。dependency graphをmanifestで照合し、source packageを復元しただけでDLLのhost配置が終わったとは扱いません。

移管前のLithoSharp.Generatorsもsource packageのexact参照を使います。bootstrapは、Generator/Analyzer/LithoSharp siteを参照しない独立componentのpack、未公開の固定版local feedへのartifact pair配置、manifest照合、両repo hostのrestore/buildの順です。最初のhost確認に必要な開発pairはMD-03/MD-04で用意し、最終pairの配布準備はMD-05で扱います。packするcommitとVを固定し、変更時は新Vを使います。公開NuGetへのpublishをbuildの前提にせず、repo内のlatest source直接Compileでも代用しません。

latest checkout、浮動版、手動copyは同版供給の代替にしません。runtime facadeとsource hostの実際のcompile、同一profileのfixture出力一致はMD-03/MD-04/IN-01の完了条件です。その証拠が揃うまでstable公開へ進みません。

## artifact pair manifestとrepo移管

canonicalSourceHashは、portable sourceのPOSIX相対pathと選択commitのGit blob bytesに加え、固定dependency manifest、profile仕様、options schemaのbytesを対象にします。checkoutのCRLF変換を対象にせず、packするcanonical payloadにも同じbytesを使います。生成version stampは対象から除き、循環を防ぎます。

hash入力は対象fileをpathのUTF-8 byte順に並べ、各fileについてpath byte長、path bytes、content byte長、content bytesを連結します。各長さはunsigned 64-bit big-endianです。dependency/profile/optionsの規範filesもpathとbytesを持つ対象fileに含め、曖昧な文字列連結を避けます。

| manifest field | 必須内容 |
| --- | --- |
| componentVersion | runtime/sourceで一致するV |
| canonicalSourceHash | 上記canonical入力のSHA256 |
| ParserVersion / ContractVersion / ProfileId | runtime/sourceで一致する解析契約 |
| sourceCommit | canonical入力を選んだcommit。cache keyには使わない |
| dependencies | portable sourceのcompile/runtime依存のexact versionと、pack時のloaded assembly identity / informational version |
| canonicalFiles | path、byte length、SHA256の一覧 |
| artifacts.runtime | packageId=`Syntamark`、nupkg SHA256 |
| artifacts.source | packageId=`Syntamark.Source`、nupkg SHA256 |

両nupkgに同じ解析metadataを含め、nupkg全体のhashは外部のpair manifestだけに記録します。自分自身のhashをpackage内部へ含めません。runtime hashとsource hashは別kindなので一致を要求しません。同じkindのartifactを使うconsumer間でhashを一致させ、共通canonicalSourceHashによってpairを確認します。実hashはpack後に計算し、本タスクで架空のartifact hashを発行しません。

両repoとcompiler hostで同版を実証した後、stable公開前に別repoへ移管します。package名、API、契約を維持し、旧commit→新commitの対応を移管記録へ残します。同じVの再packでbytesが変わる場合は新preview版を発行し、既存artifactを上書きしません。別repoの作成や名前の決定はMD-06です。

## 互換adapterと後続担当への指示

new strict entryのUnicode・resource・frontmatter方針を、旧public entryへ無条件に転用しません。legacy adapterの結果は新parse cacheへの保存も参照もしません。既存DocumentWorkspaceのinspection結果の再利用は別に管理します。旧site APIでは既存の入力処理、診断ID/severity/位置、SourceHash/SemanticHash、missing/unclosed fallback、OutputLevelを保ちます。adapterに必要なlegacy入力方針は同じcanonical grammarの呼出し条件として扱い、別parserのcopyを作りません。legacy方針を新entryのProfileId/OptionsHashと同じcache keyへ混ぜません。

既存entryの構文spanに改行を含む場合は、その既存spanをadapterで維持し、新Heading RawSpanの定義へ機械置換しません。新DTOの追加と既存DTOの挙動変更を分けます。互換差が避けられない場合は、変更点と移行方法を示して後続レビューへ戻し、未記録の破壊変更を採用しません。現時点で許可されたmajor変更はJ-00にあるTestingの公開DOM撤去です。

MD-01は描画・site処理を残したままportableなparse-only部品を抽出します。MD-02は上記immutable facts、segmentsとYAML位置、MD-03はruntime/inspection/site adapter、MD-04はcompiler hostとcache/世代、MD-05は固定版artifact pair、IN-01は両repo/hostの同版実証、MD-06は別repo移管を担当します。既存タスクの依存とレビュー停止条件を変更せず、新しいlaneやタスクを増やしません。

## J-01の確認と停止条件

`markdown-v1.fixtures.json`は机上の契約例で、parserの実行結果やgolden corpusではありません。BOM/CRLF/frontmatter、entity/escape/surrogate pair、除去prefix、同本文・別identity、unpaired surrogateの5件を扱います。原文長、既知spanの文字列、Linear対応、strict UTF-8 hash、4つのbound keyの区別を軽いscriptで確認しました。AbsentのBOM付きheading例も確認します。invalid UnicodeのcaseはrawUtf16CodeUnits=[55296]から入力を構成し、JSON readerの拒否や置換を経由させません。JSON読戻しはNode.jsで確認し、両repoに同一bytesを保存します。

公開diffの範囲、JSON読戻し、fixture同一性、git diff --check、原資料一覧等の非混入を確認します。full build/test、pack、実parser検証は文書中心のJ-01では実行しません。同PCの計測に干渉するローカルbuild/testは、必要になった後続タスクでBocchiと調整します。

SharpDepsのJ-00修正SHA `46f910b3744a5e07328a388119910f4d1ab70342`のCI run `37709418593`はLinux成功、macOS失敗でした。失敗は既存`tests/analyzer/reviewRegression.test.ts:136`のfixture用`dotnet restore`で、詳細stderrは保存されていません。文書だけの修正との因果は確認できず、原因は未確定です。前run `37708742929`の成功だけで原因をflakyと断定しません。J-01 push後のCI状態はSHAとともに親へ別途返します。

J-01の完了判定は、上記契約、両consumer採用表、同版source供給とpair manifest、机上例が揃い、実diffと確認結果・未実行理由を返せることです。Bocchiの採用レビューが通るまでJ-01はレビュー待ちで、MD-01へ進みません。
