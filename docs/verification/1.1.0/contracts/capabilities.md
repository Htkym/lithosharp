# Tooling capability contract (v1.1.0 / schema 1.0)

この文書は V110-03 で固定した capability 契約と、Editor 向け結果の identity 契約の仕様です。
実装は `src/LithoSharp/ToolingCapabilities.cs`、CLI は `lithosharp capabilities`、wire の見本は
`eng/verification/1.1.0/contracts/capabilities.golden.json` です。実装状況の要約は
`tasks/V110-03.md` にあります。

## 呼び出しと exit code

```powershell
lithosharp capabilities            # 人間向けtext
lithosharp capabilities --format json
```

- project を評価しません。project directory の外、csproj が壊れている directory でも動作し、MSBuild・C#・Node・依存 restore を起動しません。
- `--format json` の stdout は 1つの JSON document です（JSON Lines ではありません）。
- exit code は 0（成功）、2（usage error。`--format json` 指定時は `schemaVersion` 付きの失敗封筒）。capability 判定の失敗そのものは exit code にしません。必須 capability の欠落は利用側が機能を無効化して扱います。

## report の field

| field | 型 | 意味 |
| --- | --- | --- |
| `schemaVersion` | string | report の schema version。現在 `1.0`。major 一致で互換 |
| `success` | bool | report を生成できたか |
| `exitCode` | int | 0 または 1（report 生成失敗時） |
| `tool` | identity | 実行中の tool（`LithoSharp.Tool`） |
| `core` | identity | tool に同梱された Core（`LithoSharp`） |
| `project` | object | project context の解決状態。`resolved`、`coreVersion`、`reason` |
| `contracts` | array | 既知の Tooling 契約（name / maturity / schemaVersion / description） |
| `capabilities` | array | 提供する capability（name / maturity / schemaVersion / description / scope） |
| `error` | string? | 失敗時の説明 |

identity は `name` と `version` を持ちます。**tool に同梱された Core、project が参照する Core、
Language Server に同梱された Core は別の identity として記録し、未解決の値を他の identity で埋めません。**
未解決の `project.coreVersion` は `null` で、tool の version を入れません。

## capability 一覧（schema 1.0、すべて Stable）

| name | scope | 意味 | capability が無い場合のクライアント動作 |
| --- | --- | --- | --- |
| `document-inspection` | `markdown`, `mdx`, `syntax`, `front-matter`, `project-resolution`, `generated-output`, `runtime` | 未保存文書の inspection。`syntax` と `front-matter` は project 無しで実行でき、残りは明示的な project 操作を必要とする | 未保存解析を無効化し、build 済み結果のみ表示する |
| `versioned-snapshot` | `syntax`, `project-resolution` | snapshot が document version と project generation を持ち、stale 結果を破棄できる | 古い結果の表示リスクを説明し、自動更新を無効化する |
| `serve-shutdown` | （なし） | stdin 経由の構造化 serve 停止。重複・実行中・起動途中の停止要求は冪等 | 停止は Ctrl+C のみとし、UI にその制約を表示する |
| `source-route-lookup` | `project-resolution` | project / collection / variant の識別を保った source→route 取得。複数候補は複数返す | 拡張子推測を行わず、route 未取得として表示する |

`scope` の値は前方互換です。未知の scope 値は無視し、既知の scope だけで判定します。

## 交渉規則

- 未知の field と未知の capability 名は無視します（加算的追加のみ行います）。
- 必須 capability の欠落は `ToolingCapabilities.Require` で説明付きに失敗させ、機能を無効化します。
  空配列や推測で代替しません。
- schema version は major 一致で互換です（`ToolingCompatibility.IsCompatible`）。相手が先行 minor
  （例 `1.1`）でも互換とし、未知 field を無視します。major 不一致（例 `2.0`）は拒否します。
- 既存 enum へ値を追加しても、旧 consumer が数値に依存しないよう、report では maturity を文字列で出します。

## Editor 結果の identity 契約（V110-10〜V110-17 で実装）

新しい Editor 向け結果は、少なくとも次の field 名で identity を返します。値の意味は capability の
scope と同じ語彙を使います。

| field | 型 | 意味 |
| --- | --- | --- |
| `workspaceId` | string | workspace/session の識別。process 内で安定 |
| `projectId` | string | 解決済み project の識別。未解決は `null` |
| `documentUri` | string | 文書 URI。大文字小文字を無条件に正規化しない |
| `documentVersion` | int | Editor が付与する文書版数 |
| `projectGeneration` | int | project context の世代。reload で増える |
| `languageKind` | string | `markdown` / `mdx` |
| `analysisStage` | string | `syntax` / `front-matter` / `project-resolution` / `generated-output` / `runtime` |
| `completion` | string | `complete` / `partial` / `canceled` / `failed` |
| `diagnostics` | array | 診断。位置は下記の公開位置規則 |

`analysisStage` が未実行の段階を「診断0件の成功」と表示しません。context が無い場合は
`project-resolution` 以降を未実行として返します。

## 診断と位置の変換契約

- 公開 API / CLI の位置は 1-based 行・列です（既存契約を維持）。
- 内部 `SourceSpan` は UTF-16 code unit・0-based・半開区間です（既存契約を維持）。
- Language Server への変換は 1箇所（V110-14）で行い、LSP は 0-based UTF-16 行列とします。
- severity は Core の `Info` / `Warning` / `Error` を LSP の Information / Warning / Error へ写像します。
  例外文言から独自の診断 ID を作りません。
- 位置を確定できない診断に架空の range を付けません。文書外の診断は project 出力側へ回します。

## この仕様に含まれないもの

- `capabilities` 以外の新 CLI（`serve --control-stdin` は V110-13、project inspection は V110-10）。
- Language Server の transport と実装（V110-14）。LSP 自身の identity は report に加算する別 record とし、
  `tool` / `core` を流用しません。
- Editor 結果 identity の型と実装（V110-10/11/12）。本タスクでは field 名と語彙のみ固定します。
