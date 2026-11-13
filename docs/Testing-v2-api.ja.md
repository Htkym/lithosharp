# Testing v2 のスナップショットとクエリの契約

LithoSharp.Testing 3.0.0 は、v2 の読み取り専用スナップショット API を使います。
1.x の変更可能な第三者製 DOM のゲッターを、LithoSharp が所有するツリーに置き換えます。
既存の Parse、RenderComponent、RenderLayout、AssertElement/Text/Attribute/Meta/Link/Image と、
破棄できる SiteTestDocument による所有の方法は維持します。
Document の削除はソース互換性とバイナリ互換性を破る変更です。古い DOM 型は模倣しません。
導入とサイト全体のテストは、[サイトのテスト](testing.ja.md)から始めてください。

Testing と、その Core 依存先には、AngleSharp パッケージの依存関係やアセンブリ参照はありません。
製品は独自の HTML パーサーとクエリのスナップショットを使います。

```csharp
using LithoSharp.Testing;
using var page = SiteTestDocument.Parse("<main><p class='lead'>A &amp; B</p></main>");
var first = page.Snapshot.Query("main > p.lead");       // null if absent
var text = first?.TextContent;                         // "A & B"
var all = page.Snapshot.QueryAll("main p");             // unique, read-only, tree order
page.AssertText("main > p", "A & B");
```

## 所有とデータ

SiteTestDocument.Snapshot は HtmlTestDocument です。
Query は HtmlTestElement?、QueryAll は IReadOnlyList<HtmlTestElement> を返します。
一致がない場合は null または空の一覧であり、アサーションの不一致は引き続き SiteTestException です。
一覧は変更可能な配列を公開しません。所有者が有効な間、並行した読み取りは独立しています。
Dispose は何度呼んでも同じ効果で、所有者、保持したスナップショット、保持した要素を無効にします。
その後の操作は ObjectDisposedException を投げます。
保持した不変の一覧の Count などのメタデータは読めますが、要素のアクセサーは使用を拒否します。
どの表示も、保持している間は所有するツリーのメモリを保持します。
クエリと同時に Dispose すると、クエリが停止する場合があります。並行した呼び出し全体が、不可分のトランザクションではありません。
永続的なクエリキャッシュや、独立したスナップショットの生存期間はありません。

TagName と NamespaceUri は、正規化して解析したローカル名と名前空間です。
Children、Parent、PreviousSibling、NextSibling は、テキストやコメントを飛ばした要素の関係です。
SourceStart と SourceLength は生の UTF-16 の範囲です。Line と Column は、生の行番号と UTF-16 の列を 1 始まりで表します。
合成したノードの位置は null です。
GetAttribute(name, namespaceUri = null) は、デコードした値または null を返します。
名前空間の null は通常の属性を選びます。xlink:href には XLink の URI を使います。

HTML のタグ名と属性名は ASCII の大文字・小文字を区別せず、外来の名前は大文字・小文字を保持します。
ID とクラスは、標準モードでは ordinal、quirks モードでは ASCII の大文字・小文字を区別せずに比較します。
属性値の照合には、[HTML の ASCII で大文字・小文字を区別しない属性の表](https://html.spec.whatwg.org/multipage/semantics-other.html#case-sensitivity-of-selectors)、
または明示した i/s フラグを使います。カルチャや Unicode の大文字・小文字の変換は使いません。

TextContent は、script と style も含め、子孫の正確なデコード後の文字列です。
表示・非表示の規則や、空白の折り畳みは適用しません。innerText や検索用の文字列ではありません。
文書の TextContent は、つながったツリーを連結し、Title は HTML の ASCII 空白を折り畳みます。
文書へのクエリは html を含み、:scope は html を指します。
要素へのクエリは子孫だけを返し、:scope はその要素を指します。
そのため Query(":scope") は null、Query(":scope > p") は子を選択します。
セレクターは、要素クエリのルートの外にある祖先も検査できますが、返すのは子孫だけです。

Template の内容は、独立した不活性なフラグメントになり、通常のクエリ、Children、TextContent から除外します。
明示的に調べる場合は、TemplateContent がフラグメントの要素ルートを公開します。
そのルートには要素の Parent がなく、子孫を公開できます。
外来コンテンツにも文書解析と同じ上限付き HTML パーサーを使います。

## 対応するセレクターとエラー

上限付きの再帰下降文法は、[Selectors Level 4](https://www.w3.org/TR/2026/WD-selectors-4-20260122/) と
[CSS Syntax Level 3](https://www.w3.org/TR/css-syntax-3/) を参照します。完全な CSS やブラウザーとの同等性は主張しません。

対応する構文は、型・全称セレクター、ID・クラス、属性の存在、`=`・`~=`・`|=`・`^=`・`$=`・`*=` の演算子、
引用符あり・なしの値、i/s フラグ、子孫・子・隣接兄弟・一般兄弟、セレクターリスト、:scope、
:first-child・:last-child・:only-child、:nth-child(An+B)、入れ子の複合セレクターリストを持つ :not・:is・:where、
CSS エスケープ、Unicode 識別子、引用文字列の改行継続、コメント、符号付き An+B、odd/even です。
エスケープした An+B の名前トークンにも対応します。

コメントは、単純セレクターの間や空白の境界で使えますが、名前トークンや名前空間・演算子の区切りをつなぎ合わせません。
属性の i/s フラグは引用値や区切りのコメントの直後にも置けます。

:is と :where のすべての枝を、CSS の不正な枝を許すリストとは異なり、厳密に検証します。
接頭辞のない型と *|name は、任意の名前空間に一致します。|name は空の名前空間です。
通常の属性と [|name] は名前空間なし、[*|name] は任意の名前空間に一致します。
名前付きの接頭辞と @namespace の宣言は未対応です。GetAttribute は名前空間の URI を明示した読み取りに対応します。

ワイルドカードはエスケープしていない `*` トークンだけです。
`\*` と `\2a` はリテラルの名前なので、その名前の型セレクターは通常の HTML 要素に一致しません。
一方、`[\*]` は `*` という属性を読みます。
名前空間の接頭辞に使う、エスケープした `*` は名前付きの接頭辞のままなので、HtmlSelectorUnsupportedException を投げます。

:has()、疑似要素、操作・フォーム・ブラウザー状態に関するもの、一覧にない疑似クラス、:nth-child(... of selector) は未対応です。
変更、シリアル化、スクリプト、任意の Web API は公開しません。

上限は、セレクター 4096 UTF-16 単位、外側を含むリストの深さ 16、リストごとの枝 64、
複合セレクターの合計 256、連鎖ごとの複合セレクター 64、An+B の大きさ Int32.MaxValue、
クエリごとの照合操作 4,000,000 です。Query と QueryAll は CancellationToken を伝えます。
HTML 解析は共通の既定の上限を使い、スナップショットを公開する前に、Partial または Failed を InvalidOperationException で拒否します。
未対応の HTML の分岐は Partial のままです。

不正なセレクターは HtmlSelectorSyntaxException、認識した未対応機能と上限は HtmlSelectorUnsupportedException です。
null または空白のセレクターは、標準の引数例外を維持します。
有効で結果が 0 件のセレクターは、すべてのエラーと区別します。
対応範囲は [selector-support.json](selector-support.json)、API の変更は [Testing-api-diff.json](Testing-api-diff.json)に記録します。

## 移行

| 以前の読み取り | v2 の読み取り |
|---|---|
| Document.QuerySelector(selector) | Snapshot.Query(selector) |
| Document.QuerySelectorAll(selector) | Snapshot.QueryAll(selector) |
| TextContent / GetAttribute(name) | 同じ要素の読み取り。属性の名前空間 URI は任意。 |
| Document.Title | Snapshot.Title |
| ParentElement / Children / PreviousElementSibling / NextElementSibling | Parent / Children / PreviousSibling / NextSibling |
| DOM のソース参照 | null 許容の SourceStart/SourceLength/Line/Column |
| DOM の変更、第三者型へのキャスト・拡張 | ソースや描画を変えて再度 Parse、または明示的なブラウザー E2E。 |

DOM の構文例外は HtmlSelectorSyntaxException、未対応機能は HtmlSelectorUnsupportedException になります。
対応するクエリでは、最初の一致とツリー順の動作を維持します。
以前のソースやバイナリの DOM 利用側は、単純な名前変更だけでは移行できません。利用者が所有する依存関係は削除しません。

CLI は、編集前に、古いメタデータにバインドされた読み取りを報告できます。

```sh
lithosharp migrate testing --dry-run --source Tests.cs --reference /isolated/1.1/LithoSharp.Testing.dll --reference /isolated/1.1/AngleSharp.dll --format json
```

入力は、明示した UTF-8 の C# ファイルとメタデータ参照です。
プロジェクト、ビルドターゲット、ソースジェネレーター、スクリプト、ファクトリ、ゲッター、利用者のアセンブリを実行しません。
古いアセンブリは PE メタデータとして読み、CLI のランタイムへの読み込みや v2 の依存グラフへの追加は行いません。
ソースファイルや依存関係を編集しません。

既存の JSON の外側の構造は、正確な入力バイト列の SHA256 を持つ `migration.sources` と `migration.references`、および `findings` を含みます。
各指摘は、元の UTF-16 の `start` と `length`、1 始まりの行と列、含まれる式、分類、理由、任意のプレビューを持ちます。
位置には元の UTF-8 BOM 文字を含め、ハッシュには BOM と CRLF のバイト列を含めます。
`ReadCandidate` は、対応する直接の読み取りの名前変更プレビューです。
戻り値の型、null、順序、例外、所有者の生存期間を確認する必要がある提案です。
`Manual` は、変更、キャスト、別名や外へ渡す DOM、独自の使用、未対応のセレクターを対象にします。
`Unknown` は、バインドの欠落や定数でないセレクターを対象とし、推測した置換を持ちません。
定数のセレクターは実際の v2 のクエリ文法で確認します。

`LSA1501` は、正確にバインドされた古いゲッターに対する Warning のままで、通常の pragma・生成コードのポリシーも維持します。
CLI の指摘は、抑制されたコードや生成コードも含め、明示したすべての入力のバインドされたシンボルを独立して検査します。
指摘の `diagnosticId` は、移行ルールとの対応を示し、Warning が発行されたとは主張しません。
利用者の似た API と、すでに v2 のスナップショットは除外します。
v2 で削除された `Document` アクセスは、古い API の Warning ではなく、Unknown のバインドを持つコンパイルエラーです。
インデックスを使う DOM では、外側の式を保持して手動確認を要求し、インデクサー越しの変更やキャストも含めます。
終了コード 0 は、コンパイルエラーなしで静的解析を終えたこと、1 はコンパイラー・入力の失敗、2 は不正な使用方法です。
明示したソースの報告は、自動変換を証明せず、DOM の別名を追跡しないため、`migration.coverage` は Partial のままです。
適用やプロジェクトのオプションはありません。

`ReadCandidate` には、通常のローカル変数の初期化子で、対応する読み取りの連鎖を直接使う必要があります。
認識しない外側の演算子、呼び出し、ほかの文脈は、`outer_use_not_supported` の Manual です。
内側の読み取りが対象でも、外側の式が候補になるわけではありません。

以前の SetAttribute や TextContent の変更は、入力やテンプレートを明示的に変えます。

```csharp
using LithoSharp.Testing;
using var original = SiteTestDocument.Parse("<p class=expected>Expected</p><a href='/good'>Good</a>");
original.AssertText("p", "Expected");
original.AssertAttribute("p", "class", "expected");
original.AssertLink("/good");
using var changed = SiteTestDocument.Parse("<p class=changed>Changed</p><a href='/bad'>Bad</a>");
// Previous text/class/link expectations now throw SiteTestException.
```

Parse はスクリプトを無効にした文書 HTML を受け付けます。フラグメントやスクリプトモードのオプションは公開しません。
