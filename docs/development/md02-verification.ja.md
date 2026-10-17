# MD-02の実装と確認記録

共通Markdownの新しいparse-only entryとimmutable factsを実装しました。初回MD-02のSHA `41f80208cd7256421f84dcb927a7d34308704a91`は対象のbuild/canaryに成功しましたが、Bocchiの全25ファイルの実diffレビューで3点の修正を求められました。同SHAを基準に修正し、現revisionの両TFM buildとcanary build/runも成功しました。以前のPASSは基準SHAへ限定し、今回の確認と分けて記録しています。MD-03へは進みません。

## 実装の範囲

公開entryは `LithoSharp.Markdown.MarkdownParser.Parse` です。callerのowned string、identity、6項目のtyped options、CancellationTokenを受け取ります。内部factsはcanonical Portable treeに、公開facadeはPublic treeに置きました。既存siteのpublic APIへ新DTOを二重に定義しません。

| ソースとシンボル | 確認する内容 |
| --- | --- |
| Portable/MarkdownFacts.cs・Public/Markdown.cs | immutable DTO、collectionのcopy、raw UTF-16 span、line/column、query |
| Portable/MarkdownSourceMapping.cs / MdSourceMapping | Linear・Atomic・Unknown、raw fragments、prefix gap、空境界 |
| Portable/MarkdownFrontMatter.cs / MdFrontMatterParser | 厳密delimiter、optional YAML、raw key/value、alias等の拒否 |
| Portable/MarkdownParser.cs / MdParser | strict Unicode/hash、headings、outline sections、link facts、fence、coverage/診断 |
| Portable/LithoBlockParser.cs・LithoInlineParser.cs | 新entryの位置情報とresource context。旧entryはcontext=null |
| Portable/LithoSlug.cs・LithoLimits.cs | 既存のpure slugとUnicode helperを移管。旧siteも同じ実装を呼ぶ |

headingsのoutlineはASTのparentではなく原文順とRawLevelを使い、preambleを持ちます。参照定義は後方にも解決し、重複定義のうち最初のものを従来どおり採用します。解決済み・未解決のreference useとdefinitionを区別します。site routeやC# memberは解決しません。

entity、backslash escape、CRLFの正規化はAtomicとして対応し、除去したquote/list prefixのgapを埋めません。fenceはopening/info/content/closingのspanを持ち、未閉じでも原文にあるcontentだけを投影します。旧rendererが挿入する最後のLFは新factsへ架空のraw位置として追加しません。

frontmatterのmapping Childrenはkey/valueの交互順、sequence Childrenは原文順です。typed schemaや任意型へのdeserializeを行いません。scalarは構文上のdecoded文字列で、nullというliteralを型付きnullへbindしません。keyの重複判定は現行のFormC正規化を維持し、返す原文とidentityは正規化しません。

## Partialと上限

folded/literal YAML、indented code、block math等でdecoded対応をまだ確定できない場合はUnknown segmentとLMD006を返し、Partialとします。raw HTML/MDXのtagとESM候補はopaque regionとして原文spanを残します。現revisionではparagraph全体を除外せず、対応するtokenのmappingをUnknownにし、外側のtextとlinkを保持します。headingとtable cellにも同じ処理を適用します。公開Capabilitiesもdecoded mappingをPartial、HTML/MDX投影をUnsupportedとします。今回の実装を全構文の完全なtext投影とは記述しません。

入力上限はcopy/hash/parseの前に、invalid Unicodeはstrict UTF-8 hashの前に検査します。途中のscan/depth/output上限はLMD003とPartialで示します。出力ではresource診断用に1件を予約し、上限前に停止する場合があります。原文の対応を作れなかったcollectionをCompleteとは扱いません。

自前scannerのloopには再走査の計数と取消確認を入れ、string/regex操作のoperandは保守的に計数します。loopの固定chargeは複数回のUTF-16参照を上から数えるため、実際に読んだ最小単位数より多くなる場合があります。YamlDotNet内部の全命令数、CPU時間、実heapの固定上限は保証しません。取消や不変条件違反を入力診断へ変換しません。

## 版と互換性

ParserVersionはcanonical source・dependency/profile/options規範JSONのGit blob bytesから作りました。生成stamp自身はhash対象から除外しています。`md02-canonical-source.json` は開発中のsource metadataで、sourceCommit・componentVersion・artifactは未発行です。MD-05でpackするcommitと固定版のpair manifestを確定する必要があります。

旧siteのmissing/unclosed frontmatter fallback、LSM診断、OutputLevel、SourceHash/SemanticHash、描画処理はMD-03の対象です。MD-02では旧parserのspanとgrammarを維持し、新entry専用metadataを追加しました。新parse cache、runtime/inspection adapter、compiler hostへのpair供給はまだ実装していません。

MD-01のexact SHAに対するCI検索はrun 0件で、未開始でした。成功とは記録しません。MD-01でMatchAtをSpan.SequenceEqualからCompareOrdinalへ変更した影響はnet10にも及びます。性能は未測定で、速度不変を主張しません。性能観測は後続の測定枠で扱います。MD-02で追加したcontext=nullの分岐や位置metadataの処理も未測定です。旧entryの性能不変を主張しません。

## 小さい確認

既存のportability console canaryに、BOM/CRLF/YAML、entity/escape/emoji、prefix gap、nested heading/reference/fence、setext、slug、Partial/Failed、identity/options、上限と取消の短い確認を加えました。YamlDotNet 18.1.0のMark.IndexとEndは、emoji前後のeventを実行してUTF-16の半開区間であると確認しました。package版18.1.0のロード済みassembly versionは18.0.0.0、informational versionは18.1.0でした。後続のloaded identity guardで、package版をassembly versionと単純に等値比較しません。

対象のrestore、Markdown単体の両TFM build、既存console canaryのbuild/runだけを実行しました。full solution/test、benchmark、coverage、pack、公開は実行しません。正確なcommand・終了状態・未実行理由は `md02-verification.json` へ記録します。

## CLIの確認

Muse Spark 1.3はUTF-16の位置計算2件を机上で確認し、一致しました。CopilotはSonnet 5.5/mediumを明示し、toolなしでmappingとoptionsの実ソースだけをレビューしました。空Atomic tokenのguardと未投影textのUnknown queryを修正しました。出力の1件予約は上限を使い切る保証ではなく、診断を含む件数を上限内に保つための保守的な停止条件として維持します。正の整数1以上という採用契約は変更しません。

Copilotの使用前後に残量を確認し、重要レビュー用の予約分を確保できることを確認しました。累計とsession usageの生値はローカル記録に保持し、直後の累計差0を無料とは扱いません。実応答model/effortが指定に一致し、Auto・tool・追加委任は使っていません。AGYの最新一覧で確認したGemini 3.8 Flash High＋YomiyasuはSUCCESS、1turnで、意味を保つ日本語3件の修正を反映しました。Fast tierは使っていません。これらはcompileやparser実行の証拠ではありません。

## 初回SHAの確認記録

frontmatterの走査途中で上限へ達した場合に、未確認のStateが既定値のAbsentになっていた点をUnknownへ修正しました。Partial/Failedでまだ判定できないfactsを欠如確定とは扱いません。J-01の5つの入力状態の意味は維持し、未完了の表現を追加しました。短いYAML途中停止の確認とstrict Unicode失敗の確認も通っています。

初回の両TFM buildは4種類のcompileエラーとnullable警告で失敗しました。collectionの型変換、string operand、guard後のcontext参照を修正し、再buildで通りました。その後、reference targetの照合を辞書参照にして最初の定義を維持し、opaque候補の再走査もbudgetへ計上しました。最後のUnknown修正後にも同じ対象を確認しました。失敗と途中のPASSを最終sourceの証拠に混ぜず、全commandの履歴をJSONへ残しています。

最終の両TFM buildは4.11秒、canary buildは6.10秒、runは0.97秒でした。両buildは警告・エラー0です。最初のrestoreは2.22秒でした。既存2入力の構造/span/referenceのsnapshot hashはMD-01と同じで、小さい既存描画、再実行一致、Markdig非ロードも維持されています。

2026-10-08T05:19:01.1680388Zに、記録したroot PID・現存する子プロセス・taskのcommandに一致する所有build/test PID 0を確認して測定枠を返しました。共有compilerとMSBuild node reuseは無効にし、他タスクのプロセス停止は行っていません。この終了確認は初回SHAの検証枠に対応します。レビュー修正の検証結果は後述します。

初回SHAのlocal確認後、実diffレビューで以下の修正が必要と判明しました。レビュー修正の検証は完了しました。MD-02だけをfeature/2.0.0へcommit/pushして再レビューへ返します。MD-03、mainへのmerge、PR、tag、pack、公開には進みません。

## 実diffレビューの修正

旧IsOpaqueはparagraphのraw文字列を検索していたため、code spanの `<Widget>`、escaped `\<Widget>`、entityやlink destinationを実際のHTMLと区別できませんでした。新entryのinline scannerが実際に到達したtag候補だけを記録します。引用符内の `>` を含むtagも全体のspanを取り、内部の擬似Markdown linkはfactsへ出しません。外側の `[Guide](/docs)` は保持します。既存ASTの文字列とgrammarは変更せず、factsの対応だけをUnknownにします。

重複headingのsuffix探索は基底名ごとの次の候補を記録し、毎回1から探し直しません。実際の `A-1` が混在しても既存の最初の空き番号を使う順序を維持します。新entryでは基底名のhashと各候補の生成・hashの前にscanを計上し、取消を確認します。legacy Assignも同じ割当器をcontextなしで使います。NoListMarkerは共有static readonlyへ戻しました。

実行した再現用canaryは、protected tokenと通常link、引用符付きtag、heading/tableのPartial、未解決reference label、ESM後のlink、suffix混在順序を扱います。anchorの小さいbudgetと途中取消は割当器を直接reflectionで呼び、前段parserで停止しただけの確認を避けます。256件の同名anchorは時間計測ではなく候補探索のscan上限で確認します。sentinelは宣言のsource確認だけです。

ZendiatorのFeatures47測定とcleanupの完了後、Bocchiから検証枠を受け取りました。両TFM buildと小さいcanaryのbuild/runが最終sourceで通り、追加のコード修正は不要でした。所有build/test PID 0を先に報告してから、文書とgit操作へ進みます。

今回のCLI確認では、Sonnet 5.5/mediumが通常の英文をESMと誤認してlinkを落とす例を指摘しました。import/exportの開始形を絞り、通常文の再現例を追加しました。未終端候補の再走査をcacheで最適化する提案は、既存scan上限がPartial/LMD003として停止を明示するため今回は採用しません。JSX fragmentは現在の単一profileでunsupported候補として残し、新しい有効化optionは加えません。性能不変やJS全構文の検証は主張しません。

Museの `A,A,A-1,A` の回答は既存規則に合わず、採用していません。期待値 `a,a-1,a-1-1,a-2` は既存の最初の空き番号の規則から決めています。budgetの算術は一致しましたが、CLIの回答を実行検証とは扱いません。Copilotは指定model/effortの一致、tool 0、使用前後の残量と予約分を確認しました。AGYのGemini 3.8 Flash High＋YomiyasuはSUCCESS・1turnで、意味を変えない助詞等の2件を反映しました。

## レビュー修正後の実行結果

両TFM buildは4.88秒、canary buildは6.41秒、runは1.03秒で成功しました。buildは警告・エラー0です。code/escape/entity/link destinationの誤判定防止、opaqueの外側のlink、quoted tag、heading/tableと未解決labelのUnknown対応、ESMと通常英文の区別が通りました。anchorの混在suffix順序、小さいbudget、途中取消、256回の同名割当は製品net10とportable netstandard2の両経路で通りました。既存2入力のsnapshot hashも維持しています。

ParserVersionは 1/fc50f8ddf4016491861255a99e6419d36b7775dd9305344d7187e677d46ddebe です。canonical source 21件に新しいopaque lexerを含め、Git blob bytesとの一致を確認しました。2026-10-08T06:11:19.2807679Z に記録したroot PID・現存する子プロセス・対象taskのdotnet commandを確認し、所有build/test PID 0を記録しました。新規ログと私用証拠はDドライブへ保存し、既存Cドライブの記録を削除・移動していません。以後のコード変更や重い検証は行いません。
