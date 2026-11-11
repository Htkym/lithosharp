# Toolingの機能情報と互換性（schema 1.0）

[English](tooling-capabilities.md)

`lithosharp capabilities`が返す機能情報と、ツール・プロジェクト・言語サーバーの識別情報を説明します。

## 呼び出しと exit code

```powershell
lithosharp capabilities            # 人間向けテキスト
lithosharp capabilities --format json
```

- プロジェクトを評価しません。プロジェクトディレクトリの外や、csproj が壊れているディレクトリでも動作し、MSBuild・C#・Node・依存関係の復元を起動しません。
- `--format json` の標準出力は 1つの JSON ドキュメントです（JSON Lines ではありません）。
- プロセスの終了コードは0（成功）、1（処理失敗）、2（使い方の誤り）です。`--format json`指定時の失敗はcapability reportの代わりに`schemaVersion`付きのCLIエラーenvelopeで返します。必須capabilityがない場合は、利用側でその機能を無効にしてください。

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

成功したCLI呼び出しではreportの`exitCode`は0です。Core APIの`ToolingCapabilities.CreateReport`に`error`を渡して作る失敗reportでは1になります。CLIの失敗は別形式のエラーenvelopeで返し、その`exitCode`は処理失敗が1、使い方の誤りが2です。エラーenvelopeをcapability reportとして扱わず、`success`と`exitCode`を確認してから機能一覧を読んでください。

## Coreのcapability一覧（schema 1.0、すべてStable）

| name | scope | 意味 | capability が無い場合のクライアント動作 |
| --- | --- | --- | --- |
| `document-inspection` | `markdown`, `mdx`, `syntax`, `front-matter`, `project-resolution`, `generated-output`, `runtime` | 未保存文書の検査。`syntax` と `front-matter` はプロジェクトなしで実行でき、残りは明示的なプロジェクト操作を必要とする | 未保存解析を無効化し、ビルド済みの結果のみ表示する |
| `versioned-snapshot` | `syntax`, `project-resolution` | スナップショットが document version と project generation を持ち、古い結果を破棄できる | 古い結果の表示リスクを説明し、自動更新を無効化する |
| `serve-shutdown` | （なし） | stdin 経由の構造化 serve 停止。重複・実行中・起動途中の停止要求は冪等 | 停止は Ctrl+C のみとし、UI にその制約を表示する |
| `source-route-lookup` | `project-resolution` | project / collection / variant の識別を保った source→route 取得。複数候補は複数返す | 拡張子推測を行わず、route 未取得として表示する |

Core APIの`ToolingCapabilities.CreateReport`は上記の4機能を返します。Tool 2.2.0の`capabilities`コマンドはさらに次のPreview機能を加えます。Coreの一覧とCLIが返す一覧を混同しないでください。

| name | maturity | scope | 動作 |
| --- | --- | --- | --- |
| `preflight-static-inputs` | `Preview` | `markdown`, `mdx` | 明示した入力だけを、プロジェクトを評価せずに検査します。MDXには復元済みの解析用workerが必要です。 |
| `preflight-trusted-catalog` | `Preview` | `trusted-catalog` | 明示的にコンパイルし、信頼するfactoryとcatalogを検査します。描画しません。 |

どちらもschemaは`1.0`です。このreportだけでは選択したプロジェクトの対応機能は確定しません。trusted preflightはfactory assemblyが参照するCoreを`projectCoreVersion`として別途報告しますが、そのCoreの全機能や将来の描画結果を保証するものではありません。機能の有無は実際のreportから判定し、バージョンだけで推測しないでください。[実行範囲と報告形式](../src/LithoSharp.Tool/README.md#preflight)を参照してください。

`scope` の値は前方互換です。未知の scope 値は無視し、既知の scope だけで判定します。

言語サーバーのinitialize応答は独自の`capabilities.experimental.lithosharp`を使います。`markdownInspection: true`、`mdxInspection: "on-demand-worker"`を通知し、`preflightStaticInputs`と`preflightTrustedCatalog`はどちらも`false`です。Tool専用のコマンドを言語サーバーへ送らないでください。`maturity`は文字列として扱い、Coreのenumにある値だけを前提にしないでください。

## 交渉規則

- 未知のフィールドと未知の capability 名は無視します（加算的な追加のみ行います）。
- 必須 capability の欠落は `ToolingCapabilities.Require` で説明付きで失敗させ、機能を無効化します。
  空配列や推測で代替しません。
- schema version は major 一致で互換です（`ToolingCompatibility.IsCompatible`）。相手が先行 minor
  （例 `1.1`）でも互換とし、未知フィールドを無視します。major 不一致（例 `2.0`）は拒否します。
- 既存 enum へ値を追加しても、古い利用側が数値に依存しないよう、レポートでは maturity を文字列で出力します。

## スナップショットの識別と検証状態

すべての編集機能が同じ結果形式を返すわけではありません。連携するインターフェイスの実際の項目を使ってください。

- `DocumentWorkspace.Id`はworkspaceを識別します。`InspectVersionedAsync`は文書のバージョンとprojectの世代を受け取り、古くなった処理を拒否します。返す`DocumentInfo`はコンテンツの情報と`ProjectStatus`を持ちますが、要求時の識別情報をすべて複製するわけではありません。呼び出し側でも要求の識別情報を保持してください。
- `ProjectInspectionSnapshot.ToJson()`は`schemaVersion`、`projectId`、`projectGeneration`、`coreVersion`、`collection`、`language`、`schema`、`version`、`locale`、`acquiredAt`、`routes`を含みます。明示的に取得したデータであり、project assemblyの読み込みを指示するものではありません。
- LSPの診断は`uri`、`version`、`diagnostics`、`lithosharpOpenGeneration`、`lithosharpContextGeneration`を持ちます。close時には文書のバージョンを付けずに診断を消去します。別通知の`lithosharp/analysisStatus`は`uri`、`version`、`language`、`state`（`ready`または`unavailable`）、`message`と、同じ2つの世代項目を持ちます。[言語サーバーのプロトコル](language-server.md)を参照してください。

任意項目の`projectStatus`、`schemaName`、`schemaScope`、`projectId`、`projectGeneration`、`contextAcquiredAt`は、適用したcontextと検査可能な範囲を示します。範囲の通知がない場合は不明として扱ってください。MDXではスキーマ名からMarkdownのバインド対応を推定しません。取得日時だけでは、すべての入力が最新と一致することは保証できません。

`NoContext`では構文を検査できますが、プロジェクトに結び付くスキーマやルートは確定できません。`Resolved`、`Ambiguous`、`Unresolved`、`Draft`は明示した文脈でのルート解決状態であり、サイト全体の妥当性ではありません。snapshotで結び付けるのは組み込みスキーマだけです。独自C#スキーマはコンパイラーとサイトビルドで検証します。

`Partial`や取得不能の解析情報、カタログの`Unknown`を「検査済み、エラーなし」と表示しないでください。`LSM013`は解析を完了できなかったことを示します。LSPの`ready`は構文解析が利用できるという意味であり、ビルドや生成物検査を実行したことや、エラーがないことを意味しません。

## 診断と位置の変換契約

- 公開 API / CLI の位置は 1-based 行・列です（既存契約を維持）。
- 内部 `SourceSpan` は UTF-16 code unit・0-based・半開区間です（既存契約を維持）。
- Language Server への変換は 1箇所で行い、LSP は 0-based UTF-16 行列とします。
- severity は Core の `Info` / `Warning` / `Error` を LSP の Information / Warning / Error へマッピングします。
  例外文言から独自の診断 ID を作りません。
- 位置を確定できない診断に架空の range を付けません。文書外の診断はプロジェクト出力側へ回します。
