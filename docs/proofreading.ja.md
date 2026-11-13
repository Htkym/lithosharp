# 任意の校正

`LithoSharp.Proofreading` は、Syntamark の構文とソース投影を使って文章を選択します。
Python と Yomiyasu が必要なのは、外部の校正処理を明示的に呼び出す場合だけです。
サイト生成やエディターの起動では、校正処理を開始しません。

アダプターは JSON 形式 2 を使う Yomiyasu 3.x に対応します。
Python の実行ファイルと、インストール済みの `yomiyasu_lint.py` は利用側で選んでください。
LithoSharp はこれらのインストールや検出を行いません。
時間上限を設けず、lint の CLI を `--json --no-words -` で呼び出します。
キャンセルすると、所有するプロセスを停止します。
stdout の上限は UTF-16 コード単位で 4,194,304、stderr の上限は別に 65,536 です。

```csharp
using LithoSharp.Proofreading;
using Syntamark;

var source = new SourceSnapshot("guide.md", "editor-42", markdown);
var selected = MarkdownProofreading.Select(source,
    protectedIdentifiers: ["System.String", "DocumentCatalog"]);
var result = await YomiyasuProofreader.CheckAsync(
    selected, pythonExecutable, lintScript, cancellationToken);
var diagnostics = result.Findings.Select(finding => finding.Diagnostic);
```

選択するのは、見出し、段落、テーブルのセル、リンクのラベルです。
コード、HTML のトークンとブロック、数式、画像のラベル、リンクの宛先、URL は除外します。
インラインコードの外にある API 識別子は、`protectedIdentifiers` に追加してください。
一致の判定はデコードした文字列の ordinal 完全一致です。
`Exclusions` は、各除外理由と、既知の元の範囲を記録します。
セグメントを分け、除外した内容の前後の間隔を保持します。
送信する文章では Markdown の制御文字をエスケープし、デコードしたリテラルのバッククォートや角括弧が、
Yomiyasu で新しいマークアップにならないようにします。

フロントマターは、選択した Markdown プロファイルとは独立して、ネイティブのヘッダースキャナーで分離します。
本文にはそのプロファイルの Body 入力を使います。
構文が欠落または不完全な場合は、校正結果が空で成功したとは扱いません。
対象範囲を表示するときは、`WasRun`、`State`、`ParseStatus`、除外内容を確認してください。

JSON 形式 2 の指摘は、正確な置換範囲ではなく行を示します。
位置は、BOM、CRLF、補助 Unicode 文字、ネイティブ投影を通して、元の Markdown に対応付けます。
精度は最大でも `CoveringTokens` なので、`finding.PreviewReplacement(text)` は自動編集を利用できないと表示します。
メッセージや抜粋を検索して、トークンの位置を作り出しません。

アプリケーションは `selected.PreviewReplacement(segment, start, length, text)` で、
明示的に選んだデコード後のコードポイント範囲に対する置換をプレビューできます。
適用できるのは、ネイティブの正確な対応が一つだけある場合です。
`&amp;` などの実体参照は、トークンを含む精度のままです。
`preview.Apply(currentSnapshot)` は、ソースの識別情報、バージョン、文字列のハッシュ、期待する元の文字列を検証し、
ファイルを書き込まずに新しい文字列を返します。
古いスナップショットや曖昧な範囲は拒否します。

単独のコマンドはプロジェクトを評価せず、ソースを書き換えません。

```text
lithosharp proofread guide.md --python /path/to/python --yomiyasu /path/to/yomiyasu_lint.py --protect System.String --format json
```

JSON には選択したセグメントと診断を含めます。
任意の `--preview segment:start:length --replacement text` は、0 始まりのセグメント番号とコードポイント位置で、
明示した選択範囲をプレビューします。変更は適用しません。

指摘は文章をレビューするための候補です。
技術的な内容、意味の保持、OKF の検証メタデータを保証しません。
