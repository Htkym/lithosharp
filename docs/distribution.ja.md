# 配布

[English](distribution.md)

何を配り、どこで動き、誰が承認するか。Marketplace公開はBLOCKEDである。
publisherアカウントがないため、packageは身元を名乗らず、workflowも公開できない。

## NuGet package

7 packageを同一version（現行1.0.0）で出す。`LithoSharp`、`LithoSharp.Generators`、
`LithoSharp.Images`、`LithoSharp.Tool`、`LithoSharp.Testing`、`LithoSharp.Mdx`、
`LithoSharp.ProjectTemplates`である。test専用の`LithoSharp.FixtureExtension`は
packしない。`eng/Validate-Package.ps1`が内容・metadata・version整合・Markdig
禁止を検査し、`eng/Test-PackageDistribution.ps1`がpack・検証・隔離feedからの
tool/template導入・packageのみのtemplate site buildを通す。

`v*`系列のtag（`v1.1.0`）だけが`.github/workflows/publish-nuget.yml`を駆動する。
下記の拡張tag系列はこのglobに一致しない。

## 言語server

server（`src/LithoSharp.LanguageServer`、version 1.1.0）はframework-dependentで
出す。`dotnet LithoSharp.LanguageServer.dll`に`deps.json`、`runtimeconfig.json`、
managed依存、RID別native資産、worker source（`worker/*.mjs`、`runtime/`、
`package.json`、`package-lock.json`）を添える。restore済み`node_modules`は
同梱しない。trimming・単一file・AOTはproject fileで無効であり、flag運用に
頼らない。`eng/Test-LspDistribution.ps1`が配置と、初期化・Markdown診断・
Nodeなし/worker未復元時の縮退を検査する。serverはNuGet packageにせず、
VSIXまたは手動導入で運ぶ。

## VSIX

Windows x64・Linux x64・macOS arm64で同一のportable VSIXである。0.1.0で
48文書・119.39 KB、native binary（`.node`/`.dll`/`.so`/`.dylib`検査）なし、
`.local`・secret・log・資格情報・開発用`node_modules`・fixture・testなし。
内容はcompile済みshell、manifest、文書、MDX worker sourceとlockfileである。
`eng/Test-VsixContents.ps1`が`vsce`でpackしてから禁止内容・必須項目・version
整合・publisher不在を検査する。pack（`vsix:pack`）は送信しない。

## MDX workerの復元

restore済みnpm依存はmachine固有の状態なので、VSIXは`node_modules`ではなく
sourceとlockfileを持つ。`LithoSharp: Restore MDX Worker`が同梱sourceを
hash付き拡張storage（`worker-<lockfile12>`）へ写し、`npm ci --ignore-scripts
--no-audit --no-fund`を実行する。利用者project・global npm状態・利用者npm
設定には触らない。`lithosharp.workerDirectory`で上書きでき、変更後は
`LithoSharp: Restart Language Server`が必要である。未復元でもMarkdown診断は
動き、MDXは理由付きで出ない。

## tagと承認

- Core：`v1.1.0`系列 → `publish-nuget.yml`（pack・検証・OIDC push）。
- 拡張：`extension/lithosharp-vscode/0.1.0`系列 → `publish-extension.yml`は
  tag pushでpack・検証のみ行う。公開は手動dispatchと`marketplace`環境承認の
  両方が必要であり、設定済みpublisherがなければ閉じて失敗する。
- `eng/Test-ReleasePipeline.ps1`がtrigger分離・dispatch＋承認gate・
  version/tag整合・pack経路の公開command混入を検査する。

## 監査範囲

NuGet（`dotnet list package --vulnerable`）、npm（拡張とworkerの`npm audit`）、
worker依存license（全MIT）を配布runごとに確認する。結果と例外はtask memoに
残し、常設の「脆弱性0」主張はしない。NuGet分は`THIRD-PARTY-NOTICES.md`が
対象を定める。
