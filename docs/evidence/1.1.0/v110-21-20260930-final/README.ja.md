# 公開証拠：V110-21移行corpus

[English](README.md)

pin済み第三者移行corpus（`20260930-final`）のsanitize済み再実行記録である。
全文trace、workspace checkout、machine logはlocalに残す。範囲判断と再現に
必要なものだけを収める。

- `version-manifest.json`：corpus/run ID、repository commit、package version、
  SDK/Node/worker pin、container digest。
- `run.json`：全体状態とsite別成否一覧。宣言page setを超える同等性は主張しない。
- `sites/<id>.json`：site別pin、command・network・exit・input/output hash付き
  stage一覧、移行判定、候補build/serve summary、手動対応集計、既知制限。
- `routes/<id>-route-oracle.json`：完全なtyped原本route oracle。
- `commands.txt`：正確な再実行command。
- `sanitizer-manifest.json`：適用したroot置換とsecret検査結果（空）。

`Run-MigrationCorpus.ps1`実行後に
`eng/New-EvidenceBundle.ps1 -RunId <run-id> -WorkspaceRoot <workspace>`で
再生成する。machine固有rootは`<repo>`/`<workspace>`に置換し、stage結果と
件数はそのまま残す。secretが残ればsanitizerが失敗する。
