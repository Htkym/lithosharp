# MD-02の実装と確認記録

共通Markdownの新しいparse-only entryとimmutable factsを実装しました。実装基準はMD-01のSHA `aef618b6031ac26485f7b4e457a885aa9de471f5`です。対象の両TFM buildと小さいcanaryは、Unknown修正後の最終sourceでも成功しました。MD-02のcommit/push後はBocchiの実diffレビューを待ち、MD-03へは進みません。

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

folded/literal YAML、indented code、block math等でdecoded対応をまだ確定できない場合はUnknown segmentとLMD006を返し、Partialとします。raw HTML/MDXを含むparagraphはopaque regionとして原文spanを残します。公開Capabilitiesもdecoded mappingをPartial、HTML/MDX投影をUnsupportedとします。今回の実装を全構文の完全なtext投影とは記述しません。

入力上限はcopy/hash/parseの前に、invalid Unicodeはstrict UTF-8 hashの前に検査します。途中のscan/depth/output上限はLMD003とPartialで示します。出力ではresource診断用に1件を予約し、上限前に停止する場合があります。原文の対応を作れなかったcollectionをCompleteとは扱いません。

自前scannerのloopには再走査の計数と取消確認を入れ、string/regex操作のoperandは保守的に計数します。loopの固定chargeは複数回のUTF-16参照を上から数えるため、実際に読んだ最小単位数より多くなる場合があります。YamlDotNet内部の全命令数、CPU時間、実heapの固定上限は保証しません。取消や不変条件違反を入力診断へ変換しません。

## 版と互換性

ParserVersionはcanonical source・dependency/profile/options規範JSONのGit blob bytesから作りました。生成stamp自身はhash対象から除外しています。`md02-canonical-source.json` は開発中のsource metadataで、sourceCommit・componentVersion・artifactは未発行です。MD-05でpackするcommitと固定版のpair manifestを確定する必要があります。

旧siteのmissing/unclosed frontmatter fallback、LSM診断、OutputLevel、SourceHash/SemanticHash、描画処理はMD-03の対象です。MD-02では旧parserのspanとgrammarを維持し、新entry専用metadataを追加しました。新parse cache、runtime/inspection adapter、compiler hostへのpair供給はまだ実装していません。

MD-01のexact SHAに対するCI検索はrun 0件で、未開始でした。成功とは記録しません。MD-01でMatchAtをSpan.SequenceEqualからCompareOrdinalへ変更した影響はnet10にも及びます。性能は未測定で、速度不変を主張しません。性能観測は後続の測定枠で扱います。MD-02で追加したcontext=nullの分岐や位置metadataの処理も未測定です。旧entryの性能不変を主張しません。

## 小さい確認

既存のportability console canaryに、BOM/CRLF/YAML、entity/escape/emoji、prefix gap、nested heading/reference/fence、setext、slug、Partial/Failed、identity/options、上限と取消の短い確認を加えました。YamlDotNet 18.1.0のMark.IndexとEndは、emoji前後のeventを実行してUTF-16の半開区間と確認しました。package版18.1.0のロード済みassembly versionは18.0.0.0、informational versionは18.1.0でした。後続のloaded identity guardで、package版をassembly versionと単純に等値比較しません。

対象のrestore、Markdown単体の両TFM build、既存console canaryのbuild/runだけを実行しました。full solution/test、benchmark、coverage、pack、公開は実行しません。正確なcommand・終了状態・未実行理由は `md02-verification.json` へ記録します。

## CLIの確認

Muse Spark 1.3はUTF-16の位置計算2件を机上で確認し、一致しました。CopilotはSonnet 5.5/mediumを明示し、toolなしでmappingとoptionsの実ソースだけをレビューしました。空Atomic tokenのguardと未投影textのUnknown queryを修正しました。出力の1件予約は上限を使い切る保証ではなく、診断を含む件数を上限内に保つための保守的な停止条件として維持します。正の整数1以上という採用契約は変更しません。

Copilotの使用前後に残量を確認し、重要レビュー用の予約分を確保できることを確認しました。累計とsession usageの生値はローカル記録に保持し、直後の累計差0を無料とは扱いません。実応答model/effortが指定に一致し、Auto・tool・追加委任は使っていません。AGYの最新一覧で確認したGemini 3.8 Flash High＋YomiyasuはSUCCESS、1turnで、意味を保つ日本語3件の修正を反映しました。Fast tierは使っていません。これらはcompileやparser実行の証拠ではありません。

## 最終確認とレビュー待ち

frontmatterの走査途中で上限へ達した場合に、未確認のStateが既定値のAbsentになっていた点をUnknownへ修正しました。Partial/Failedでまだ判定できないfactsを欠如確定と扱いません。J-01の5つの入力状態の意味は維持し、未完了の表現を追加しました。短いYAML途中停止の確認とstrict Unicode失敗の確認も通っています。

初回の両TFM buildは4種類のcompileエラーとnullable警告で失敗しました。collectionの型変換、string operand、guard後のcontext参照を修正し、再buildで通りました。その後、reference targetの照合を辞書参照にして最初の定義を維持し、opaque候補の再走査もbudgetへ計上しました。最後のUnknown修正後にも同じ対象を確認しました。失敗と途中のPASSを最終sourceの証拠に混ぜず、全commandの履歴をJSONへ残しています。

最終の両TFM buildは4.11秒、canary buildは6.10秒、runは0.97秒でした。両buildは警告・エラー0です。最初のrestoreは2.22秒でした。既存2入力の構造/span/referenceのsnapshot hashはMD-01と同じで、小さい既存描画、再実行一致、Markdig非ロードも維持されています。

2026-10-08T05:19:01.1680388Zに、記録したroot PID・現存する子プロセス・taskのcommandに一致する所有build/test PID 0を確認して測定枠を返しました。共有compilerとMSBuild node reuseは無効にし、他タスクのプロセス停止は行っていません。以後は文書とgit操作だけです。

MD-02の最終sourceは確認済みで、feature/2.0.0へのcommit/push後にBocchiの実diffレビューを待ちます。MD-03、mainへのmerge、PR、tag、pack、公開には進みません。
