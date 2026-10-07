# Tooling capability contract (v1.1.0 / schema 1.0)

`lithosharp capabilities`が返す機能情報と、ツール・プロジェクト・言語サーバーの識別情報を説明します。

## 呼び出しと exit code

```powershell
lithosharp capabilities            # 人間向けテキスト
lithosharp capabilities --format json
```

- プロジェクトを評価しません。プロジェクトディレクトリの外や、csproj が壊れているディレクトリでも動作し、MSBuild・C#・Node・依存関係の復元を起動しません。
- `--format json` の標準出力は 1つの JSON ドキュメントです（JSON Lines ではありません）。
- プロセスの終了コードは0（成功）、1（処理失敗）、2（使い方の誤り）です。`--format json`指定時の失敗は、capability reportの代わりに`schemaVersion`付きのCLIエラー封筒で返します。必須capabilityがない場合は、利用側でその機能を無効にしてください。

## レポートのフィールド

| field | 型 | 意味 |
| --- | --- | --- |
| `schemaVersion` | string | レポートの schema version。現在 `1.0`。major 一致で互換 |
| `success` | bool | レポートを生成できたか |
| `exitCode` | int | 0 または 1（レポート生成失敗時） |
| `tool` | identity | 実行中のツール（`LithoSharp.Tool`） |
| `core` | identity | ツールに同梱された Core（`LithoSharp`） |
| `project` | object | プロジェクトコンテキストの解決状態。`resolved`、`coreVersion`、`reason` |
| `contracts` | array | 既知の Tooling 契約（name / maturity / schemaVersion / description） |
| `capabilities` | array | 提供する capability（name / maturity / schemaVersion / description / scope） |
| `error` | string? | 失敗時の説明 |

identity は `name` と `version` を持ちます。**ツールに同梱された Core、プロジェクトが参照する Core、
Language Server に同梱された Core は別の identity として記録し、未解決の値を他の identity で埋めません**。
未解決の `project.coreVersion` は `null` で、ツールの version を入れません。

成功したCLI呼び出しではreportの`exitCode`は0です。Core APIの`ToolingCapabilities.CreateReport`に`error`を渡して作る失敗reportでは1になります。CLIの失敗は別形式のエラー封筒で返し、その`exitCode`は処理失敗が1、使い方の誤りが2です。エラー封筒をcapability reportとして扱わず、`success`と`exitCode`を確認してから機能一覧を読んでください。

## capability 一覧（schema 1.0、すべて Stable）

| name | scope | 意味 | capability が無い場合のクライアント動作 |
| --- | --- | --- | --- |
| `document-inspection` | `markdown`, `mdx`, `syntax`, `front-matter`, `project-resolution`, `generated-output`, `runtime` | 未保存文書の検査。`syntax` と `front-matter` はプロジェクトなしで実行でき、残りは明示的なプロジェクト操作を必要とする | 未保存解析を無効化し、ビルド済みの結果のみ表示する |
| `versioned-snapshot` | `syntax`, `project-resolution` | スナップショットが document version と project generation を持ち、古い結果を破棄できる | 古い結果の表示リスクを説明し、自動更新を無効化する |
| `serve-shutdown` | （なし） | stdin 経由の構造化 serve 停止。重複・実行中・起動途中の停止要求は冪等 | 停止は Ctrl+C のみとし、UI にその制約を表示する |
| `source-route-lookup` | `project-resolution` | project / collection / variant の識別を保った source→route 取得。複数候補は複数返す | 拡張子推測を行わず、route 未取得として表示する |

`scope` の値は前方互換です。未知の scope 値は無視し、既知の scope だけで判定します。

## 交渉規則

- 未知のフィールドと未知の capability 名は無視します（加算的な追加のみ行います）。
- 必須 capability の欠落は `ToolingCapabilities.Require` で説明付きで失敗させ、機能を無効化します。
  空配列や推測で代替しません。
- schema version は major 一致で互換です（`ToolingCompatibility.IsCompatible`）。相手が先行 minor
  （例 `1.1`）でも互換とし、未知フィールドを無視します。major 不一致（例 `2.0`）は拒否します。
- 既存 enum へ値を追加しても、古い利用側が数値に依存しないよう、レポートでは maturity を文字列で出力します。

## Editor 結果の identity 契約

新しい Editor 向け結果は、少なくとも次のフィールド名で identity を返します。値の意味は capability の
scope と同じ語彙を使います。

| field | 型 | 意味 |
| --- | --- | --- |
| `workspaceId` | string | ワークスペースやセッションの識別。プロセス内で安定 |
| `projectId` | string? | 解決済みプロジェクトの識別。未解決は `null` |
| `documentUri` | string | 文書 URI。大文字小文字を無条件に正規化しない |
| `documentVersion` | int | Editor が付与する文書版数 |
| `projectGeneration` | int | プロジェクトコンテキストの世代。リロードで増える |
| `languageKind` | string | `markdown` / `mdx` |
| `analysisStage` | string | `syntax` / `front-matter` / `project-resolution` / `generated-output` / `runtime` |
| `completion` | string | `complete` / `partial` / `canceled` / `failed` |
| `diagnostics` | array | 診断。位置は下記の公開位置規則 |

`analysisStage` が未実行の段階を「診断0件の成功」と表示しません。コンテキストがない場合は
`project-resolution` 以降を未実行として返します。

## 診断と位置の変換契約

- 公開 API / CLI の位置は 1-based 行・列です（既存契約を維持）。
- 内部 `SourceSpan` は UTF-16 code unit・0-based・半開区間です（既存契約を維持）。
- Language Server への変換は 1箇所で行い、LSP は 0-based UTF-16 行列とします。
- severity は Core の `Info` / `Warning` / `Error` を LSP の Information / Warning / Error へマッピングします。
  例外文言から独自の診断 ID を作りません。
- 位置を確定できない診断に架空の range を付けません。文書外の診断はプロジェクト出力側へ回します。
